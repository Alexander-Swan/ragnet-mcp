using RagNet.Mcp.Analyzers.DotNet;
using RagNet.Mcp.Indexing;

namespace RagNet.Mcp.Tests;

public sealed class ProjectMetadataAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_ClassifiesCsprojAsDotNetProject()
    {
        using var workspace = new TemporaryWorkspace();
        var file = workspace.WriteFile(
            "src/App/App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var analyzer = new ProjectMetadataAnalyzer();
        var chunks = await analyzer.AnalyzeAsync(workspace.RootPath, file);

        var chunk = Assert.Single(chunks);
        Assert.True(analyzer.CanAnalyze(file));
        Assert.Equal("xml", chunk.Language);
        Assert.Equal("DotNetProject", chunk.SymbolKind);
        Assert.Equal(IndexedContentTypes.ProjectMetadata, chunk.ContentType);
        Assert.Contains("TargetFramework", chunk.Content);
    }

    [Fact]
    public void CanAnalyze_IncludesReasonableDotNetMetadataAndDefersFSharpAndVisualBasicProjects()
    {
        var analyzer = new ProjectMetadataAnalyzer();

        Assert.True(analyzer.CanAnalyze(Path.Combine("repo", "Directory.Packages.props")));
        Assert.True(analyzer.CanAnalyze(Path.Combine("repo", "global.json")));
        Assert.True(analyzer.CanAnalyze(Path.Combine("repo", ".github", "workflows", "ci.yml")));
        Assert.True(analyzer.CanAnalyze(Path.Combine("repo", "Dockerfile")));
        Assert.False(analyzer.CanAnalyze(Path.Combine("repo", "Library.fsproj")));
        Assert.False(analyzer.CanAnalyze(Path.Combine("repo", "Legacy.vbproj")));
    }
}
