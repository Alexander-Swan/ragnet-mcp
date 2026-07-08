namespace RagNet.Mcp.Indexing;

public sealed record IndexFileChunkEstimate(
    string FilePath,
    int CurrentChunks,
    int EstimatedChunksToEmbed,
    int EstimatedChunksToDelete);
