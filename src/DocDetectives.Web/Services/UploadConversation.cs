using System.Text;
using System.Text.RegularExpressions;
using DocDetectives.Application.Ingestion;
using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;

namespace DocDetectives.Web.Services;

/// <summary>One assistant message in the chat: Markdown text, suggested replies, and a spinner while work runs.</summary>
public sealed record AssistantMessage(string Markdown, IReadOnlyList<string> Chips, bool Working = false)
{
    public static AssistantMessage Text(string markdown) => new(markdown, []);
    public static AssistantMessage Busy(string markdown) => new(markdown, [], Working: true);
}

/// <summary>What happened to a typed reply.</summary>
public enum ReplyOutcome
{
    /// <summary>The reply answered or changed the open question.</summary>
    Handled,

    /// <summary>The reply is a question about the manual: answer it, then ask the open question again.</summary>
    Question,

    /// <summary>Not understood: show <see cref="UploadConversation.HintAsync"/>.</summary>
    NotUnderstood
}

/// <summary>
/// Drives the review of one uploaded document as a conversation: the same steps and the same
/// <see cref="IngestionJobService"/> calls as the Ingest page, asked as questions and answered by typed replies.
/// Proposals can be changed in plain words before they are submitted. Steps decided elsewhere are skipped.
/// </summary>
public sealed class UploadConversation : IDisposable
{
    private enum Step { Processing, Failed, Matches, Proposal, CharacteristicsOffer, CharacteristicsConfirm, Plans, Done }

    private const int MaxPlanLinesShown = 30;

    private readonly IngestionJobService _jobs;
    private readonly IAssetCatalogGateway _catalog;
    private readonly Dictionary<string, string> _assetNames = new(StringComparer.Ordinal);
    private readonly List<string> _summary = [];
    private Step _step = Step.Processing;
    private string _progress;
    private List<AssetMappingResult> _matches = [];
    private JobPlan? _plan; // the original draft of the open PM question (its Id keys decisions and edits)

    public UploadConversation(IngestionJobService jobs, IAssetCatalogGateway catalog, IngestionJob job)
    {
        _jobs = jobs;
        _catalog = catalog;
        Job = job;
        _progress = ProgressText();
        _jobs.Changed += OnJobsChanged;
    }

    public IngestionJob Job { get; private set; }

    public string FileName => Job.FileName;

    /// <summary>Raised (on a background thread) when the progress text changes or processing ends.</summary>
    public event Action? Changed;

    /// <summary>Progress of the processing, for the message that is updated in place.</summary>
    public string Progress => _progress;

    public bool IsProcessing => _step == Step.Processing && Job.Status is IngestionJobStatus.Queued or IngestionJobStatus.Running;

    /// <summary>Processing ended and the review has not been started yet.</summary>
    public bool ReadyToReview => _step == Step.Processing && !IsProcessing;

    public bool AwaitingReply => _step is not (Step.Processing or Step.Done);

    public bool IsFinished => _step == Step.Done;

    /// <summary>The manual to answer questions from (null for inspection documents, which are not indexed).</summary>
    public (string ManualId, string FileName)? Manual =>
        Job.Report is { } report ? (report.ExtractedManual.ManualId, Job.FileName) : null;

    /// <summary>Called once processing has ended: posts the result and the first question.</summary>
    public async Task StartReviewAsync(Func<AssistantMessage, Task> post, CancellationToken cancellationToken)
    {
        if (Job.Status == IngestionJobStatus.Failed)
        {
            _step = Step.Failed;
            await post(new AssistantMessage(
                $"I could not process **{FileName}**: {Job.Error}\n\nReply **retry** to try again, or **skip**.", ["retry", "skip"]));
            return;
        }

        var doc = Job.Document!;
        var kind = doc.Classification.DocumentType == DocumentType.Inspection ? "an inspection document" : "a manual";
        var seconds = Job.Duration is { } d ? $" in {d.TotalSeconds:0} s" : "";
        var sections = Job.Report is not { } r ? ""
            : r.AlreadyIndexed ? $", already indexed ({r.EmbeddedChunkCount} sections) so not indexed again"
            : $", {r.EmbeddedChunkCount} section(s) indexed";
        await post(AssistantMessage.Text($"Processed **{FileName}**{seconds}: {kind}{sections}."));

        await AdvanceAsync(Step.Matches, post, cancellationToken);
    }

