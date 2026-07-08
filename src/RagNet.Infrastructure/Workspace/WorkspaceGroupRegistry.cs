using RagNet.Mcp.Workspace.Interfaces;

namespace RagNet.Mcp.Workspace;

public sealed class WorkspaceGroupRegistry : IWorkspaceGroupRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WorkspaceGroupRecord> _groups = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<WorkspaceGroupRecord>> GetGroupsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<WorkspaceGroupRecord>>(
                _groups.Values.OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }

    public Task<WorkspaceGroupRecord?> GetGroupAsync(string name, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_groups.GetValueOrDefault(name));
        }
    }

    public Task<WorkspaceGroupRecord> SaveGroupAsync(
        string name,
        IReadOnlyList<string> roots,
        IReadOnlyList<string>? excludeDirectories = null,
        CancellationToken cancellationToken = default)
    {
        var group = new WorkspaceGroupRecord(
            name,
            WorkspaceGroupSources.Shared,
            NormalizeDistinct(roots),
            NormalizeDistinct(excludeDirectories ?? []),
            IsReadOnly: false);

        lock (_gate)
        {
            _groups[group.Name] = group;
        }

        return Task.FromResult(group);
    }

    public Task DeleteGroupAsync(string name, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _groups.Remove(name);
        }

        return Task.CompletedTask;
    }

    private static IReadOnlyList<string> NormalizeDistinct(IEnumerable<string> paths)
        => paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return fullPath.Length == root?.Length
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
