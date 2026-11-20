using System.Collections.Concurrent;
using System.Threading.Channels;
using DocDetectives.Application.Ingestion;
using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;
using DocDetectives.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace DocDetectives.Web.Services;

public enum IngestionJobStatus { Queued, Running, Succeeded, Failed }

/// <summary>A person's decision on a proposed new asset or a drafted PM model.</summary>
public enum ReviewDecision { Pending, Approved, Rejected }

/// <summary>One manual going through the ingestion pipeline. Mutated only by <see cref="IngestionJobService"/>.</summary>
public sealed class IngestionJob
{
    public required Guid Id { get; init; }
    public required string FileName { get; init; }
    public required string FilePath { get; init; }
    public DateTimeOffset QueuedAt { get; init; } = DateTimeOffset.Now;

    public IngestionJobStatus Status { get; internal set; } = IngestionJobStatus.Queued;
    public IngestionStage? Stage { get; internal set; }
    public DateTimeOffset? StartedAt { get; internal set; }
    public DateTimeOffset? FinishedAt { get; internal set; }
    /// <summary>Set when processing succeeded: the document type plus either the manual or the inspection result.</summary>
    public DocumentProcessingReport? Document { get; internal set; }

    /// <summary>The manual result, when the document was classified as a manual.</summary>
    public ManualProcessingReport? Report => Document?.Manual;

    /// <summary>The inspection result, when the document was classified as an inspection.</summary>
    public InspectionProcessingReport? Inspection => Document?.Inspection;

    public string? Error { get; internal set; }

    /// <summary>
    /// Matched existing assets this manual is currently linked to (stored in the asset's linked manual ids).
    /// Empty after ingestion; filled only when a user saves the selection in the UI.
    /// </summary>
    public ConcurrentDictionary<string, bool> LinkedAssetIds { get; } = new(StringComparer.Ordinal);

    /// <summary>True once the user saved an asset selection for this job.</summary>
    public bool LinksSaved { get; internal set; }

    /// <summary>
    /// Checkbox state before the user changes anything: what is linked once saved, otherwise the high-confidence matches.
    /// </summary>
    public HashSet<string> DefaultSelection() =>
        LinksSaved
            ? LinkedAssetIds.Keys.ToHashSet(StringComparer.Ordinal)
            : SelectableMatches.Where(m => m.Status == MappingStatus.AutoLinked).Select(m => m.AssetId)
                .ToHashSet(StringComparer.Ordinal);

    /// <summary>Matches of existing assets a user can select or deselect (new/proposed assets are not listed).</summary>
    public IReadOnlyList<AssetMappingResult> SelectableMatches =>
        Report?.Mappings.Where(m => m.Status is MappingStatus.AutoLinked or MappingStatus.NeedsReview).ToList() ?? [];

    /// <summary>Decision on <see cref="DocumentProcessingReport.AssetProposal"/> (new assets wait for approval).</summary>
    public ReviewDecision AssetProposalDecision { get; internal set; } = ReviewDecision.Pending;

    /// <summary>Assets actually created when the proposal was approved (with characteristic values filled in).</summary>
    public IReadOnlyList<Asset> ApprovedProposalAssets { get; internal set; } = [];

    /// <summary>Decision per drafted PM model, keyed by draft Id. Missing means pending.</summary>
    public ConcurrentDictionary<string, ReviewDecision> JobPlanDecisions { get; } = new(StringComparer.Ordinal);

