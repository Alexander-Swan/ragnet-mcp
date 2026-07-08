namespace RagNet.Mcp.Indexing;

public sealed record DryRunIndexWorkspaceResult(
    string WorkspaceRoot,
    int FilesScanned,
    int ChunksThatWouldBeIndexed,
    string IndexProfile,
    bool FullReindex,
    bool StateCompatible,
    int ChangedFiles,
    int DeletedFiles,
    int UnchangedFiles,
    IReadOnlyList<string> IndexedTargets,
    IReadOnlyList<string> Warnings);
