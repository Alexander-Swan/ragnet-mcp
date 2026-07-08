using RagNet.Mcp.Embeddings;

namespace RagNet.Mcp.Embeddings.Interfaces;

public interface IEmbeddingModelCatalog
{
    Task<IReadOnlyList<EmbeddingModelInfo>> ListInstalledModelsAsync(CancellationToken cancellationToken = default);

    Task<EmbeddingModelResolution> ResolveEmbeddingModelAsync(CancellationToken cancellationToken = default);
}
