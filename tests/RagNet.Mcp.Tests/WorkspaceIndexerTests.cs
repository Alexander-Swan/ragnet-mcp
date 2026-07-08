using Microsoft.Extensions.Options;
using RagNet.Mcp.Analyzers.Interfaces;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Embeddings.Interfaces;
using RagNet.Mcp.Indexing;
using RagNet.Mcp.Indexing.Interfaces;
using RagNet.Mcp.Source;
using RagNet.Mcp.Source.Interfaces;
using RagNet.Mcp.Storage.Interfaces;
using RagNet.Mcp.Workspace;
using RagNet.Mcp.Workspace.Interfaces;

namespace RagNet.Mcp.Tests;

public sealed class WorkspaceIndexerTests
{
    private const string CurrentSchemaVersion = "ragnet-index-v2/analyzers-v8";

    [Fact]
    public async Task IndexAsync_ReindexesOnlyChangedFilesAndDeletesRemovedFiles()
    {
        using var workspace = new TemporaryWorkspace();
        var unchanged = workspace.WriteFile("src/Unchanged.cs", "unchanged");
        var changed = workspace.WriteFile("src/Changed.cs", "changed");
        var removed = Path.Combine(workspace.RootPath, "src", "Removed.cs");
        var stateStore = new FakeStateStore(State(
            workspace.RootPath,
            FileState(unchanged),
            FileState(changed, fingerprint: "old-fingerprint"),
            new IndexedFileState(Path.GetFullPath(removed), "removed-fingerprint", 10, DateTimeOffset.UtcNow)));
        var vectorStore = new FakeVectorStore();
        var analyzer = new FakeAnalyzer();
        var indexer = CreateIndexer(workspace.RootPath, stateStore, vectorStore, analyzer);

        var result = await indexer.IndexAsync(workspace.RootPath);

        Assert.False(result.FullReindex);
        Assert.Equal(1, result.ChunksIndexed);
        Assert.Equal([Path.GetFullPath(removed), Path.GetFullPath(changed)], vectorStore.DeletedFiles);
        Assert.Equal([Path.GetFullPath(changed)], analyzer.AnalyzedFiles);
        Assert.Single(vectorStore.UpsertedChunks);
        Assert.Equal(Path.GetFullPath(changed), vectorStore.UpsertedChunks[0].FilePath);
        Assert.Contains(Path.GetFullPath(unchanged), stateStore.SavedState!.Files.Keys);
        Assert.Contains(Path.GetFullPath(changed), stateStore.SavedState.Files.Keys);
    }

    [Fact]
    public async Task IndexAsync_ProfileScopedRunPreservesOtherProfileState()
    {
        using var workspace = new TemporaryWorkspace();
        var codeFile = workspace.WriteFile("src/Program.cs", "code");
        var docsFile = workspace.WriteFile("docs/guide.md", "docs v2");
        var stateStore = new FakeStateStore(State(
            workspace.RootPath,
            FileState(codeFile),
            FileState(docsFile, fingerprint: "old-docs-fingerprint")));
        var vectorStore = new FakeVectorStore();
        var analyzer = new FakeAnalyzer();
        var indexer = CreateIndexer(workspace.RootPath, stateStore, vectorStore, analyzer);

        var result = await indexer.IndexAsync(workspace.RootPath, indexProfile: IndexProfiles.Documentation);

        Assert.False(result.FullReindex);
        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(1, result.ChunksIndexed);
        Assert.Equal([Path.GetFullPath(docsFile)], analyzer.AnalyzedFiles);
        Assert.Equal([Path.GetFullPath(docsFile)], vectorStore.DeletedFiles);
        Assert.Contains(Path.GetFullPath(codeFile), stateStore.SavedState!.Files.Keys);
        Assert.Contains(Path.GetFullPath(docsFile), stateStore.SavedState.Files.Keys);
    }

    [Fact]
    public async Task IndexAsync_ForceProfileScopedRunReindexesAllFilesInProfile()
    {
        using var workspace = new TemporaryWorkspace();
        var codeFile = workspace.WriteFile("src/Program.cs", "code");
        var docsFile = workspace.WriteFile("docs/guide.md", "docs");
        var stateStore = new FakeStateStore(State(
            workspace.RootPath,
            FileState(codeFile),
            FileState(docsFile)));
        var vectorStore = new FakeVectorStore();
        var analyzer = new FakeAnalyzer();
        var indexer = CreateIndexer(workspace.RootPath, stateStore, vectorStore, analyzer);

        var result = await indexer.IndexAsync(workspace.RootPath, force: true, indexProfile: IndexProfiles.Documentation);

        Assert.True(result.FullReindex);
        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(1, result.ChunksIndexed);
        Assert.False(vectorStore.WorkspaceDeleted);
        Assert.Equal([Path.GetFullPath(docsFile)], analyzer.AnalyzedFiles);
        Assert.Equal([Path.GetFullPath(docsFile)], vectorStore.DeletedFiles);
        Assert.Contains(Path.GetFullPath(codeFile), stateStore.SavedState!.Files.Keys);
    }

