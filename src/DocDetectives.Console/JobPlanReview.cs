using DocDetectives.Application.Ingestion;
using DocDetectives.Domain.Entities;

namespace DocDetectives.ConsoleApp;

/// <summary>
/// Walks a person through each drafted job plan after ingestion. A plan is saved only when they approve it;
/// before that they can edit the title, interval and lines, or skip the plan (nothing is saved).
/// Plain ASCII output only: the default Windows console code page cannot display characters such as '•'.
/// </summary>
public static class JobPlanReview
{
    private const string Indent = "      ";

    public static async Task RunAsync(
        IReadOnlyList<JobPlan> drafts,
        JobPlanService jobPlanService,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        output.WriteLine($"=== Job plan review: {drafts.Count} draft(s). Nothing is saved until you approve it. ===");
        var approved = 0;
        var skipped = 0;

        for (var i = 0; i < drafts.Count; i++)
        {
            var plan = drafts[i];

            while (true)
            {
                output.WriteLine();
                output.WriteLine($"--- Job plan {i + 1}/{drafts.Count} ---");
                Print(plan, output);

                var storedStatus = await jobPlanService.GetStoredStatusAsync(plan.Id, cancellationToken);
                if (storedStatus is not null)
                {
                    output.WriteLine($"  Note: a plan with this ID is already saved ({storedStatus}); approving replaces it.");
                }

                var choice = Ask(input, output, "(A)pprove and save, (E)dit, (S)kip without saving, (Q)uit review");
                if (choice is null or "q")
                {
                    skipped += drafts.Count - i;
                    output.WriteLine($"Review stopped. Approved: {approved}, not saved: {skipped}.");
                    return;
                }

                if (choice == "a")
                {
                    var saved = await jobPlanService.SaveApprovedAsync(plan, cancellationToken);
                    output.WriteLine($"  Saved '{saved.Id}' as {saved.Status} with {saved.Lines.Count} line(s).");
                    approved++;
                    break;
                }

                if (choice == "s")
                {
                    output.WriteLine("  Skipped: not saved.");
                    skipped++;
                    break;
                }

                if (choice == "e")
                {
                    plan = Edit(plan, input, output);
                }
            }
        }

        output.WriteLine();
        output.WriteLine($"Review complete. Approved and saved: {approved}, not saved: {skipped}.");
    }