    /// <summary>Handles a typed reply to the open question: an answer, an undo, or a change in plain words.</summary>
    public async Task<ReplyOutcome> ReplyAsync(string text, Func<AssistantMessage, Task> post, CancellationToken cancellationToken)
    {
        var reply = ReplyParser.Parse(text);

        if (reply?.Kind == ReplyKind.Undo)
        {
            await UndoAsync(post, cancellationToken);
            return ReplyOutcome.Handled;
        }

        if (reply is not null && await AnswerAsync(reply, post, cancellationToken))
        {
            return ReplyOutcome.Handled;
        }

        // "Can you set the frequency to 6 months?" is a change, not a question about the manual.
        if (reply is null && EditTarget is not null && ReplyParser.LooksLikeEdit(text))
        {
            return await EditAsync(text, post, cancellationToken) ? ReplyOutcome.Handled : ReplyOutcome.NotUnderstood;
        }

        if (ReplyParser.LooksLikeQuestion(text))
        {
            return ReplyOutcome.Question;
        }

        // Anything else on a proposal is a change in plain words ("set the frequency to 6 months").
        return reply is null && await EditAsync(text, post, cancellationToken)
            ? ReplyOutcome.Handled
            : ReplyOutcome.NotUnderstood;
    }

    /// <summary>The open question again, e.g. after answering a question about the manual in between.</summary>
    public async Task<AssistantMessage> RepeatQuestionAsync(CancellationToken cancellationToken) =>
        await QuestionAsync(cancellationToken) ?? AssistantMessage.Text("");

    /// <summary>Shown when a reply was not understood.</summary>
    public async Task<AssistantMessage> HintAsync(CancellationToken cancellationToken)
    {
        var question = await QuestionAsync(cancellationToken);
        var expected = _step switch
        {
            Step.Matches => "numbers like `1, 3`, **all** or **none**",
            Step.Plans => "**submit**, **no**, **approve all**, **skip all**, or tell me what to change",
            Step.Proposal or Step.CharacteristicsConfirm => "**submit**, **no**, or tell me what to change",
            Step.Failed => "**retry** or **skip**",
            _ => "**yes** or **no**"
        };
        return new AssistantMessage(
            $"Sorry, I didn't get that. Please answer with {expected}.\n\n{question?.Markdown}",
            question?.Chips ?? []);
    }

    // ---------- answers ----------

