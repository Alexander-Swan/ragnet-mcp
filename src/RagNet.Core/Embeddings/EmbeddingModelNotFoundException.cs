namespace RagNet.Mcp.Embeddings;

public sealed class EmbeddingModelNotFoundException(string model, string message) : Exception(message)
{
    public string Model { get; } = model;
}
