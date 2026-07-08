using Microsoft.Extensions.Options;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Workspace.Interfaces;

namespace RagNet.Mcp.Workspace;

public sealed class WorkspaceScopeResolver(
    IWorkspaceDetector workspaceDetector,
    IIndexedWorkspaceRegistry indexedWorkspaceRegistry,
    IOptions<RagNetOptions> options) : IWorkspaceScopeResolver
{
    private readonly RagNetOptions _options = options.Value;

    public async Task<IReadOnlyList<WorkspaceInfo>> ResolveAsync(
        string? filePath,
        string? scope,
        string? workspaceRoot,
        string? workspaceGroup,
        CancellationToken cancellationToken = default)
    {
        var scopeKind = ParseScope(scope, workspaceRoot, workspaceGroup);

        return scopeKind switch
        {
            WorkspaceScopeKind.CurrentWorkspace => [await workspaceDetector.DetectAsync(Required(filePath, "file_path"), cancellationToken)],
            WorkspaceScopeKind.ExplicitWorkspaceRoot => [await workspaceDetector.DetectAsync(Required(workspaceRoot, "workspace_root"), cancellationToken)],
            WorkspaceScopeKind.NamedWorkspaceGroup => await ResolveGroupAsync(Required(workspaceGroup, "workspace_group"), cancellationToken),
            WorkspaceScopeKind.AllIndexedWorkspaces => indexedWorkspaceRegistry.GetIndexedWorkspaceRoots()
                .Select(root => new WorkspaceInfo(root, new DirectoryInfo(root).Name, null, null))
                .ToArray(),
            _ => throw new InvalidOperationException($"Unsupported workspace scope '{scope}'.")
        };
    }

    private async Task<IReadOnlyList<WorkspaceInfo>> ResolveGroupAsync(string groupName, CancellationToken cancellationToken)
    {
        var group = _options.WorkspaceGroups.FirstOrDefault(candidate => string.Equals(candidate.Name, groupName, StringComparison.OrdinalIgnoreCase));
        if (group is null)
        {
            throw new InvalidOperationException($"Workspace group '{groupName}' was not found in configuration.");
        }

        var workspaces = new List<WorkspaceInfo>();
        foreach (var root in group.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            workspaces.Add(await workspaceDetector.DetectAsync(root, cancellationToken));
        }

        return workspaces;
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
}
