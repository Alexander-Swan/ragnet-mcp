namespace RagNet.Mcp.Indexing.Interfaces;

public interface IWorkspaceIndexStateStore
{
    Task<WorkspaceIndexState> LoadAsync(string workspaceRoot, CancellationToken cancellationToken = default);

    Task SaveAsync(WorkspaceIndexState state, CancellationToken cancellationToken = default);
}
