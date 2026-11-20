using DocDetectives.Domain.Entities;

namespace DocDetectives.Domain.Abstractions;

public interface IKnowledgeIndex
{
    Task UpsertChunksAsync(
        IReadOnlyList<ManualChunk> chunks,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hybrid search over manual chunks. When <paramref name="assetId"/> is null or empty,
    /// all manuals are searched instead of only those linked to the asset.
    /// </summary>
    Task<IReadOnlyList<ManualChunk>> SearchByAssetAsync(
        string? assetId,
        ReadOnlyMemory<float> queryEmbedding,
        string queryText,
        int topK = 5,
        CancellationToken cancellationToken = default);

    /// <summary>Hybrid search over the chunks of one manual only.</summary>
    Task<IReadOnlyList<ManualChunk>> SearchByManualAsync(
        string manualId,
        ReadOnlyMemory<float> queryEmbedding,
        string queryText,
        int topK = 5,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the distinct manuals that have at least one chunk linked to the asset.</summary>
    Task<IReadOnlyList<ManualReference>> GetManualsForAssetAsync(
        string assetId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists every manual in the index, with linked asset ids.</summary>
    Task<IReadOnlyList<ManualReference>> ListManualsAsync(CancellationToken cancellationToken = default);

    /// <summary>One manual with its linked asset ids and section titles; null when it is not in the index.</summary>
    Task<ManualReference?> GetManualAsync(string manualId, CancellationToken cancellationToken = default);
}
