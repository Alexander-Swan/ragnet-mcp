using RagNet.Mcp.Workspace.Interfaces;

namespace RagNet.Mcp.Workspace;

public sealed class IndexedWorkspaceRegistry : IIndexedWorkspaceRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IndexedWorkspaceRecord> _records = new(StringComparer.OrdinalIgnoreCase);

    public Task MarkIndexedAsync(IndexedWorkspaceRecord record, CancellationToken cancellationToken = default)
    {
        var namedRecord = record.WithCalculatedNames();
        lock (_gate)
        {
            _records[Path.GetFullPath(namedRecord.WorkspaceRoot)] = namedRecord;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetIndexedWorkspaceRootsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<string>>(_records.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }

    public Task<IReadOnlyList<IndexedWorkspaceRecord>> GetIndexedWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<IndexedWorkspaceRecord>>(
                _records.Values.OrderBy(record => record.WorkspaceRoot, StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }

    public Task DeleteWorkspaceAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _records.Remove(Path.GetFullPath(workspaceRoot));
        }

        return Task.CompletedTask;
    }
}
