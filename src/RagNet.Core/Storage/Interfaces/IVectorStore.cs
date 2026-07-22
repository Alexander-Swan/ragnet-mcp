using RagNet.Mcp.Indexing;

namespace RagNet.Mcp.Storage.Interfaces;

public interface IVectorStore
{
    Task UpsertAsync(string workspaceRoot, IReadOnlyList<CodeChunk> chunks, IReadOnlyList<float[]> embeddings, CancellationToken cancellationToken = default);

    Task UpsertAsync(string workspaceRoot, string collectionName, IReadOnlyList<CodeChunk> chunks, IReadOnlyList<float[]> embeddings, CancellationToken cancellationToken = default);

    Task DeleteByFileAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default);

    Task DeleteByFilesAsync(string workspaceRoot, IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default);

    Task DeleteByFilesAsync(string workspaceRoot, string collectionName, IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default);

    Task DeleteByDirectoriesAsync(string workspaceRoot, string collectionName, IReadOnlyList<string> directoryPaths, CancellationToken cancellationToken = default);

    Task DeleteWorkspaceAsync(string workspaceRoot, CancellationToken cancellationToken = default);

    Task DeleteCollectionAsync(string collectionName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchResult>> SearchAsync(
        string workspaceRoot,
        float[] embedding,
        string query,
        int limit,
        bool hybrid,
        string? contentType = null,
        string? indexProfile = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchResult>> SearchAsync(
        string workspaceRoot,
        string collectionName,
        float[] embedding,
        string query,
        int limit,
        bool hybrid,
        string? contentType = null,
        string? indexProfile = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodeChunk>> GetChunksByFileAsync(
        string workspaceRoot,
        string collectionName,
        string filePath,
        CancellationToken cancellationToken = default);
}
