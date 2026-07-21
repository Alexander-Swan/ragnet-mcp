using Microsoft.Extensions.Logging.Abstractions;
using RagNet.Mcp.Source;

namespace RagNet.Mcp.Tests;

public sealed class TfsSourceChangeDetectorTests
{
    [Fact]
    public async Task DetectChangesAsync_UsesChangesetRangeAndPendingStatus()
    {
        using var workspace = new TemporaryWorkspace();
        var changed = workspace.WriteFile("src/Changed.cs", "changed");
        var added = workspace.WriteFile("src/Added.cs", "added");
        var deleted = Path.Combine(workspace.RootPath, "src", "Deleted.cs");
        var runner = new FakeTfsCommandRunner(workspace.RootPath)
        {
            LatestChangeset = 105,
            DetailedHistory = """
                -------------------------------------------------------------------------------
                Changeset: 105
                Items:
                  edit $/Product/src/Changed.cs
                  delete $/Product/src/Deleted.cs;X104
                """,
            Status = $"""
                Change: add
                Local item: {added}
                """
        };
        var detector = new TfsSourceChangeDetector(runner);

        var changeSet = await detector.DetectChangesAsync(
            workspace.RootPath,
            candidateFiles: null,
            previouslyIndexedFiles: [changed, deleted],
            previousCommitSha: "tfvc:100");

        Assert.True(changeSet.IsAvailable);
        Assert.True(changeSet.IsComplete);
        Assert.Contains(Path.GetFullPath(changed), changeSet.ChangedFiles);
        Assert.Contains(Path.GetFullPath(added), changeSet.ChangedFiles);
        Assert.Contains(Path.GetFullPath(deleted), changeSet.DeletedFiles);
        Assert.DoesNotContain(Path.GetFullPath(deleted), changeSet.ChangedFiles);
    }

    [Fact]
    public async Task SourceIdentityResolver_RecordsTfvcChangeset()
    {
        using var workspace = new TemporaryWorkspace();
        var file = workspace.WriteFile("src/Program.cs", "code");
        var runner = new FakeTfsCommandRunner(workspace.RootPath)
        {
            LatestChangeset = 321
        };
        var resolver = new TfsSourceIdentityResolver(runner, NullLogger<TfsSourceIdentityResolver>.Instance);

        var identity = await resolver.ResolveAsync(workspace.RootPath, file);

        Assert.Equal("tfvc:321", identity.CommitSha);
        Assert.Equal("$/Product", identity.Branch);
        Assert.False(identity.IsGitRepository);
        Assert.Equal("src/Program.cs", identity.RelativePath);
    }

    [Fact]
    public void ParseWorkfold_SelectsDeepestMappingContainingWorkspace()
    {
        var output = """
            Workspace: ProductWs;DOMAIN\User
            Collection: http://tfs:8080/tfs/DefaultCollection
             $/Product: C:\Code\Product
             $/Product/App: C:\Code\Product\App
            """;

        var workspace = TfsWorkspaceDiscovery.ParseWorkfold(output, @"C:\Code\Product\App\Api");

        Assert.NotNull(workspace);
        Assert.Equal("$/Product/App", workspace.ServerPath);
        Assert.Equal(@"C:\Code\Product\App", workspace.LocalPath);
    }

    private sealed class FakeTfsCommandRunner(string workspaceRoot) : ITfsCommandRunner
    {
        public int LatestChangeset { get; init; } = 100;

        public string DetailedHistory { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public Task<TfsCommandResult> RunAsync(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            var command = arguments.Count == 0 ? string.Empty : arguments[0];
            return Task.FromResult(command switch
            {
                "workfold" => Success($"""
                    Workspace: ProductWs;DOMAIN\User
                    Collection: http://tfs:8080/tfs/DefaultCollection
                     $/Product: {workspaceRoot}
                    """),
                "history" when arguments.Any(argument => argument.StartsWith("/stopafter", StringComparison.OrdinalIgnoreCase)) =>
                    Success($"""
                    Changeset User              Date       Comment
                    --------- ----------------- ---------- ----------------------------------------
                    {LatestChangeset}        DOMAIN\User       1/1/2026
                    """),
                "history" => Success(DetailedHistory),
                "status" => Success(Status),
                _ => new TfsCommandResult(1, string.Empty, $"Unexpected command: {string.Join(" ", arguments)}")
            });
        }

        private static TfsCommandResult Success(string output)
            => new(0, output, string.Empty);
    }
}
