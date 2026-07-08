using RagNet.Mcp.Workspace.Interfaces;

namespace RagNet.Mcp.Workspace;

public sealed class IndexedWorkspaceRegistry : IIndexedWorkspaceRegistry
{
    private readonly object _gate = new();
    private readonly HashSet<string> _roots = new(StringComparer.OrdinalIgnoreCase);

    public void MarkIndexed(string workspaceRoot)
    {
        lock (_gate)
        {
            _roots.Add(Path.GetFullPath(workspaceRoot));
        }
    }

    public IReadOnlyList<string> GetIndexedWorkspaceRoots()
    {
        lock (_gate)
        {
            return _roots.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
}
