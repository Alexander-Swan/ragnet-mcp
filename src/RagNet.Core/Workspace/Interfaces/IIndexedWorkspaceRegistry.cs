namespace RagNet.Mcp.Workspace.Interfaces;

public interface IIndexedWorkspaceRegistry
{
    Task MarkIndexedAsync(IndexedWorkspaceRecord record, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetIndexedWorkspaceRootsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IndexedWorkspaceRecord>> GetIndexedWorkspacesAsync(CancellationToken cancellationToken = default);

    Task DeleteWorkspaceAsync(string workspaceRoot, CancellationToken cancellationToken = default);
}