    private async Task<bool> AnswerAsync(Reply reply, Func<AssistantMessage, Task> post, CancellationToken cancellationToken)
    {
        switch (_step)
        {
            case Step.Failed:
                if (reply.Kind is ReplyKind.Retry or ReplyKind.Yes)
                {
                    _step = Step.Processing;
                    Job = _jobs.Enqueue(Job.FilePath);
                    _progress = ProgressText();
                    await post(AssistantMessage.Text($"Trying **{FileName}** again."));
                    return true;
                }
                if (reply.Kind is ReplyKind.No or ReplyKind.SkipAll or ReplyKind.None)
                {
                    _step = Step.Done;
                    await post(AssistantMessage.Text($"Skipped **{FileName}**."));
                    return true;
                }
                return false;

            case Step.Matches:
            {
                HashSet<string>? selected = reply.Kind switch
                {
                    ReplyKind.All => _matches.Select(m => m.AssetId).ToHashSet(StringComparer.Ordinal),
                    ReplyKind.None or ReplyKind.No => [],
                    ReplyKind.Yes => Job.DefaultSelection(),
                    ReplyKind.Numbers when reply.Numbers.All(n => n >= 1 && n <= _matches.Count) =>
                        reply.Numbers.Select(n => _matches[n - 1].AssetId).ToHashSet(StringComparer.Ordinal),
                    _ => null
                };
                if (selected is null) return false;

                await RunAsync(post, () => _jobs.SaveMatchSelectionAsync(Job, selected, cancellationToken), "save the links");
                var linked = _matches.Where(m => Job.LinkedAssetIds.ContainsKey(m.AssetId)).Select(m => AssetLink(m.AssetId)).ToList();
                var note = linked.Count == 0 ? "Not linked to any existing asset." : $"Linked to {string.Join(", ", linked)}.";
                _summary.Add(note);
                await post(AssistantMessage.Text(note));
                await AdvanceAsync(Step.Proposal, post, cancellationToken);
                return true;
            }

            case Step.Proposal:
                if (reply.Kind == ReplyKind.Yes)
                {
                    await post(AssistantMessage.Busy("Creating the assets…"));
                    if (await RunAsync(post, () => _jobs.ApproveAssetProposalAsync(Job, cancellationToken), "create the assets"))
                    {
                        var created = Job.ApprovedProposalAssets;
                        var note = created.Count == 0
                            ? "Approved; no new assets were needed."
                            : $"Created {created.Count} asset(s): {string.Join(", ", created.Take(5).Select(a => AssetLink(a.Id, a.Name)))}{(created.Count > 5 ? ", …" : "")}.";
                        _summary.Add(note);
                        await post(AssistantMessage.Text(note));
                    }
                }
                else if (reply.Kind == ReplyKind.No)
                {
                    _jobs.RejectAssetProposal(Job);
                    _summary.Add("New assets not created.");
                    await post(AssistantMessage.Text("OK, nothing is created."));
                }
                else return false;

                await AdvanceAsync(Step.CharacteristicsOffer, post, cancellationToken);
                return true;

            case Step.CharacteristicsOffer:
                if (reply.Kind == ReplyKind.No)
                {
                    await post(AssistantMessage.Text("Skipped the characteristics."));
                    await AdvanceAsync(Step.Plans, post, cancellationToken);
                    return true;
                }
                if (reply.Kind != ReplyKind.Yes) return false;

                await post(AssistantMessage.Busy("Extracting characteristics… this takes about half a minute."));
                await RunAsync(post, () => _jobs.ExtractCharacteristicsAsync(Job, cancellationToken), "extract the characteristics");

                if (Job.CharacteristicDraft is { Extracted.Count: > 0 })
                {
                    _step = Step.CharacteristicsConfirm;
                    await post((await QuestionAsync(cancellationToken))!);
                }
                else
                {
                    await post(AssistantMessage.Text(Job.CharacteristicDraft?.Note ?? "No characteristics found."));
                    await AdvanceAsync(Step.Plans, post, cancellationToken);
                }
                return true;

            case Step.CharacteristicsConfirm:
                if (reply.Kind == ReplyKind.Yes)
                {
                    if (Job.CharacteristicDraft is not { WouldFill.Count: > 0 })
                    {
                        await post(AssistantMessage.Text("Nothing to save: no empty characteristics would be filled."));
                    }
                    else if (await RunAsync(post, () => _jobs.SaveCharacteristicsAsync(Job, cancellationToken), "save the characteristics"))
                    {
                        var saved = Job.SavedCharacteristics?.Note ?? "Saved the characteristics.";
                        _summary.Add(saved);
                        await post(AssistantMessage.Text(saved));
                    }
                }
                else if (reply.Kind == ReplyKind.No)
                {
                    _jobs.DiscardCharacteristics(Job);
                    await post(AssistantMessage.Text("Discarded; nothing was saved."));
                }
                else return false;

                await AdvanceAsync(Step.Plans, post, cancellationToken);
                return true;

            case Step.Plans:
                if (_plan is null) return false;
                switch (reply.Kind)
                {
                    case ReplyKind.Yes:
                        await ApprovePlanAsync(_plan, post, cancellationToken);
                        break;
                    case ReplyKind.No:
                        _jobs.RejectJobPlan(Job, _plan);
                        await post(AssistantMessage.Text($"Skipped **{Job.CurrentPlan(_plan).Title}**."));
                        break;
                    case ReplyKind.ApproveAll:
                        foreach (var plan in PendingPlans().ToList())
                        {
                            await ApprovePlanAsync(plan, post, cancellationToken);
                        }
                        break;
                    case ReplyKind.SkipAll:
                        foreach (var plan in PendingPlans().ToList())
                        {
                            _jobs.RejectJobPlan(Job, plan);
                        }
                        await post(AssistantMessage.Text("Skipped the remaining PM models."));
                        break;
                    default:
                        return false;
                }

                await AdvanceAsync(Step.Plans, post, cancellationToken);
                return true;

            default:
                return false;
        }
    }

