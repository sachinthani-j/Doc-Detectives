using DocDetectives.Domain.Abstractions;
using Microsoft.AspNetCore.StaticFiles;

namespace DocDetectives.Web.Services;

/// <summary>
/// Serves the original file of an ingested document so the browser can preview it inline (PDF viewer, image).
/// Files are looked up by manual id in the index and only ever read from the configured manuals folder.
/// </summary>
public static class DocumentFileEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>Extensions browsers can show inline; other files are offered as a download instead.</summary>
    public static bool CanPreview(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".pdf" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";

    public static bool IsImage(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";

    public static string FileUrl(string manualId, bool download = false) =>
        $"documents/file/{Uri.EscapeDataString(manualId)}{(download ? "?download=true" : "")}";

    /// <summary>Full path of the document's file in the manuals folder, or null when it is not there.</summary>
    public static string? ResolvePath(string manualsFolder, string fileName)
    {
        var folder = Path.GetFullPath(manualsFolder);
        var path = Path.GetFullPath(Path.Combine(folder, Path.GetFileName(fileName)));

        // Path.GetFileName already strips directories; the prefix check is a second guard against traversal.
        return path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }

    public static IEndpointRouteBuilder MapDocumentFiles(this IEndpointRouteBuilder app)
    {
        app.MapGet("/documents/file/{manualId}", async (
            string manualId,
            bool? download,
            IKnowledgeIndex knowledge,
            IngestionJobService jobs,
            CancellationToken cancellationToken) =>
        {
            var manual = await knowledge.GetManualAsync(manualId, cancellationToken);
            if (manual is null)
            {
                return Results.NotFound();
            }

            var path = ResolvePath(jobs.ManualsFolder, manual.FileName);
            if (path is null)
            {
                return Results.NotFound();
            }

            if (!ContentTypes.TryGetContentType(path, out var contentType))
            {
                contentType = "application/octet-stream";
            }

            // Without a download name the browser shows the file inline; range requests let the PDF viewer page through it.
            return Results.File(
                path,
                contentType,
                fileDownloadName: download == true ? manual.FileName : null,
                enableRangeProcessing: true);
        });

        return app;
    }
}
