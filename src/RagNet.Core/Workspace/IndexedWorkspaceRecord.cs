namespace RagNet.Mcp.Workspace;

public sealed record IndexedWorkspaceRecord(
    string WorkspaceRoot,
    string WorkspaceId,
    string CollectionName,
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> IndexedTargets,
    DateTimeOffset LastIndexedUtc,
    int FilesScanned,
    int ChunksIndexed,
    bool FullReindex);
