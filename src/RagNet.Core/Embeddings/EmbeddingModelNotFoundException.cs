namespace RagNet.Mcp.Embeddings;

public sealed class EmbeddingModelNotFoundException(
    string model,
    string message,
    IReadOnlyList<EmbeddingModelInfo>? installedModels = null,
    string? fallbackModel = null,
    bool fallbackAvailable = false) : Exception(message)
{
    public string Model { get; } = model;

    public IReadOnlyList<EmbeddingModelInfo> InstalledModels { get; } = installedModels ?? [];

    public string? FallbackModel { get; } = fallbackModel;

    public bool FallbackAvailable { get; } = fallbackAvailable;
}
