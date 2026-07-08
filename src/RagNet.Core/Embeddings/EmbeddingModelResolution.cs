namespace RagNet.Mcp.Embeddings;

public sealed record EmbeddingModelResolution(
    string ConfiguredModel,
    string SelectedModel,
    bool UsedFallback,
    IReadOnlyList<EmbeddingModelInfo> InstalledModels,
    string? Message);
