using RagNet.Mcp.Indexing;
using RagNet.Mcp.Storage;

namespace RagNet.Mcp.Tests;

public sealed class InMemoryVectorStoreTests
{
    [Fact]
    public async Task SearchAsync_FiltersByContentType()
    {
        var store = new InMemoryVectorStore();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"ragnet-store-tests-{Guid.NewGuid():N}");
        var chunks = new[]
        {
            CreateChunk(workspaceRoot, "src/Program.cs", "csharp", "Program", IndexedContentTypes.Code),
            CreateChunk(workspaceRoot, "docs/readme.md", "markdown", "Overview", IndexedContentTypes.Documentation)
        };
        var embeddings = new[]
        {
            new[] { 1f, 0f },
            new[] { 1f, 0f }
        };

        await store.UpsertAsync(workspaceRoot, chunks, embeddings);

        var results = await store.SearchAsync(
            workspaceRoot,
            [1f, 0f],
            "overview",
            limit: 10,
            hybrid: false,
            contentType: IndexedContentTypes.Documentation);

        var result = Assert.Single(results);
        Assert.Equal(IndexedContentTypes.Documentation, result.ContentType);
        Assert.Equal("markdown", result.Language);
        Assert.EndsWith("readme.md", result.FilePath);
    }

    private static CodeChunk CreateChunk(string workspaceRoot, string relativePath, string language, string symbolName, string contentType)
        => new(
            relativePath,
            workspaceRoot,
            Path.Combine(workspaceRoot, relativePath),
            language,
            symbolName,
            "File",
            1,
            1,
            $"{symbolName} content")
        {
            ContentType = contentType
        };
}
