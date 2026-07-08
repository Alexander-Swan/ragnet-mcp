namespace RagNet.Mcp.Workspace.Interfaces;

public interface IIndexedWorkspaceRegistry
{
    void MarkIndexed(string workspaceRoot);

    IReadOnlyList<string> GetIndexedWorkspaceRoots();
}
