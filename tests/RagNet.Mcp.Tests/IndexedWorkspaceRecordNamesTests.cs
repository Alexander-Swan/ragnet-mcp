using RagNet.Mcp.Workspace;

namespace RagNet.Mcp.Tests;

public sealed class IndexedWorkspaceRecordNamesTests
{
    [Fact]
    public void WithCalculatedNames_AddsDirectoryWorkspaceShortName()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "ragnet-alias-product");
        var record = WorkspaceRecord(workspaceRoot, [workspaceRoot]).WithCalculatedNames();

        Assert.Equal("ragnet-alias-product", record.DisplayName);
        Assert.Contains("ragnet-alias-product", record.EffectiveAliases);
        Assert.True(IndexedWorkspaceRecordNames.MatchesAlias(record, "ragnet-alias-product"));
        Assert.Equal(workspaceRoot, IndexedWorkspaceRecordNames.GetIndexTargetForAlias(record, "ragnet-alias-product"));
    }

    [Theory]
    [InlineData("Api")]
    [InlineData("Api.sln")]
    public void WithCalculatedNames_AddsSolutionFilenameAliases(string alias)
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "ragnet-solution-product");
        var solution = Path.Combine(workspaceRoot, "Api.sln");
        var record = WorkspaceRecord(workspaceRoot, [solution]).WithCalculatedNames();

        Assert.Contains("Api", record.EffectiveAliases);
        Assert.Contains("Api.sln", record.EffectiveAliases);
        Assert.True(IndexedWorkspaceRecordNames.MatchesAlias(record, alias));
        Assert.Equal(solution, IndexedWorkspaceRecordNames.GetIndexTargetForAlias(record, alias));
    }

    private static IndexedWorkspaceRecord WorkspaceRecord(string workspaceRoot, IReadOnlyList<string> indexedTargets)
        => new(
            workspaceRoot,
            "workspace-id",
            "collection",
            [],
            indexedTargets,
            DateTimeOffset.UtcNow,
            FilesScanned: 1,
            ChunksIndexed: 1,
            FullReindex: false);
}
