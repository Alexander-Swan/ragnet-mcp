using Microsoft.Extensions.Options;
using RagNet.Mcp.Analyzers.Markup;
using RagNet.Mcp.Analyzers.Documentation;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Indexing;

namespace RagNet.Mcp.Tests;

public sealed class MarkupAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_PreservesRazorDirectivesBindingsAndCode()
    {
        using var workspace = new TemporaryWorkspace();
        var file = workspace.WriteFile(
            "Pages/Counter.razor",
            """
            @page "/counter"
            @inject NavigationManager Navigation

            <h1>Counter</h1>

            <button class="primary" @onclick="IncrementCount">@currentCount</button>
            <input @bind="searchText" />

            @code {
                private int currentCount;
                private string searchText = "";

                private void IncrementCount()
                {
                    currentCount++;
                }
            }
            """);

        var analyzer = new MarkupAnalyzer();
        var chunks = await analyzer.AnalyzeAsync(workspace.RootPath, file);

        var directives = Assert.Single(chunks, chunk => chunk.SymbolName == "markup directives");
        Assert.Equal("Directives", directives.SymbolKind);
        Assert.Equal(IndexedContentTypes.Markup, directives.ContentType);
        Assert.Contains("@page \"/counter\"", directives.Content);
        Assert.Contains("@inject NavigationManager", directives.Content);

        Assert.Contains(chunks, chunk => chunk.SymbolName == "button.primary" && chunk.SymbolKind == "BoundElement");
        Assert.Contains(chunks, chunk => chunk.SymbolName == "input" && chunk.SymbolKind == "BoundElement");

        var code = Assert.Single(chunks, chunk => chunk.SymbolName == "@code");
        Assert.Equal("CodeBlock", code.SymbolKind);
        Assert.Contains("IncrementCount", code.Content);
    }

    [Fact]
    public async Task AnalyzeAsync_PreservesXamlNamesEventsAndCssRules()
    {
        using var workspace = new TemporaryWorkspace();
        var xaml = workspace.WriteFile(
            "Views/MainWindow.xaml",
            """
            <Window x:Class="Demo.MainWindow">
                <Grid>
                    <Button x:Name="SaveButton" Click="Save_Click" Command="{Binding SaveCommand}" />
                </Grid>
            </Window>
            """);
        var css = workspace.WriteFile(
            "wwwroot/site.css",
            """
            .toolbar button {
                color: red;
            }

            @media (max-width: 600px) {
                .toolbar {
                    display: grid;
                }
            }
            """);

        var analyzer = new MarkupAnalyzer();
        var xamlChunks = await analyzer.AnalyzeAsync(workspace.RootPath, xaml);
        var cssChunks = await analyzer.AnalyzeAsync(workspace.RootPath, css);

        Assert.Contains(xamlChunks, chunk => chunk.SymbolName == "markup directives" && chunk.Content.Contains("x:Class=\"Demo.MainWindow\"", StringComparison.Ordinal));
        Assert.Contains(xamlChunks, chunk => chunk.SymbolName == "Button#SaveButton" && chunk.SymbolKind == "BoundElement");
        Assert.Contains(cssChunks, chunk => chunk.SymbolName == ".toolbar button" && chunk.SymbolKind == "StyleRule");
        Assert.Contains(cssChunks, chunk => chunk.SymbolName.StartsWith("@media", StringComparison.Ordinal) && chunk.SymbolKind == "StyleAtRule");
    }

    [Fact]
    public async Task MatchAsync_ScoresMdxWithReactComponentsAsMarkup()
    {
        using var workspace = new TemporaryWorkspace();
        var file = workspace.WriteFile(
            "src/components/Usage.mdx",
            """
            import { Alert } from './Alert';

            # Usage

            <Alert variant="warning" />
            <DemoPanel title="Options" />
            """);

        var markupAnalyzer = new MarkupAnalyzer();
        var documentationAnalyzer = new DocumentationAnalyzer();

        var markupMatch = await markupAnalyzer.MatchAsync(workspace.RootPath, file);
        var documentationMatch = await documentationAnalyzer.MatchAsync(workspace.RootPath, file);

        Assert.True(markupAnalyzer.CanAnalyze(file));
        Assert.True(markupMatch.Confidence > documentationMatch.Confidence);
    }

    [Fact]
    public async Task MatchAsync_UsesConfiguredPathOverridesForAmbiguousExtensions()
    {
        using var workspace = new TemporaryWorkspace();
        var docFile = workspace.WriteFile(
            "knowledge-base/page.html",
            """
            <section *ngIf="ready">
                <h1>Internal Cookbook</h1>
            </section>
            """);
        var appFile = workspace.WriteFile(
            "docs/live-templates/widget.html",
            """
            <article>
                <h1>Widget</h1>
                <p>This mostly looks like documentation unless the configured path says it is application markup.</p>
            </article>
            """);
        var options = Options.Create(new RagNetOptions
        {
            Classification = new ClassificationOptions
            {
                DocumentationPathPatterns = ["**/knowledge-base/**"],
                ApplicationMarkupPathPatterns = ["**/docs/live-templates/**"]
            }
        });

        var markupAnalyzer = new MarkupAnalyzer(options);
        var documentationAnalyzer = new DocumentationAnalyzer(options);

        var docFileMarkupMatch = await markupAnalyzer.MatchAsync(workspace.RootPath, docFile);
        var docFileDocumentationMatch = await documentationAnalyzer.MatchAsync(workspace.RootPath, docFile);
        var appFileMarkupMatch = await markupAnalyzer.MatchAsync(workspace.RootPath, appFile);
        var appFileDocumentationMatch = await documentationAnalyzer.MatchAsync(workspace.RootPath, appFile);

        Assert.True(docFileDocumentationMatch.Confidence > docFileMarkupMatch.Confidence);
        Assert.True(appFileMarkupMatch.Confidence > appFileDocumentationMatch.Confidence);
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public string RootPath { get; } = Path.Combine(Path.GetTempPath(), $"ragnet-tests-{Guid.NewGuid():N}");

        public TemporaryWorkspace()
        {
            Directory.CreateDirectory(RootPath);
        }

        public string WriteFile(string relativePath, string content)
        {
            var file = Path.Combine(RootPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
            return file;
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