    /// <summary>PM models created on approval (one per linked asset), keyed by draft Id.</summary>
    public ConcurrentDictionary<string, IReadOnlyList<PmModel>> CreatedPmModels { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The assets a drafted PM model is created on: the assets the person selected for this manual (saved with the
    /// asset selection), plus the asset created or approved from the manual. A document without matches to select
    /// from (e.g. an inspection document) uses the draft's own asset. Assets of a rejected proposal are left out.
    /// </summary>
    public IReadOnlyList<string> PmModelAssetIds(JobPlan draft)
    {
        var rejected = AssetProposalDecision == ReviewDecision.Rejected && Document?.AssetProposal is { } proposal
            ? proposal.Assets.Select(a => a.Id).ToHashSet(StringComparer.Ordinal)
            : [];

        var selected = SelectableMatches.Count > 0
            ? SelectableMatches.Select(m => (string?)m.AssetId).Where(id => LinkedAssetIds.ContainsKey(id!)) // match order, best first
            : [draft.AssetId];

        return new[] { Report?.CreatedAssets.FirstOrDefault()?.Id, Report?.ProposedAssets.FirstOrDefault()?.Id }
            .Concat(selected)
            .OfType<string>()
            .Where(id => !string.IsNullOrWhiteSpace(id) && !rejected.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>True when the PM models wait for the asset selection: there are matches, but none saved yet.</summary>
    public bool PmModelsWaitForSelection => SelectableMatches.Count > 0 && !LinksSaved;

    public ReviewDecision PlanDecision(JobPlan plan) =>
        JobPlanDecisions.GetValueOrDefault(plan.Id, ReviewDecision.Pending);

    /// <summary>True while a proposed asset or a drafted PM model is still waiting for a person's decision.</summary>
    public bool NeedsReview =>
        (Document?.AssetProposal is not null && AssetProposalDecision == ReviewDecision.Pending)
        || (Document?.JobPlans?.Plans.Any(p => PlanDecision(p) == ReviewDecision.Pending) ?? false);

    public TimeSpan? Duration => StartedAt is null ? null : (FinishedAt ?? DateTimeOffset.Now) - StartedAt;
}

/// <summary>
/// Queues documents and ingests them in the background (IngestionParallelism at a time), so a long ingestion survives
/// page navigation and a browser refresh. Job history is kept in memory for the app's lifetime.
/// </summary>
public sealed class IngestionJobService(
    IServiceProvider services,
    IOptions<DocDetectivesOptions> options,
    ILogger<IngestionJobService> logger) : BackgroundService
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".docx", ".doc", ".png", ".jpg", ".jpeg", ".tiff" };

    private readonly Channel<IngestionJob> _queue = Channel.CreateUnbounded<IngestionJob>();
    private readonly ConcurrentDictionary<Guid, IngestionJob> _jobs = new();

    /// <summary>Raised (on a background thread) whenever any job changes.</summary>
    public event Action? Changed;

    public string ManualsFolder => Path.GetFullPath(options.Value.ManualsFolder);

    public static bool IsSupported(string fileName) => SupportedExtensions.Contains(Path.GetExtension(fileName));

    public IReadOnlyList<IngestionJob> Jobs =>
        _jobs.Values.OrderByDescending(j => j.QueuedAt).ToList();

    public IReadOnlyList<FileInfo> GetManualFiles()
    {
        var folder = new DirectoryInfo(ManualsFolder);
        if (!folder.Exists)
        {
            return [];
        }

        return folder.EnumerateFiles()
            .Where(f => IsSupported(f.Name))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Saves an uploaded file into the manuals folder and returns its full path.</summary>
    public async Task<string> SaveUploadAsync(Stream content, string fileName, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ManualsFolder);

        var safeName = Path.GetFileName(fileName);
        var target = Path.Combine(ManualsFolder, safeName);
        var counter = 1;
        while (File.Exists(target))
        {
            target = Path.Combine(
                ManualsFolder,
                $"{Path.GetFileNameWithoutExtension(safeName)} ({counter++}){Path.GetExtension(safeName)}");
        }

        await using var file = File.Create(target);
        await content.CopyToAsync(file, cancellationToken);
        return target;
    }

    public IngestionJob Enqueue(string filePath)
    {
        var job = new IngestionJob
        {
            Id = Guid.NewGuid(),
            FileName = Path.GetFileName(filePath),
            FilePath = filePath
        };

        _jobs[job.Id] = job;
        _queue.Writer.TryWrite(job);
        NotifyChanged();
        return job;
    }

    public bool IsQueuedOrRunning(string filePath) =>
        _jobs.Values.Any(j => j.Status is IngestionJobStatus.Queued or IngestionJobStatus.Running
                              && string.Equals(j.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Makes the manual linked to exactly the selected matched assets: selected ones are added to the asset's
    /// linked manual ids, deselected ones that were linked are removed.
    /// </summary>
    public async Task SaveMatchSelectionAsync(
        IngestionJob job,
        IReadOnlySet<string> selectedAssetIds,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IAssetCatalogGateway>();

        try
        {
            foreach (var mapping in job.SelectableMatches)
            {
                var selected = selectedAssetIds.Contains(mapping.AssetId);
                var linked = job.LinkedAssetIds.ContainsKey(mapping.AssetId);

                if (selected && !linked)
                {
                    await catalog.LinkManualToAssetAsync(
                        mapping.AssetId,
                        mapping.ManualId,
                        mapping.ConfidenceScore,
                        MappingStatus.AutoLinked,
                        cancellationToken);
                    job.LinkedAssetIds[mapping.AssetId] = true;
                }
                else if (!selected && linked)
                {
                    await catalog.UnlinkManualFromAssetAsync(mapping.AssetId, mapping.ManualId, cancellationToken);
                    job.LinkedAssetIds.TryRemove(mapping.AssetId, out _);
                }
            }
        }
        finally
        {
            // Also on failure: the links saved before the error are shown as linked.
            job.LinksSaved = true;
            NotifyChanged();
        }
    }

    /// <summary>Creates the proposed asset tree (and its new characteristics) after a person approved it.</summary>
    public async Task ApproveAssetProposalAsync(IngestionJob job, CancellationToken cancellationToken = default)
    {
        if (job.Document?.AssetProposal is not { } proposal || job.AssetProposalDecision != ReviewDecision.Pending)
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var proposals = scope.ServiceProvider.GetRequiredService<AssetProposalService>();

        job.ApprovedProposalAssets = await proposals.ApproveAsync(proposal, cancellationToken);
        job.AssetProposalDecision = ReviewDecision.Approved;
        NotifyChanged();
    }

    /// <summary>Discards the proposed assets; nothing is written. Drafted PM models of this job stop pointing at them.</summary>
    public void RejectAssetProposal(IngestionJob job)
    {
        if (job.AssetProposalDecision != ReviewDecision.Pending) return;
        job.AssetProposalDecision = ReviewDecision.Rejected;
        NotifyChanged();
    }

    /// <summary>
    /// Whether a drafted plan can be decided on yet. When the same document also proposed new assets, those are
    /// reviewed first (as in the console flow), so a plan never ends up pointing at an asset that was not created.
    /// </summary>
    public static bool CanReviewPlans(IngestionJob job) =>
        (job.Document?.AssetProposal is null || job.AssetProposalDecision != ReviewDecision.Pending)
        && !job.PmModelsWaitForSelection;

    /// <summary>
    /// Creates the PM model of an approved draft (with its PM jobs and lines) on every asset the manual is linked to.
    /// </summary>
    public async Task ApproveJobPlanAsync(IngestionJob job, JobPlan plan, CancellationToken cancellationToken = default)
    {
        if (!CanReviewPlans(job) || job.PlanDecision(plan) != ReviewDecision.Pending)
        {
            return;
        }

        var assetIds = job.PmModelAssetIds(plan);
        if (assetIds.Count == 0)
        {
            throw new InvalidOperationException("the manual is not linked to any asset, and a PM model needs one.");
        }

        // Line links to assets that were rejected (so never created) are removed first.
        var rejected = job.AssetProposalDecision == ReviewDecision.Rejected && job.Document?.AssetProposal is { } proposal
            ? proposal.Assets.Select(a => a.Id).ToHashSet(StringComparer.Ordinal)
            : [];

        var toSave = plan with
        {
            AssetId = plan.AssetId is { } planAsset && rejected.Contains(planAsset) ? null : plan.AssetId,
            Lines = plan.Lines
                .Select(line => line.AssetId is { } lineAsset && rejected.Contains(lineAsset) ? line with { AssetId = null } : line)
                .ToList()
        };

        await using var scope = services.CreateAsyncScope();
        var pmService = scope.ServiceProvider.GetRequiredService<JobPlanService>();

        job.CreatedPmModels[plan.Id] = await pmService.CreatePmModelsAsync(toSave, assetIds, cancellationToken);
        job.JobPlanDecisions[plan.Id] = ReviewDecision.Approved;
        NotifyChanged();
    }

    /// <summary>Discards a drafted PM model; drafts are never stored, so nothing is written.</summary>
    public void RejectJobPlan(IngestionJob job, JobPlan plan)
    {
        if (job.PlanDecision(plan) != ReviewDecision.Pending) return;
        job.JobPlanDecisions[plan.Id] = ReviewDecision.Rejected;
        NotifyChanged();
    }

    /// <summary>Whether this draft's PM model already exists on one of the linked assets (approving replaces it).</summary>
    public async Task<bool> PmModelExistsAsync(IngestionJob job, JobPlan draft, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<JobPlanService>()
            .PmModelExistsAsync(draft.Id, job.PmModelAssetIds(draft), cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Several documents are processed at once (IngestionParallelism), like the console's ingest mode:
        // the next file's Content Understanding call starts before the previous file has finished embedding.
        var workers = Math.Max(1, options.Value.IngestionParallelism);
        return Task.WhenAll(Enumerable.Range(0, workers).Select(async _ =>
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await RunJobAsync(job, stoppingToken);
            }
        }));
    }

    private async Task RunJobAsync(IngestionJob job, CancellationToken stoppingToken)
    {
        job.Status = IngestionJobStatus.Running;
        job.StartedAt = DateTimeOffset.Now;
        NotifyChanged();

        try
        {
            await using var scope = services.CreateAsyncScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<ManualIngestionOrchestrator>();
            var cfg = options.Value;

            var progress = new InlineProgress<IngestionStage>(stage =>
            {
                job.Stage = stage;
                NotifyChanged();
            });

            job.Document = await orchestrator.ProcessDocumentAsync(
                job.FilePath,
                cfg.AutoLinkConfidenceThreshold,
                cfg.AutoCreateAssets,
                cfg.GenerateJobPlans,
                cfg.ExtractCharacteristics,
                cfg.ReviewNewAssets,
                cfg.CreateInspectionJobs,
                progress,
                stoppingToken,
                // Matched assets are not linked during ingestion: the user selects them in the UI.
                linkMatchedAssets: false);

            job.Status = IngestionJobStatus.Succeeded;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            job.Status = IngestionJobStatus.Failed;
            job.Error = "The app was shut down while this manual was being processed.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ingestion failed for {File}", job.FileName);
            job.Status = IngestionJobStatus.Failed;
            job.Error = ex.Message;
        }
        finally
        {
            job.FinishedAt = DateTimeOffset.Now;
            NotifyChanged();
        }
    }

    private void NotifyChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A job change subscriber threw.");
        }
    }

    /// <summary>Unlike <see cref="Progress{T}"/>, reports synchronously so stages are never reordered.</summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