    [Fact]
    public async Task IndexAsync_IncompatibleAllProfileStateDeletesWorkspaceAndReindexesAllFiles()
    {
        using var workspace = new TemporaryWorkspace();
        var codeFile = workspace.WriteFile("src/Program.cs", "code");
        var docsFile = workspace.WriteFile("docs/guide.md", "docs");
        var stateStore = new FakeStateStore(State(
            workspace.RootPath,
            schemaVersion: "old-schema",
            FileState(codeFile),
            FileState(docsFile)));
        var vectorStore = new FakeVectorStore();
        var analyzer = new FakeAnalyzer();
        var indexer = CreateIndexer(workspace.RootPath, stateStore, vectorStore, analyzer);

        var result = await indexer.IndexAsync(workspace.RootPath);

        Assert.True(result.FullReindex);
        Assert.True(vectorStore.WorkspaceDeleted);
        Assert.Equal(2, result.ChunksIndexed);
        Assert.Equal(
            new[] { Path.GetFullPath(codeFile), Path.GetFullPath(docsFile) }.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            analyzer.AnalyzedFiles.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(CurrentSchemaVersion, stateStore.SavedState!.SchemaVersion);
    }

    [Fact]
    public async Task IndexAsync_ProfileScopedRunRequiresCompatibleState()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.WriteFile("docs/guide.md", "docs");
        var stateStore = new FakeStateStore(State(workspace.RootPath, schemaVersion: "old-schema"));
        var indexer = CreateIndexer(workspace.RootPath, stateStore, new FakeVectorStore(), new FakeAnalyzer());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            indexer.IndexAsync(workspace.RootPath, indexProfile: IndexProfiles.Documentation));

