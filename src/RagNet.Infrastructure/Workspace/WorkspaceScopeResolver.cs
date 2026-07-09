using RagNet.Mcp.Workspace.Interfaces;

namespace RagNet.Mcp.Workspace;

public sealed class WorkspaceScopeResolver(
    IWorkspaceDetector workspaceDetector,
    IIndexedWorkspaceRegistry indexedWorkspaceRegistry,
    IWorkspaceGroupRegistry workspaceGroupRegistry) : IWorkspaceScopeResolver
{
    public async Task<IReadOnlyList<WorkspaceInfo>> ResolveAsync(
        string? filePath,
        string? scope,
        string? workspaceRoot,
        string? workspaceGroup,
        bool includeGroupedWorkspaces = false,
        CancellationToken cancellationToken = default)
    {
        var scopeKind = ParseScope(scope, workspaceRoot, workspaceGroup);

        return scopeKind switch
        {
            WorkspaceScopeKind.CurrentWorkspace => await ResolveWorkspaceAsync(Required(filePath, "file_path"), includeGroupedWorkspaces, cancellationToken),
            WorkspaceScopeKind.ExplicitWorkspaceRoot => await ResolveWorkspaceAsync(Required(workspaceRoot, "workspace_root"), includeGroupedWorkspaces, cancellationToken),
            WorkspaceScopeKind.NamedWorkspaceGroup => await ResolveGroupAsync(Required(workspaceGroup, "workspace_group"), cancellationToken),
            WorkspaceScopeKind.AllIndexedWorkspaces => (await indexedWorkspaceRegistry.GetIndexedWorkspaceRootsAsync(cancellationToken))
                .Select(root => new WorkspaceInfo(root, new DirectoryInfo(root).Name, null, null))
                .ToArray(),
            _ => throw new InvalidOperationException($"Unsupported workspace scope '{scope}'.")
        };
    }

    private async Task<IReadOnlyList<WorkspaceInfo>> ResolveWorkspaceAsync(
        string path,
        bool includeGroupedWorkspaces,
        CancellationToken cancellationToken)
    {
        var workspace = await workspaceDetector.DetectAsync(path, cancellationToken);
        if (!includeGroupedWorkspaces)
        {
            return [workspace];
        }

        var groupRoots = await GetGroupedWorkspaceRootsAsync(workspace.RootPath, cancellationToken);
        if (groupRoots.Count == 0)
        {
            return [workspace];
        }

        var resolved = new List<WorkspaceInfo> { workspace };
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizePath(workspace.RootPath)
        };

        foreach (var root in groupRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedRoot = NormalizePath(root);
            if (!seenRoots.Add(normalizedRoot))
            {
                continue;
            }

            resolved.Add(await workspaceDetector.DetectAsync(root, cancellationToken));
        }

        return resolved;
    }

    private async Task<IReadOnlyList<WorkspaceInfo>> ResolveGroupAsync(string groupName, CancellationToken cancellationToken)
    {
        var group = await workspaceGroupRegistry.GetGroupAsync(groupName, cancellationToken);
        if (group is null)
        {
            throw new InvalidOperationException($"Workspace group '{groupName}' was not found.");
        }

        var workspaces = new List<WorkspaceInfo>();
        foreach (var root in group.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            workspaces.Add(await workspaceDetector.DetectAsync(root, cancellationToken));
        }

        return workspaces;
    }

    private async Task<IReadOnlyList<string>> GetGroupedWorkspaceRootsAsync(string workspaceRoot, CancellationToken cancellationToken)
    {
        var normalizedWorkspaceRoot = NormalizePath(workspaceRoot);
        var groups = await workspaceGroupRegistry.GetGroupsAsync(cancellationToken);

        return groups
            .Where(group => group.Roots.Any(root => string.Equals(NormalizePath(root), normalizedWorkspaceRoot, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(group => group.Roots)
            .ToArray();
    }

    private static WorkspaceScopeKind ParseScope(string? scope, string? workspaceRoot, string? workspaceGroup)
    {
        if (!string.IsNullOrWhiteSpace(workspaceGroup))
        {
            return WorkspaceScopeKind.NamedWorkspaceGroup;
        }

        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return WorkspaceScopeKind.ExplicitWorkspaceRoot;
        }

        return scope?.Trim().ToLowerInvariant() switch
        {
            null or "" or "current" or "current_workspace" => WorkspaceScopeKind.CurrentWorkspace,
            "root" or "workspace_root" or "explicit_workspace_root" => WorkspaceScopeKind.ExplicitWorkspaceRoot,
            "group" or "workspace_group" or "named_workspace_group" => WorkspaceScopeKind.NamedWorkspaceGroup,
            "all" or "all_indexed" or "all_indexed_workspaces" => WorkspaceScopeKind.AllIndexedWorkspaces,
            _ => throw new ArgumentException("Scope must be one of: current_workspace, explicit_workspace_root, named_workspace_group, all_indexed_workspaces.")
        };
    }

    private static string Required(string? value, string name)
        => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} is required for this scope.") : value;

    private static string NormalizePath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
