namespace RagNet.Mcp.Indexing.Interfaces;

public interface IWorkspaceIndexer
{
    Task<IndexWorkspaceResult> IndexAsync(
        string workspacePath,
        IReadOnlyList<string>? excludeDirectories = null,
        bool force = false,
        CancellationToken cancellationToken = default,
        IProgress<IndexingProgress>? progress = null);

    Task<IReadOnlyList<IndexWorkspaceResult>> IndexGroupAsync(
        string workspaceGroup,
        IReadOnlyList<string>? excludeDirectories = null,
        bool force = false,
        CancellationToken cancellationToken = default,
        IProgress<IndexingProgress>? progress = null);

    Task<IndexStatusResult> GetStatusAsync(
        string workspacePath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchResult>> SearchAsync(
        string? filePath,
        string query,
        int limit,
        bool hybrid,
        string? scope,
        string? workspaceRoot,
        string? workspaceGroup,
        string? contentType = null,
        string? retrievalMode = null,
        CancellationToken cancellationToken = default);

    Task<string> GetCodeContextAsync(string filePath, int line, int before, int after, CancellationToken cancellationToken = default);

    Task<string?> GetSymbolDetailsAsync(string filePath, string symbolName, CancellationToken cancellationToken = default);
}
