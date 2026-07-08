namespace RagNet.Mcp.Indexing;

public sealed record IndexingProgress(
    string WorkspaceRoot,
    IndexingProgressStage Stage,
    int Current,
    int? Total,
    string Message);
