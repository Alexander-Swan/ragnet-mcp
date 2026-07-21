namespace RagNet.Mcp.Indexing;

public sealed record IndexWorkspaceResult(
    string WorkspaceRoot,
    int FilesScanned,
    int ChunksIndexed,
    bool FullReindex,
    IReadOnlyList<string> Warnings,
    int TotalChunksIndexed = 0,
    int UnchangedFiles = 0);
