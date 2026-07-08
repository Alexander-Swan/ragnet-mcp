namespace RagNet.Mcp.Indexing;

public sealed record WorkspaceIndexState(
    string WorkspaceRoot,
    IReadOnlyDictionary<string, IndexedFileState> Files,
    string? EmbeddingModel,
    string? SchemaVersion,
    DateTimeOffset? SavedAtUtc,
    bool StateExists);
