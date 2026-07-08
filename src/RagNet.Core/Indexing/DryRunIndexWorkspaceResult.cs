namespace RagNet.Mcp.Indexing;

public sealed record DryRunIndexWorkspaceResult(
    string WorkspaceRoot,
    int FilesScanned,
    int ChunksThatWouldBeIndexed,
    int ChunksThatWouldBeDeleted,
    int TotalChunksAfterIndex,
    string IndexProfile,
    bool FullReindex,
    bool StateCompatible,
    int ChangedFiles,
    int DeletedFiles,
    int UnchangedFiles,
    IReadOnlyList<IndexFileChunkEstimate> FileChunkEstimates,
    IReadOnlyList<string> IndexedTargets,
    IReadOnlyList<string> Warnings);
