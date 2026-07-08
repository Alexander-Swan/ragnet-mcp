using RagNet.Mcp.Analyzers.Documentation;
using RagNet.Mcp.Analyzers.Markup;
using RagNet.Mcp.Indexing;

namespace RagNet.Mcp.Tests;

public sealed class DocumentationAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_CreatesChunksForMarkdownSectionsWithContentType()
    {
        using var workspace = new TemporaryWorkspace();
        var file = workspace.WriteFile(
            "docs/guide.md",
            """
            # Install

            Run the installer.

            ## Configure

            Set the endpoint.
            """);

        var analyzer = new DocumentationAnalyzer();
        var chunks = await analyzer.AnalyzeAsync(workspace.RootPath, file);

        Assert.True(analyzer.CanAnalyze(file));
        Assert.Collection(
            chunks,
            first =>
            {
                Assert.Equal("Install", first.SymbolName);
                Assert.Equal("DocumentSection", first.SymbolKind);
                Assert.Equal("markdown", first.Language);
                Assert.Equal(IndexedContentTypes.Documentation, first.ContentType);
                Assert.Contains("Run the installer.", first.Content);
            },
            second =>
            {
                Assert.Equal("Configure", second.SymbolName);
                Assert.Equal(IndexedContentTypes.Documentation, second.ContentType);
                Assert.Contains("Set the endpoint.", second.Content);
            });
    }

    [Fact]
    public async Task AnalyzeAsync_FallsBackToWholeTextDocumentWithoutHeadings()
    {
        using var workspace = new TemporaryWorkspace();
        var file = workspace.WriteFile("notes.txt", "plain operational note");

        var analyzer = new DocumentationAnalyzer();
        var chunks = await analyzer.AnalyzeAsync(workspace.RootPath, file);

        var chunk = Assert.Single(chunks);
        Assert.Equal("notes.txt", chunk.SymbolName);
        Assert.Equal("Document", chunk.SymbolKind);
        Assert.Equal("text", chunk.Language);
        Assert.Equal(IndexedContentTypes.Documentation, chunk.ContentType);
    }

    [Fact]
    public async Task MatchAsync_ScoresAmbiguousHtmlFromContentAndPath()
    {
        using var workspace = new TemporaryWorkspace();
        var docHtml = workspace.WriteFile(
            "docs/reference/index.html",
            """
            <article>
                <h1>Reference</h1>
                <p>This page explains how to configure the public API and includes enough prose to look like documentation.</p>
            </article>
            """);
        var appHtml = workspace.WriteFile(
            "src/app/user-view.html",
            """
            <section *ngIf="user">
                <button (click)="save()" [disabled]="saving">Save</button>
            </section>
            """);

        var documentationAnalyzer = new DocumentationAnalyzer();
        var markupAnalyzer = new MarkupAnalyzer();

        Assert.True(documentationAnalyzer.CanAnalyze(docHtml));
        Assert.True(documentationAnalyzer.CanAnalyze(appHtml));

        var docHtmlMatch = await documentationAnalyzer.MatchAsync(workspace.RootPath, docHtml);
        var docHtmlMarkupMatch = await markupAnalyzer.MatchAsync(workspace.RootPath, docHtml);
        var appHtmlDocMatch = await documentationAnalyzer.MatchAsync(workspace.RootPath, appHtml);
        var appHtmlMarkupMatch = await markupAnalyzer.MatchAsync(workspace.RootPath, appHtml);

        Assert.True(docHtmlMatch.Confidence > docHtmlMarkupMatch.Confidence);
        Assert.True(appHtmlMarkupMatch.Confidence > appHtmlDocMatch.Confidence);
    }
}
