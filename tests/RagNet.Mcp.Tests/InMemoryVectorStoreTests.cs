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

    [Fact]
    public async Task SearchAsync_FiltersByIndexProfile()
    {
        var store = new InMemoryVectorStore();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"ragnet-store-tests-{Guid.NewGuid():N}");
        var chunks = new[]
        {
            CreateChunk(workspaceRoot, "src/Program.cs", "csharp", "Program", IndexedContentTypes.Code, IndexProfiles.Code),
            CreateChunk(workspaceRoot, "tests/ProgramTests.cs", "csharp", "ProgramTests", IndexedContentTypes.Code, IndexProfiles.Tests)
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
            "program",
            limit: 10,
            hybrid: false,
            indexProfile: IndexProfiles.Tests);

        var result = Assert.Single(results);
        Assert.Equal(IndexProfiles.Tests, result.IndexProfile);
        Assert.EndsWith("ProgramTests.cs", result.FilePath);
    }

    [Fact]
    public async Task DeleteByFilesAsync_RemovesAllMatchingFiles()
    {
        var store = new InMemoryVectorStore();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"ragnet-store-tests-{Guid.NewGuid():N}");
        var chunks = new[]
        {
            CreateChunk(workspaceRoot, "src/Program.cs", "csharp", "Program", IndexedContentTypes.Code),
            CreateChunk(workspaceRoot, "src/Other.cs", "csharp", "Other", IndexedContentTypes.Code),
            CreateChunk(workspaceRoot, "docs/readme.md", "markdown", "Overview", IndexedContentTypes.Documentation)
        };
        var embeddings = new[]
        {
            new[] { 1f, 0f },
            new[] { 1f, 0f },
            new[] { 1f, 0f }
        };

        await store.UpsertAsync(workspaceRoot, chunks, embeddings);
        await store.DeleteByFilesAsync(
            workspaceRoot,
            [
                Path.Combine(workspaceRoot, "src", "Program.cs"),
                Path.Combine(workspaceRoot, "docs", "readme.md")
            ]);

        var results = await store.SearchAsync(
            workspaceRoot,
            [1f, 0f],
            "content",
            limit: 10,
            hybrid: false);

        var result = Assert.Single(results);
        Assert.EndsWith("Other.cs", result.FilePath);
    }

    private static CodeChunk CreateChunk(
        string workspaceRoot,
        string relativePath,
        string language,
        string symbolName,
        string contentType,
        string indexProfile = IndexProfiles.Code)
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
            ContentType = contentType,
            IndexProfile = indexProfile
        };
}
