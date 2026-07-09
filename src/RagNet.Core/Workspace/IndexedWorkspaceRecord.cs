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
    bool FullReindex,
    string? RepositoryRoot = null,
    string? RepositoryRelativeWorkspaceRoot = null,
    string? RemoteUrl = null,
    string? Branch = null,
    string? CommitSha = null,
    IReadOnlyList<string>? IndexedTargetRelativePaths = null);
