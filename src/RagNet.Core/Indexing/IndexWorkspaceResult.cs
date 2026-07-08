namespace RagNet.Mcp.Indexing;

public sealed record IndexWorkspaceResult(
    string WorkspaceRoot,
    int FilesScanned,
    int ChunksIndexed,
    bool FullReindex,
    IReadOnlyList<string> Warnings);
