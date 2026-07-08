namespace RagNet.Mcp.Embeddings.Interfaces;

public interface IEmbeddingProvider
{
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);
}
