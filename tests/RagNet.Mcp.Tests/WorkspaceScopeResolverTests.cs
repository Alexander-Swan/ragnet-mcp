using RagNet.Mcp.Workspace;
using RagNet.Mcp.Workspace.Interfaces;

namespace RagNet.Mcp.Tests;

public sealed class WorkspaceScopeResolverTests
{
    [Fact]
    public async Task ResolveAsync_CurrentWorkspaceDoesNotExpandGroupsByDefault()
    {
        using var workspace = new TemporaryWorkspace();
        var apiRoot = CreateDirectory(workspace, "Api");
        var webRoot = CreateDirectory(workspace, "Web");
        var file = workspace.WriteFile("Api/Program.cs", "code");
        var resolver = CreateResolver(
            apiRoot,
            new WorkspaceGroupRecord("product", WorkspaceGroupSources.Shared, [apiRoot, webRoot], [], IsReadOnly: false));

        var workspaces = await resolver.ResolveAsync(file, scope: null, workspaceRoot: null, workspaceGroup: null);

        var resolved = Assert.Single(workspaces);
        Assert.Equal(Normalize(apiRoot), Normalize(resolved.RootPath));
    }

    [Fact]
    public async Task ResolveAsync_CurrentWorkspaceExpandsToGroupedWorkspacesWhenEnabled()
    {
        using var workspace = new TemporaryWorkspace();
        var apiRoot = CreateDirectory(workspace, "Api");
        var webRoot = CreateDirectory(workspace, "Web");
        var workerRoot = CreateDirectory(workspace, "Worker");
        var docsRoot = CreateDirectory(workspace, "Docs");
        var file = workspace.WriteFile("Api/Program.cs", "code");
        var resolver = CreateResolver(
            apiRoot,
            [
                new WorkspaceGroupRecord("product", WorkspaceGroupSources.Shared, [apiRoot, webRoot], [], IsReadOnly: false),
                new WorkspaceGroupRecord("platform", WorkspaceGroupSources.Shared, [apiRoot, workerRoot], [], IsReadOnly: false),
                new WorkspaceGroupRecord("docs", WorkspaceGroupSources.Shared, [docsRoot], [], IsReadOnly: false)
            ]);

        var workspaces = await resolver.ResolveAsync(
            file,
            scope: null,
            workspaceRoot: null,
            workspaceGroup: null,
            includeGroupedWorkspaces: true);

        Assert.Equal(
            [Normalize(apiRoot), Normalize(webRoot), Normalize(workerRoot)],
            workspaces.Select(workspaceInfo => Normalize(workspaceInfo.RootPath)).ToArray());
    }

    [Fact]
    public async Task ResolveAsync_ExplicitWorkspaceRootExpandsToGroupedWorkspacesWhenEnabled()
    {
        using var workspace = new TemporaryWorkspace();
        var apiRoot = CreateDirectory(workspace, "Api");
        var webRoot = CreateDirectory(workspace, "Web");
        var resolver = CreateResolver(
            apiRoot,
            [new WorkspaceGroupRecord("product", WorkspaceGroupSources.Shared, [apiRoot, webRoot], [], IsReadOnly: false)]);

        var workspaces = await resolver.ResolveAsync(
            filePath: null,
            scope: "explicit_workspace_root",
            workspaceRoot: apiRoot,
            workspaceGroup: null,
            includeGroupedWorkspaces: true);

        Assert.Equal(
            [Normalize(apiRoot), Normalize(webRoot)],
            workspaces.Select(workspaceInfo => Normalize(workspaceInfo.RootPath)).ToArray());
    }

    private static WorkspaceScopeResolver CreateResolver(
        string detectedWorkspaceRoot,
        params WorkspaceGroupRecord[] groups)
        => new(
            new FakeWorkspaceDetector(detectedWorkspaceRoot),
            new FakeIndexedWorkspaceRegistry(),
            new FakeWorkspaceGroupRegistry(groups));

    private static string Normalize(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string CreateDirectory(TemporaryWorkspace workspace, string relativePath)
    {
        var path = Path.Combine(workspace.RootPath, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeWorkspaceDetector(string currentWorkspaceRoot) : IWorkspaceDetector
    {
        public Task<WorkspaceInfo> DetectAsync(string filePath, CancellationToken cancellationToken = default)
        {
            var fullPath = Normalize(filePath);
            var matchingRoot = _knownRoots
                .OrderByDescending(root => root.Length)
                .FirstOrDefault(root => fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                ?? Normalize(currentWorkspaceRoot);

            return Task.FromResult(new WorkspaceInfo(matchingRoot, new DirectoryInfo(matchingRoot).Name, null, null));
        }

        private readonly IReadOnlyList<string> _knownRoots = Directory.Exists(Path.GetDirectoryName(currentWorkspaceRoot))
            ? Directory.GetDirectories(Path.GetDirectoryName(currentWorkspaceRoot)!)
                .Select(Normalize)
                .ToArray()
            : [Normalize(currentWorkspaceRoot)];
    }

    private sealed class FakeIndexedWorkspaceRegistry : IIndexedWorkspaceRegistry
    {
        public Task MarkIndexedAsync(IndexedWorkspaceRecord record, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<string>> GetIndexedWorkspaceRootsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<IndexedWorkspaceRecord>> GetIndexedWorkspacesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IndexedWorkspaceRecord>>([]);

        public Task DeleteWorkspaceAsync(string workspaceRoot, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeWorkspaceGroupRegistry(IReadOnlyList<WorkspaceGroupRecord> groups) : IWorkspaceGroupRegistry
    {
        public Task<IReadOnlyList<WorkspaceGroupRecord>> GetGroupsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceGroupRecord>>(groups);

        public Task<WorkspaceGroupRecord?> GetGroupAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(groups.FirstOrDefault(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase)));

        public Task<WorkspaceGroupRecord> SaveGroupAsync(
            string name,
            IReadOnlyList<string> roots,
            IReadOnlyList<string>? excludeDirectories = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceGroupRecord(name, WorkspaceGroupSources.Shared, roots, excludeDirectories ?? [], IsReadOnly: false));

        public Task DeleteGroupAsync(string name, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
