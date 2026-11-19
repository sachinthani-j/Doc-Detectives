using DocDetectives.Domain.Entities;
using System.Text;

namespace DocDetectives.Application.Services;

/// <summary>
/// Splits layout-aware Markdown along section headings so tables and procedural steps remain intact.
/// </summary>
public sealed class MarkdownManualChunker
{
    private const int MaxChunkCharacters = 3000;

    public IReadOnlyList<ManualChunk> ChunkManual(
        ExtractedManual manual,
        IReadOnlyList<string> linkedAssetIds)
    {
        var chunks = new List<ManualChunk>();
        var lines = manual.MarkdownContent.Split('\n');

        var currentSection = "Overview";
        var buffer = new StringBuilder();
        var chunkIndex = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var isHeading = line.StartsWith("# ") || line.StartsWith("## ") || line.StartsWith("### ");

            if ((isHeading || buffer.Length + line.Length > MaxChunkCharacters) && buffer.Length > 0)
            {
                chunks.Add(CreateChunk(manual, currentSection, buffer.ToString(), linkedAssetIds, chunkIndex++));
                buffer.Clear();
            }

            if (isHeading)
            {
                currentSection = line.TrimStart('#', ' ');
            }

            buffer.AppendLine(line);
        }

        if (buffer.Length > 0)
        {
            chunks.Add(CreateChunk(manual, currentSection, buffer.ToString(), linkedAssetIds, chunkIndex));
        }

        return chunks;
    }

    private static ManualChunk CreateChunk(
        ExtractedManual manual,
        string sectionTitle,
        string content,
        IReadOnlyList<string> assetIds,
        int index) =>
        new(
            ChunkId: $"{manual.ManualId}_chunk_{index:D4}",
            ManualId: manual.ManualId,
            FileName: manual.FileName,
            SectionTitle: sectionTitle,
            Content: content.Trim(),
            AssetIds: assetIds,
            Embedding: ReadOnlyMemory<float>.Empty);
}