    private static void Print(JobPlan plan, TextWriter output)
    {
        output.WriteLine($"  ID        : {plan.Id}  [{plan.Status}]");
        output.WriteLine($"  Title     : {plan.Title}");
        output.WriteLine($"  Source    : {plan.SourceFileName} ({plan.SourceType}){(plan.DocumentCode is null ? "" : $" | code {plan.DocumentCode}")}");
        output.WriteLine($"  Asset     : {plan.AssetId ?? "none (no asset identified)"}");
        output.WriteLine($"  Interval  : {FormatInterval(plan)}");

        if (plan.Materials.Count > 0)
        {
            output.WriteLine($"  Materials : {string.Join(", ", plan.Materials)}");
        }

        foreach (var note in plan.SafetyNotes)
        {
            output.WriteLine($"  Safety    : {note}");
        }

        output.WriteLine("  Lines     :");
        for (var n = 0; n < plan.Lines.Count; n++)
        {
            var line = plan.Lines[n];
            output.WriteLine($"  {n + 1,3}. [{line.LineType}] {line.Title}");

            if (!string.IsNullOrWhiteSpace(line.Instruction))
            {
                output.WriteLine($"{Indent}Instruction: {line.Instruction}");
            }

            var equipment = string.Join(" ", new[] { line.Component, line.EquipmentCode is null ? null : $"({line.EquipmentCode})" }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            if (equipment.Length > 0 || line.AssetId != plan.AssetId)
            {
                output.WriteLine($"{Indent}Equipment  : {(equipment.Length > 0 ? equipment : "-")} -> asset {line.AssetId ?? "none"}");
            }

            if (line.AllowedResults.Count > 0)
            {
                output.WriteLine($"{Indent}Answers    : {string.Join(" / ", line.AllowedResults)}");
            }

            if (!string.IsNullOrWhiteSpace(line.NominalValue))
            {
                output.WriteLine($"{Indent}Nominal    : {line.NominalValue}");
            }

            if (!string.IsNullOrWhiteSpace(line.MeasurementPoint) || !string.IsNullOrWhiteSpace(line.MeasurementCode))
            {
                output.WriteLine($"{Indent}Measuring  : point {line.MeasurementPoint ?? "-"}, code {line.MeasurementCode ?? "-"}");
            }
        }
    }

    private static string FormatInterval(JobPlan plan) =>
        plan.Interval is null && plan.IntervalValue is null
            ? "not stated (set it in the EAM, or edit it here)"
            : $"{plan.Interval ?? "-"}{(plan.IntervalValue is null ? "" : $" (every {plan.IntervalValue} {plan.IntervalUnit})")}";

    private static JobPlan Edit(JobPlan plan, TextReader input, TextWriter output)
    {
        while (true)
        {
            var choice = Ask(input, output,
                "Edit: (1) title, (2) interval, (3) change a line, (4) delete a line, (5) add a line, (6) move a line, (D)one");

            switch (choice)
            {
                case null or "d":
                    return plan;

                case "1":
                    plan = plan with { Title = AskField(input, output, "Title", plan.Title) ?? plan.Title };
                    break;

                case "2":
                    var interval = AskField(input, output, "Interval as written (e.g. 4 W)", plan.Interval);
                    var value = AskField(input, output, "Every (number)", plan.IntervalValue?.ToString());
                    var unit = AskField(input, output, "Unit (Hours, Days, Weeks, Months, Years)", plan.IntervalUnit);
                    plan = plan with
                    {
                        Interval = interval,
                        IntervalValue = int.TryParse(value, out var number) ? number : null,
                        IntervalUnit = unit
                    };
                    break;

                case "3" when AskLineNumber(input, output, plan, "Line to change") is { } index:
                    var lines = plan.Lines.ToList();
                    lines[index] = EditLine(lines[index], input, output);
                    plan = plan with { Lines = lines };
                    break;

                case "4" when AskLineNumber(input, output, plan, "Line to delete") is { } index:
                    plan = plan with { Lines = plan.Lines.Where((_, n) => n != index).ToList() };
                    output.WriteLine($"  Line {index + 1} deleted.");
                    break;

                case "5":
                    var position = AskField(input, output, $"Insert at position (1-{plan.Lines.Count + 1})", (plan.Lines.Count + 1).ToString());
                    var at = int.TryParse(position, out var p) ? Math.Clamp(p - 1, 0, plan.Lines.Count) : plan.Lines.Count;
                    var newLine = EditLine(
                        new JobPlanLine(0, "New line", JobPlanLineType.Inspection, null, null, null, plan.AssetId, null, null, null, ["OK", "NOK"]),
                        input, output);
                    var withNew = plan.Lines.ToList();
                    withNew.Insert(at, newLine);
                    plan = plan with { Lines = withNew };
                    break;

                case "6" when AskLineNumber(input, output, plan, "Line to move") is { } from:
                    var target = AskField(input, output, $"New position (1-{plan.Lines.Count})", (from + 1).ToString());
                    if (int.TryParse(target, out var t))
                    {
                        var moved = plan.Lines.ToList();
                        var item = moved[from];
                        moved.RemoveAt(from);
                        moved.Insert(Math.Clamp(t - 1, 0, moved.Count), item);
                        plan = plan with { Lines = moved };
                    }
                    break;
            }

            // Keep displayed numbers in step with the order after every change.
            plan = plan with { Lines = plan.Lines.Select((l, n) => l with { LineNumber = n + 1 }).ToList() };
            Print(plan, output);
        }
    }

    private static JobPlanLine EditLine(JobPlanLine line, TextReader input, TextWriter output)
    {
        output.WriteLine("  Press Enter to keep a value, type '-' to clear it.");
        var type = AskField(input, output, "Type (T = Task, I = Inspection)", line.LineType);
        var answers = AskField(input, output, "Answers, separated by '/' (e.g. OK / NOK)", string.Join(" / ", line.AllowedResults));

        return line with
        {
            Title = AskField(input, output, "Title", line.Title) ?? line.Title,
            LineType = type?.Trim().ToLowerInvariant() is "t" or "task" ? JobPlanLineType.Task : JobPlanLineType.Inspection,
            Instruction = AskField(input, output, "Instruction", line.Instruction),
            Component = AskField(input, output, "Equipment / component", line.Component),
            EquipmentCode = AskField(input, output, "Equipment code", line.EquipmentCode),
            NominalValue = AskField(input, output, "Nominal value", line.NominalValue),
            AllowedResults = (answers ?? string.Empty)
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList()
        };
    }

    private static int? AskLineNumber(TextReader input, TextWriter output, JobPlan plan, string prompt)
    {
        if (plan.Lines.Count == 0)
        {
            output.WriteLine("  The plan has no lines.");
            return null;
        }

        var answer = Ask(input, output, $"{prompt} (1-{plan.Lines.Count})");
        if (int.TryParse(answer, out var number) && number >= 1 && number <= plan.Lines.Count)
        {
            return number - 1;
        }

        output.WriteLine("  Not a valid line number.");
        return null;
    }

    // Enter keeps the current value, "-" clears it.
    private static string? AskField(TextReader input, TextWriter output, string label, string? current)
    {
        output.Write($"  {label} [{current ?? ""}]: ");
        var answer = input.ReadLine();
        if (answer is null || answer.Length == 0)
        {
            return current;
        }

        return answer.Trim() == "-" ? null : answer.Trim();
    }

    // Returns the lower-cased answer, or null when the input has ended.
    private static string? Ask(TextReader input, TextWriter output, string prompt)
    {
        output.Write($"{prompt}: ");
        return input.ReadLine()?.Trim().ToLowerInvariant();
    }
}
