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

    [Fact]
    public async Task AnalyzeAsync_UsesConservativeChunksForSolutionFiles()
    {
        using var workspace = new TemporaryWorkspace();
        var projects = string.Join(Environment.NewLine, Enumerable.Range(0, 20)
            .Select(index =>
                "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = " +
                $"\"Project{index}\", \"src\\Project{index}\\Project{index}.csproj\", " +
                $"\"{{11111111-2222-3333-4444-{index:000000000000}}}\""));
        var file = workspace.WriteFile(
            "Sample.sln",
            $$"""
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            {{projects}}
            Global
            EndGlobal
            """);

        var analyzer = new ProjectMetadataAnalyzer();
        var chunks = await analyzer.AnalyzeAsync(workspace.RootPath, file);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk =>
        {
            Assert.Equal("solution", chunk.Language);
            Assert.Equal(IndexedContentTypes.ProjectMetadata, chunk.ContentType);
            Assert.True(chunk.Content.Length <= 700);
        });
    }

    [Fact]
    public async Task AnalyzeAsync_SplitsSingleLongMetadataLines()
    {
        using var workspace = new TemporaryWorkspace();
        var file = workspace.WriteFile(
            "appsettings.json",
            $$"""
            {
              "LargeKey": "{{new string('x', 1800)}}"
            }
            """);

        var analyzer = new ProjectMetadataAnalyzer();
        var chunks = await analyzer.AnalyzeAsync(workspace.RootPath, file);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.Content.Length <= 700));
    }

    [Fact]
    public async Task AnalyzeAsync_SplitsDenseMetadataByEstimatedTokens()
    {
        using var workspace = new TemporaryWorkspace();
        var file = workspace.WriteFile(
            ".editorconfig",
            $"""
            root = true
            punctuation = {new string(';', 500)}
            """);

        var analyzer = new ProjectMetadataAnalyzer();
        var chunks = await analyzer.AnalyzeAsync(workspace.RootPath, file);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.Content.Length <= 700));
    }
}
