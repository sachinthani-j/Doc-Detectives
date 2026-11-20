using DocDetectives.Application.Ingestion;
using DocDetectives.Domain.Entities;

namespace DocDetectives.Web.Services;

/// <summary>Display labels and CSS classes shared by several pages.</summary>
public static class UiText
{
    public static readonly IngestionStage[] Stages =
    [
        IngestionStage.ExtractingContent,
        IngestionStage.AnalyzingStructure,
        IngestionStage.MatchingAssets,
        IngestionStage.EmbeddingChunks,
        IngestionStage.StoringInIndex
    ];

    public static string Label(IngestionStage stage) => stage switch
    {
        IngestionStage.ExtractingContent => "Reading document",
        IngestionStage.AnalyzingStructure => "Extracting specs",
        IngestionStage.MatchingAssets => "Matching assets",
        IngestionStage.EmbeddingChunks => "Embedding sections",
        IngestionStage.StoringInIndex => "Saving to index",
        IngestionStage.Completed => "Done",
        _ => stage.ToString()
    };

    public static (string Text, string Css) Status(IngestionJobStatus status) => status switch
    {
        IngestionJobStatus.Queued => ("Queued", "badge"),
        IngestionJobStatus.Running => ("Processing", "badge badge-info"),
        IngestionJobStatus.Succeeded => ("Completed", "badge badge-ok"),
        IngestionJobStatus.Failed => ("Failed", "badge badge-danger"),
        _ => (status.ToString(), "badge")
    };

    public static (string Text, string Css) Mapping(MappingStatus status) => status switch
    {
        MappingStatus.AutoLinked => ("Linked", "badge badge-ok"),
        MappingStatus.Created => ("New asset", "badge badge-accent"),
        MappingStatus.NeedsReview => ("Needs review", "badge badge-warn"),
        _ => ("Unmatched", "badge")
    };

    public static string AssetUrl(string assetId) => $"assets/view?id={Uri.EscapeDataString(assetId)}";

    public static string DocumentUrl(string manualId) => $"documents/view?id={Uri.EscapeDataString(manualId)}";

    public static string FileBadge(string fileName) =>
        Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : "DOC";

    public static string AskUrl(string? assetId) =>
        string.IsNullOrEmpty(assetId) ? "ask" : $"ask?asset={Uri.EscapeDataString(assetId)}";

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / (1024.0 * 1024):0.0} MB"
    };

    public static string Duration(TimeSpan? span) => span is null
        ? ""
        : span.Value.TotalMinutes >= 1
            ? $"{(int)span.Value.TotalMinutes}m {span.Value.Seconds:00}s"
            : $"{span.Value.Seconds}s";
}