        Assert.Contains("Profile-scoped indexing requires a compatible existing index state", exception.Message);
    }

    [Fact]
    public async Task SearchAsync_ForwardsFiltersOverfetchesAndPacksResults()
    {
        using var workspace = new TemporaryWorkspace();
        var file = workspace.WriteFile("tests/SearchTests.cs", "code");
        var vectorStore = new FakeVectorStore
        {
            SearchResults =
            [
                Result(file, "SearchTests", 1, 5, 0.9, "first", IndexProfiles.Tests),
                Result(file, "SearchTests", 5, 8, 0.8, "second", IndexProfiles.Tests),
                Result(file, "OtherTests", 20, 25, 0.7, "third", IndexProfiles.Tests)
            ]
        };
        var indexer = CreateIndexer(workspace.RootPath, new FakeStateStore(State(workspace.RootPath)), vectorStore, new FakeAnalyzer());

        var results = await indexer.SearchAsync(
            file,
            "search tests",
            limit: 2,
            hybrid: true,
            scope: null,
            workspaceRoot: null,
            workspaceGroup: null,
            contentType: IndexedContentTypes.Code,
            retrievalMode: RetrievalModes.CodeFirst,
            searchProfile: IndexProfiles.Tests);

        Assert.Equal(10, vectorStore.LastSearchLimit);
        Assert.True(vectorStore.LastSearchHybrid);
        Assert.Equal(IndexedContentTypes.Code, vectorStore.LastSearchContentType);
        Assert.Equal(IndexProfiles.Tests, vectorStore.LastSearchIndexProfile);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, result => result.SymbolName == "SearchTests" && result.StartLine == 1 && result.EndLine == 8);
    }

    private static WorkspaceIndexer CreateIndexer(
        string workspaceRoot,
        FakeStateStore stateStore,
        FakeVectorStore vectorStore,
        FakeAnalyzer analyzer)
        => new(
            new FakeWorkspaceDetector(workspaceRoot),
            new FakeWorkspaceScopeResolver(workspaceRoot),
            new FakeIndexedWorkspaceRegistry(),
            [analyzer],
            stateStore,
            new FakeEmbeddingProvider(),
            vectorStore,
            new FakeSourceIdentityResolver(),
            Options.Create(new RagNetOptions()));

    private static WorkspaceIndexState State(
        string workspaceRoot,
        params IndexedFileState[] files)
        => State(workspaceRoot, CurrentSchemaVersion, files);

    private static WorkspaceIndexState State(
        string workspaceRoot,
        string? schemaVersion,
        params IndexedFileState[] files)
        => new(
            Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            files.ToDictionary(file => file.FilePath, StringComparer.OrdinalIgnoreCase),
            "mxbai-embed-large",
            schemaVersion,
            DateTimeOffset.UtcNow,
            StateExists: true);

    private static IndexedFileState FileState(string filePath, string? fingerprint = null)
    {
        var fullPath = Path.GetFullPath(filePath);
        var info = new FileInfo(fullPath);
        return new IndexedFileState(
            fullPath,
            fingerprint ?? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fullPath))),
            info.Length,
            info.LastWriteTimeUtc);
    }

    private static SearchResult Result(
        string filePath,
        string symbolName,
        int startLine,
        int endLine,
        double score,
        string preview,
        string indexProfile)
        => new(Path.GetFullPath(filePath), symbolName, "Method", startLine, endLine, score, preview)
        {
            ContentType = IndexedContentTypes.Code,
            IndexProfile = indexProfile,
            Language = "csharp"
        };

    private sealed class FakeWorkspaceDetector(string workspaceRoot) : IWorkspaceDetector
    {
        public Task<WorkspaceInfo> DetectAsync(string filePath, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceInfo(workspaceRoot, "test", null, null));
    }

    private sealed class FakeWorkspaceScopeResolver(string workspaceRoot) : IWorkspaceScopeResolver
    {
        private readonly string _workspaceRoot = workspaceRoot;

        public Task<IReadOnlyList<WorkspaceInfo>> ResolveAsync(
            string? filePath,
            string? scope,
            string? workspaceRoot,
            string? workspaceGroup,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceInfo>>([new WorkspaceInfo(_workspaceRoot, "test", null, null)]);
    }

    private sealed class FakeIndexedWorkspaceRegistry : IIndexedWorkspaceRegistry
    {
        public void MarkIndexed(string workspaceRoot)
        {
        }

        public IReadOnlyList<string> GetIndexedWorkspaceRoots() => [];
    }

    private sealed class FakeStateStore(WorkspaceIndexState state) : IWorkspaceIndexStateStore
    {
        public WorkspaceIndexState? SavedState { get; private set; }

        public Task<WorkspaceIndexState> LoadAsync(string workspaceRoot, CancellationToken cancellationToken = default)
            => Task.FromResult(state);

        public Task SaveAsync(WorkspaceIndexState state, CancellationToken cancellationToken = default)
        {
            SavedState = state;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult(new[] { 1f, 0f });
    }

    private sealed class FakeVectorStore : IVectorStore
    {
        public List<string> DeletedFiles { get; } = [];

        public List<CodeChunk> UpsertedChunks { get; } = [];

        public bool WorkspaceDeleted { get; private set; }

        public IReadOnlyList<SearchResult> SearchResults { get; init; } = [];

        public int LastSearchLimit { get; private set; }

        public bool LastSearchHybrid { get; private set; }

        public string? LastSearchContentType { get; private set; }

        public string? LastSearchIndexProfile { get; private set; }

        public Task UpsertAsync(string workspaceRoot, IReadOnlyList<CodeChunk> chunks, IReadOnlyList<float[]> embeddings, CancellationToken cancellationToken = default)
        {
            UpsertedChunks.AddRange(chunks);
            return Task.CompletedTask;
        }

        public Task DeleteByFileAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default)
        {
            DeletedFiles.Add(Path.GetFullPath(filePath));
            return Task.CompletedTask;
        }

        public Task DeleteWorkspaceAsync(string workspaceRoot, CancellationToken cancellationToken = default)
        {
            WorkspaceDeleted = true;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            string workspaceRoot,
            float[] embedding,
            string query,
            int limit,
            bool hybrid,
            string? contentType = null,
            string? indexProfile = null,
            CancellationToken cancellationToken = default)
        {
            LastSearchLimit = limit;
            LastSearchHybrid = hybrid;
            LastSearchContentType = contentType;
            LastSearchIndexProfile = indexProfile;
            return Task.FromResult(SearchResults);
        }
    }

    private sealed class FakeAnalyzer : ICodeAnalyzer
    {
        public List<string> AnalyzedFiles { get; } = [];

        public bool CanAnalyze(string filePath)
            => Path.GetExtension(filePath) is ".cs" or ".md";

        public Task<IReadOnlyList<CodeChunk>> AnalyzeAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default)
        {
            var fullPath = Path.GetFullPath(filePath);
            AnalyzedFiles.Add(fullPath);
            var contentType = string.Equals(Path.GetExtension(fullPath), ".md", StringComparison.OrdinalIgnoreCase)
                ? IndexedContentTypes.Documentation
                : IndexedContentTypes.Code;
            return Task.FromResult<IReadOnlyList<CodeChunk>>(
            [
                new CodeChunk(
                    $"{Path.GetRelativePath(workspaceRoot, fullPath)}:1:1:chunk",
                    workspaceRoot,
                    fullPath,
                    contentType == IndexedContentTypes.Documentation ? "markdown" : "csharp",
                    Path.GetFileNameWithoutExtension(fullPath),
                    "File",
                    1,
                    1,
                    File.ReadAllText(fullPath))
                {
                    ContentType = contentType
                }
            ]);
        }
    }

    private sealed class FakeSourceIdentityResolver : ISourceIdentityResolver
    {
        public Task<SourceIdentity> ResolveAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default)
            => Task.FromResult(SourceIdentity.Local(workspaceRoot, filePath));
    }
}
