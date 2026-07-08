namespace RagNet.Mcp.Indexing;

public sealed record IndexStatusResult(
    string WorkspaceRoot,
    bool StateExists,
    DateTimeOffset? LastIndexedAtUtc,
    int IndexedFileCount,
    string? EmbeddingModel,
    string? SchemaVersion,
    bool NeedsFullReindex,
    IReadOnlyList<string> Warnings);