    // ---------- plain-words edits ----------

    private (ReviewEditTarget Target, JobPlan? Draft)? EditTarget => _step switch
    {
        Step.Proposal => (ReviewEditTarget.Proposal, null),
        Step.CharacteristicsConfirm => (ReviewEditTarget.Characteristics, null),
        Step.Plans when _plan is not null => (ReviewEditTarget.Plan, _plan),
        _ => null
    };

    private async Task<bool> EditAsync(string instruction, Func<AssistantMessage, Task> post, CancellationToken cancellationToken)
    {
        if (EditTarget is not { } target) return false;

        await post(AssistantMessage.Busy("Updating the draft…"));
        try
        {
            var result = await _jobs.ApplyEditAsync(Job, target.Target, instruction, target.Draft, cancellationToken);
            var text = new StringBuilder($"✏️ {result.Summary ?? "Updated the draft."}");
            if (result.Refused.Count > 0)
            {
                text.Append("\n\nNot changed:\n").AppendJoin("\n", result.Refused.Select(r => $"- {r}"));
            }
            await post(AssistantMessage.Text(text.ToString()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await post(AssistantMessage.Text($"⚠ I couldn't apply that change: {ex.Message}"));
        }

        await post((await QuestionAsync(cancellationToken))!);
        return true;
    }

    private async Task UndoAsync(Func<AssistantMessage, Task> post, CancellationToken cancellationToken)
    {
        if (EditTarget is { } target && _jobs.UndoEdit(Job, target.Target, target.Draft))
        {
            await post(AssistantMessage.Text("↩ Undid the last change."));
        }
        else
        {
            await post(AssistantMessage.Text("There is no change to undo."));
        }

        if (await QuestionAsync(cancellationToken) is { } question) await post(question);
    }

    // ---------- steps ----------

    // Moves to the first step from `from` on that still needs a decision, and posts its question (or the summary).
    private async Task AdvanceAsync(Step from, Func<AssistantMessage, Task> post, CancellationToken cancellationToken)
    {
        foreach (var step in new[] { Step.Matches, Step.Proposal, Step.CharacteristicsOffer, Step.Plans })
        {
            if (step < from || !Applies(step)) continue;

            _step = step;
            if (await QuestionAsync(cancellationToken) is { } question)
            {
                await post(question);
                return;
            }
        }

        _step = Step.Done;
        var text = new StringBuilder($"All done with **{FileName}**.");
        if (_summary.Count > 0)
        {
            text.Append("\n\n").AppendJoin("\n", _summary.Select(s => $"- {s}"));
        }

        if (Manual is not null)
        {
            text.Append($"\n\nFrom now on I answer from **{FileName}**. Reply **all manuals** to search every manual again.");
            await post(new AssistantMessage(text.ToString(), ["What are the safety warnings?", "Which spare parts are listed?", "all manuals"]));
        }
        else
        {
            await post(AssistantMessage.Text(text.ToString()));
        }
    }

    private bool Applies(Step step)
    {
        var doc = Job.Document;
        return step switch
        {
            Step.Matches => Job.Report is not null && Job.SelectableMatches.Count > 0 && !Job.LinksSaved,
            Step.Proposal => Job.CurrentProposal is not null && Job.AssetProposalDecision == ReviewDecision.Pending,
            Step.CharacteristicsOffer => doc is { Characteristics: null, Manual: not null }
                                         && (doc.Manual.ExtractedManual.AssetStructure?.TechnicalSpecifications.Count ?? 0) > 0
                                         && Job.CharacteristicsAssetId is not null
                                         && Job.CharacteristicDecision == ReviewDecision.Pending
                                         && Job.CharacteristicDraft is null,
            Step.Plans => IngestionJobService.CanReviewPlans(Job) && PendingPlans().Any(),
            _ => false
        };
    }

    private async Task<AssistantMessage?> QuestionAsync(CancellationToken cancellationToken)
    {
        switch (_step)
        {
            case Step.Failed:
                return new AssistantMessage($"Retry **{FileName}**?", ["retry", "skip"]);

            case Step.Matches:
            {
                _matches = Job.SelectableMatches.OrderByDescending(m => m.ConfidenceScore).ToList();
                await LoadAssetNamesAsync(_matches.Select(m => m.AssetId), cancellationToken);

                var text = new StringBuilder($"I found {_matches.Count} existing asset(s) that may match **{FileName}**:\n\n");
                for (var i = 0; i < _matches.Count; i++)
                {
                    var m = _matches[i];
                    text.Append($"{i + 1}. {AssetLink(m.AssetId)} — {m.ConfidenceScore:P0}. {m.Reasoning}\n");
                }

                var suggested = _matches.Select((m, i) => (m, i))
                    .Where(x => Job.DefaultSelection().Contains(x.m.AssetId))
                    .Select(x => (x.i + 1).ToString())
                    .ToList();
                text.Append("\nWhich should this manual be linked to? PM models are created on the linked assets. " +
                            "Reply with numbers like `1, 3`, or **all** / **none**.");

                var chips = new List<string>();
                if (suggested.Count > 0) chips.Add(string.Join(", ", suggested));
                if (_matches.Count > 1) chips.Add("all");
                chips.Add("none");
                return new AssistantMessage(text.ToString(), chips);
            }

            case Step.Proposal:
            {
                var proposal = Job.CurrentProposal!;
                var mapsOntoExisting = proposal.IsExisting(proposal.Assets[0].Id);
                var text = new StringBuilder(mapsOntoExisting
                    ? "The manual describes sub-assets that this existing asset doesn't have yet. Existing assets are only linked, never changed:\n\n"
                    : "No existing asset matched, so I propose creating these assets:\n\n");
                AppendTree(text, proposal, parentId: null, depth: 0);
                var newCount = proposal.NewAssets.Count;
                var parts = proposal.NewAssets.Sum(a => a.SpareParts?.Count ?? 0);
                text.Append($"\n{newCount} new asset(s){(parts > 0 ? $" with {parts} spare part(s)" : "")}.");
                if (Job.EditedProposal is not null) text.Append(" *(changed by you)*");
                text.Append($"\n\n{SubmitPrompt(mapsOntoExisting ? $"create the {newCount} new one(s)" : "create them")}");
                return new AssistantMessage(text.ToString(), Chips(ReviewEditTarget.Proposal, null, false));
            }

            case Step.CharacteristicsOffer:
            {
                var assetId = Job.CharacteristicsAssetId!;
                await LoadAssetNamesAsync([assetId], cancellationToken);
                var specs = Job.Report!.ExtractedManual.AssetStructure?.TechnicalSpecifications.Count ?? 0;
                return new AssistantMessage(
                    $"The manual lists {specs} technical specification(s). Shall I extract characteristics for {AssetLink(assetId)}? " +
                    "It takes about half a minute; you can check and change the values before anything is saved.",
                    ["yes", "no"]);
            }

            case Step.CharacteristicsConfirm:
            {
                if (Job.CharacteristicDraft is not { } draft) return null;
                var text = new StringBuilder($"{draft.Note}\n\n");
                if (draft.WouldFill.Count > 0)
                {
                    text.Append("| Characteristic | Value |\n|---|---|\n");
                    foreach (var c in draft.WouldFill)
                    {
                        text.Append($"| {Cell(c.Name)} | {Cell(FormatValue(c))} |\n");
                    }
                    text.Append('\n');
                }
                if (Job.CharacteristicUndo.Count > 0) text.Append("*(changed by you)* ");
                text.Append(SubmitPrompt($"save these to {AssetLink(draft.AssetId)}"));
                return new AssistantMessage(text.ToString(), Chips(ReviewEditTarget.Characteristics, null, false));
            }

            case Step.Plans:
            {
                var pending = PendingPlans().ToList();
                if (pending.Count == 0) return null;

                _plan = pending[0];
                var all = Job.Document!.JobPlans!.Plans;
                var number = all.ToList().FindIndex(p => p.Id == _plan.Id) + 1;

                var exists = false;
                try { exists = await _jobs.PmModelExistsAsync(Job, _plan, cancellationToken); }
                catch { /* index not created yet: nothing stored */ }

                var targets = Job.PmModelAssetIds(Job.CurrentPlan(_plan));
                await LoadAssetNamesAsync(targets, cancellationToken);
                return new AssistantMessage(
                    PlanMarkdown(Job.CurrentPlan(_plan), number, all.Count, targets, exists, pending.Count),
                    targets.Count == 0
                        ? pending.Count > 1 ? ["no", "skip all"] : ["no"]
                        : Chips(ReviewEditTarget.Plan, _plan, pending.Count > 1));
            }

            default:
                return null;
        }
    }

    private static string SubmitPrompt(string action) =>
        $"Reply **submit** to {action}, **no** to reject, or tell me what to change (e.g. *rename …*, *remove …*).";

    private IReadOnlyList<string> Chips(ReviewEditTarget target, JobPlan? draft, bool many)
    {
        var chips = new List<string> { "submit", "no" };
        if (Job.CanUndo(target, draft?.Id)) chips.Add("undo");
        if (many) chips.AddRange(["approve all", "skip all"]);
        return chips;
    }

    private string PlanMarkdown(JobPlan plan, int number, int total, IReadOnlyList<string> targets, bool exists, int pending)
    {
        var text = new StringBuilder($"PM model {number} of {total}: **{Cell(plan.Title)}**");
        if (Job.EditedPlans.ContainsKey(plan.Id)) text.Append(" *(changed by you)*");

        var facts = new List<string>();
        facts.Add(string.IsNullOrWhiteSpace(plan.Interval) ? "no frequency stated" : $"every {plan.Interval}");
        facts.Add($"{plan.Lines.Count} line(s)");
        text.Append($"\n\n{string.Join(" · ", facts)}");

        text.Append(targets.Count switch
        {
            0 => "\n\nNo asset is linked to this manual, and a PM model needs one: it can only be skipped.",
            1 => $"\n\nCreated on {AssetLink(targets[0])}.",
            _ => $"\n\nCreated on each of {targets.Count} assets: {string.Join(", ", targets.Select(t => AssetLink(t)))}."
        });

        if (plan.Materials.Count > 0) text.Append($"\n\nMaterials: {string.Join(", ", plan.Materials)}");
        if (plan.SafetyNotes.Count > 0) text.Append("\n\nSafety:\n").AppendJoin("\n", plan.SafetyNotes.Select(n => $"- {n}"));

        text.Append("\n\n| # | Line | Type | Equipment | Record |\n|---|---|---|---|---|\n");
        foreach (var line in plan.Lines.Take(MaxPlanLinesShown))
        {
            var record = string.Join(" · ", new[]
            {
                line.MeasurementPoint, line.NominalValue,
                line.AllowedResults.Count > 0 ? string.Join(" / ", line.AllowedResults) : null
            }.Where(x => !string.IsNullOrWhiteSpace(x)));
            text.Append($"| {line.LineNumber} | {Cell(line.Title)} | {line.LineType} | {Cell(line.Component ?? line.AssetId ?? "—")} | {Cell(record)} |\n");
        }

        if (plan.Lines.Count > MaxPlanLinesShown)
        {
            text.Append($"\n…and {plan.Lines.Count - MaxPlanLinesShown} more line(s).");
        }

        if (exists) text.Append("\n\nThis PM model already exists on a linked asset; submitting replaces its PM jobs and lines.");

        if (targets.Count > 0)
        {
            text.Append($"\n\n{SubmitPrompt("create this PM model")}");
            if (pending > 1) text.Append(" You can also reply **approve all** or **skip all**.");
        }
        else
        {
            text.Append("\n\nReply **no** to skip it.");
        }

        return text.ToString();
    }

    private async Task ApprovePlanAsync(JobPlan draft, Func<AssistantMessage, Task> post, CancellationToken cancellationToken)
    {
        var title = Job.CurrentPlan(draft).Title;
        if (Job.PmModelAssetIds(Job.CurrentPlan(draft)).Count == 0)
        {
            _jobs.RejectJobPlan(Job, draft);
            await post(AssistantMessage.Text($"Skipped **{title}**: no asset is linked, and a PM model needs one."));
            return;
        }

        if (await RunAsync(post, () => _jobs.ApproveJobPlanAsync(Job, draft, cancellationToken), $"create PM model '{title}'"))
        {
            var created = Job.CreatedPmModels.GetValueOrDefault(draft.Id) ?? [];
            var on = created.Count == 0 ? "" : $" on {string.Join(", ", created.Select(pm => $"`{pm.AssetId}`"))}";
            _summary.Add($"Created PM model **{title}**{on}.");
            await post(AssistantMessage.Text($"Created PM model **{title}**{on}."));
        }
    }

    private IEnumerable<JobPlan> PendingPlans() =>
        Job.Document?.JobPlans?.Plans.Where(p => Job.PlanDecision(p) == ReviewDecision.Pending) ?? [];

    // Runs a review action; on failure posts the error (the caller's flow asks the same question again).
    private static async Task<bool> RunAsync(Func<AssistantMessage, Task> post, Func<Task> action, string what)
    {
        try
        {
            await action();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await post(AssistantMessage.Text($"⚠ Could not {what}: {ex.Message}"));
            return false;
        }
    }

    private void AppendTree(StringBuilder text, AssetProposal proposal, string? parentId, int depth)
    {
        var children = depth == 0 ? proposal.Assets.Take(1) : proposal.Assets.Where(a => a.ParentAssetId == parentId);
        foreach (var asset in children)
        {
            text.Append(new string(' ', depth * 2))
                .Append($"- **{Cell(asset.Name)}** `{asset.Id}` {(proposal.IsExisting(asset.Id) ? "· existing" : "· **new**")}");
            if (!string.IsNullOrWhiteSpace(asset.ModelNumber)) text.Append($" · model {Cell(asset.ModelNumber)}");
            if (asset.SpareParts is { Count: > 0 } parts) text.Append($" · {parts.Count} spare part(s)");
            text.Append('\n');
            if (depth < 6) AppendTree(text, proposal, asset.Id, depth + 1);
        }
    }

    private async Task LoadAssetNamesAsync(IEnumerable<string> assetIds, CancellationToken cancellationToken)
    {
        var missing = assetIds.Where(id => !_assetNames.ContainsKey(id)).Distinct().ToList();
        var lookups = await Task.WhenAll(missing.Select(async id =>
        {
            try { return (id, (await _catalog.GetAssetByIdAsync(id, cancellationToken))?.Name); }
            catch { return (id, (string?)null); }
        }));

        foreach (var (id, name) in lookups)
        {
            _assetNames[id] = name ?? "";
        }
    }

    private string AssetLink(string assetId, string? name = null)
    {
        name ??= _assetNames.GetValueOrDefault(assetId);
        var label = string.IsNullOrWhiteSpace(name) ? $"`{assetId}`" : $"**{Cell(name)}** `{assetId}`";
        return $"{label} ([open]({UiText.AssetUrl(assetId)}))";
    }

    private static string Cell(string? text) => (text ?? "").Replace("|", "\\|").Replace("\n", " ").Trim();

    private static string FormatValue(AssetCharacteristic c) =>
        c.NumericValue is { } n ? $"{n:0.###} {c.UnitSymbol ?? c.Unit}".Trim()
        : c.BooleanValue is { } b ? (b ? "Yes" : "No")
        : !string.IsNullOrWhiteSpace(c.TextValue) ? c.TextValue
        : c.DateValue ?? "—";

    private string ProgressText() => Job.Status switch
    {
        IngestionJobStatus.Queued => $"**{FileName}**: waiting to start…",
        IngestionJobStatus.Running => $"**{FileName}**: {(Job.Stage is { } s ? UiText.Label(s) : "Starting")}…",
        IngestionJobStatus.Succeeded => $"**{FileName}**: processed.",
        _ => $"**{FileName}**: failed."
    };

    private void OnJobsChanged()
    {
        if (_step != Step.Processing) return;

        var text = ProgressText();
        if (text == _progress && IsProcessing) return;

        _progress = text;
        Changed?.Invoke();
    }

    public void Dispose() => _jobs.Changed -= OnJobsChanged;
}

