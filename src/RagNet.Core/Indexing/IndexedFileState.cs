namespace RagNet.Mcp.Indexing;

public sealed record IndexedFileState(
    string FilePath,
    string Fingerprint,
    long Size,
    DateTimeOffset LastWriteTimeUtc,
    int ChunkCount = 0);
