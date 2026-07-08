namespace RagNet.Mcp.Workspace.Interfaces;

public interface IWorkspaceGroupRegistry
{
    Task<IReadOnlyList<WorkspaceGroupRecord>> GetGroupsAsync(CancellationToken cancellationToken = default);

    Task<WorkspaceGroupRecord?> GetGroupAsync(string name, CancellationToken cancellationToken = default);

    Task<WorkspaceGroupRecord> SaveGroupAsync(
        string name,
        IReadOnlyList<string> roots,
        IReadOnlyList<string>? excludeDirectories = null,
        CancellationToken cancellationToken = default);

    Task DeleteGroupAsync(string name, CancellationToken cancellationToken = default);
}
