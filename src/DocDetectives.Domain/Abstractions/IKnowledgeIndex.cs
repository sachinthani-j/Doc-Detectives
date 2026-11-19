using DocDetectives.Domain.Entities;

namespace DocDetectives.Domain.Abstractions;

public interface IKnowledgeIndex
{
    Task UpsertChunksAsync(
        IReadOnlyList<ManualChunk> chunks,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ManualChunk>> SearchByAssetAsync(
        string assetId,
        ReadOnlyMemory<float> queryEmbedding,
        string queryText,
        int topK = 5,
        CancellationToken cancellationToken = default);
}