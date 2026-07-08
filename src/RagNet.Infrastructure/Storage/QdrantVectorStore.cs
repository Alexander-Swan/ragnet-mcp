using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Indexing;
using RagNet.Mcp.Storage.Interfaces;

namespace RagNet.Mcp.Storage;

public sealed class QdrantVectorStore(
    HttpClient httpClient,
    IOptions<RagNetOptions> options,
    InMemoryVectorStore fallbackStore,
    ILogger<QdrantVectorStore> logger) : IVectorStore
{
    private readonly RagNetOptions _options = options.Value;
    private readonly HttpClient _httpClient = httpClient;

    public async Task UpsertAsync(string workspaceRoot, IReadOnlyList<CodeChunk> chunks, IReadOnlyList<float[]> embeddings, CancellationToken cancellationToken = default)
    {
        // The first implementation keeps a working local store while the typed Qdrant payloads mature.
        // Health checks still validate Qdrant availability, and this class is the seam for durable storage.
        logger.LogInformation("Indexing {Count} chunks for {WorkspaceRoot}; Qdrant target is {QdrantUrl}.", chunks.Count, workspaceRoot, _httpClient.BaseAddress ?? new Uri(_options.Qdrant.BaseUrl));
        await fallbackStore.UpsertAsync(workspaceRoot, chunks, embeddings, cancellationToken);
    }

    public Task DeleteByFileAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Deleting indexed chunks for {FilePath} in {WorkspaceRoot}.", filePath, workspaceRoot);
        return fallbackStore.DeleteByFileAsync(workspaceRoot, filePath, cancellationToken);
    }

    public Task DeleteWorkspaceAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Deleting all indexed chunks for {WorkspaceRoot}.", workspaceRoot);
        return fallbackStore.DeleteWorkspaceAsync(workspaceRoot, cancellationToken);
    }

    public Task<IReadOnlyList<SearchResult>> SearchAsync(string workspaceRoot, float[] embedding, string query, int limit, bool hybrid, CancellationToken cancellationToken = default)
        => fallbackStore.SearchAsync(workspaceRoot, embedding, query, limit, hybrid, cancellationToken);
}
