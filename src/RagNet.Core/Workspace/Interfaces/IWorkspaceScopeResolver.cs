namespace RagNet.Mcp.Workspace.Interfaces;

public interface IWorkspaceScopeResolver
{
    Task<IReadOnlyList<WorkspaceInfo>> ResolveAsync(
        string? filePath,
        string? scope,
        string? workspaceRoot,
        string? workspaceGroup,
        bool includeGroupedWorkspaces = false,
        CancellationToken cancellationToken = default);
}
