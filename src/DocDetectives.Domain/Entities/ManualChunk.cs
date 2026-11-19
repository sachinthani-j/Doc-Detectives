namespace DocDetectives.Domain.Entities;

public sealed record ManualChunk(
    string ChunkId,
    string ManualId,
    string FileName,
    string SectionTitle,
    string Content,
    IReadOnlyList<string> AssetIds,
    ReadOnlyMemory<float> Embedding);
