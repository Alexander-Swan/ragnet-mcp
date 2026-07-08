namespace RagNet.Mcp.Indexing;

public sealed record IndexStatusResult(
    string WorkspaceRoot,
    bool StateExists,
    DateTimeOffset? LastIndexedAtUtc,
    int IndexedFileCount,
    int TotalChunkCount,
    string? EmbeddingModel,
    string? SchemaVersion,
    bool NeedsFullReindex,
    IReadOnlyList<IndexFileChunkEstimate> Files,
    IReadOnlyList<string> Warnings);
