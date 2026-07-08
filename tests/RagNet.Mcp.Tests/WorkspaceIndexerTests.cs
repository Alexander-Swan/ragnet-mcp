using Microsoft.Extensions.Options;
using RagNet.Mcp.Analyzers.Interfaces;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Embeddings;
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
    public async Task DryRunIndexTargetsAsync_AnalyzesChangedFilesButDoesNotMutateStores()
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
        var embeddingProvider = new FakeEmbeddingProvider();
        var registry = new FakeIndexedWorkspaceRegistry();
        var indexer = CreateIndexer(workspace.RootPath, stateStore, vectorStore, analyzer, registry, embeddingProvider);

        var result = Assert.Single(await indexer.DryRunIndexTargetsAsync([workspace.RootPath]));

        Assert.Equal(Path.GetFullPath(workspace.RootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), result.WorkspaceRoot);
        Assert.Equal(IndexProfiles.All, result.IndexProfile);
        Assert.Equal(2, result.FilesScanned);
        Assert.Equal(1, result.ChunksThatWouldBeIndexed);
        Assert.False(result.FullReindex);
        Assert.True(result.StateCompatible);
        Assert.Equal(1, result.ChangedFiles);
        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(1, result.UnchangedFiles);
        Assert.Equal([Path.GetFullPath(changed)], analyzer.AnalyzedFiles);
        Assert.Empty(vectorStore.DeletedFiles);
        Assert.Empty(vectorStore.UpsertedChunks);
        Assert.False(vectorStore.WorkspaceDeleted);
        Assert.Null(stateStore.SavedState);
        Assert.Empty(registry.Records);
        Assert.Equal(0, embeddingProvider.EmbedCallCount);
    }

    [Fact]
    public async Task IndexTargetsAsync_BareWorkspaceNameResolvesIndexedWorkspaceRoot()
    {
        using var workspace = new TemporaryWorkspace();
        var sourceFile = workspace.WriteFile("src/Program.cs", "program");
        var analyzer = new FakeAnalyzer();
        var registry = new FakeIndexedWorkspaceRegistry();
        registry.Records.Add(IndexedWorkspace(workspace.RootPath));
        var indexer = CreateIndexer(
            workspace.RootPath,
            new FakeStateStore(State(workspace.RootPath)),
            new FakeVectorStore(),
            analyzer,
            registry);

        var result = Assert.Single(await indexer.IndexTargetsAsync([Path.GetFileName(workspace.RootPath)]));

        Assert.Equal(Path.GetFullPath(workspace.RootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), result.WorkspaceRoot);
        Assert.Equal([Path.GetFullPath(sourceFile)], analyzer.AnalyzedFiles);
    }

    [Fact]
    public async Task IndexTargetsAsync_UnindexedBareWorkspaceNameRequiresFullPath()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.WriteFile("src/Program.cs", "program");
        var indexer = CreateIndexer(
            workspace.RootPath,
            new FakeStateStore(State(workspace.RootPath)),
            new FakeVectorStore(),
            new FakeAnalyzer());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => indexer.IndexTargetsAsync([Path.GetFileName(workspace.RootPath)]));

        Assert.Contains("Use a full path for the first index", exception.Message);
    }

    [Fact]
    public async Task IndexTargetsAsync_EmbeddingFailuresDoNotShiftChunkEmbeddingAlignment()
    {
        using var workspace = new TemporaryWorkspace();
        var first = workspace.WriteFile("src/First.cs", "first");
        var skipped = workspace.WriteFile("src/Skipped.cs", "skip");
        var third = workspace.WriteFile("src/Third.cs", "third");
        var vectorStore = new FakeVectorStore();
        var embeddingProvider = new FakeEmbeddingProvider((text, _) =>
        {
            if (text == "skip")
            {
                throw new HttpRequestException("embedding failed");
            }

            return Task.FromResult(new[] { text == "first" ? 1f : 3f, 0f });
        });
        var indexer = CreateIndexer(
            workspace.RootPath,
            new FakeStateStore(State(workspace.RootPath)),
            vectorStore,
            new FakeAnalyzer(),
            embeddingProvider: embeddingProvider);

        var result = Assert.Single(await indexer.IndexTargetsAsync([first, skipped, third]));

        Assert.Equal(2, result.ChunksIndexed);
        Assert.Single(result.Warnings);
        Assert.Equal([Path.GetFullPath(first), Path.GetFullPath(third)], vectorStore.UpsertedChunks.Select(chunk => chunk.FilePath).ToArray());
        Assert.Equal([1f, 3f], vectorStore.UpsertedEmbeddings.Select(embedding => embedding[0]).ToArray());
    }

    [Fact]
    public async Task IndexTargetsAsync_MissingEmbeddingModelFailsFast()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.WriteFile("src/First.cs", "first");
        workspace.WriteFile("src/Second.cs", "second");
        var vectorStore = new FakeVectorStore();
        var embeddingProvider = new FakeEmbeddingProvider((_, _) =>
            throw new EmbeddingModelNotFoundException("missing-model", "model missing"));
        var indexer = CreateIndexer(
            workspace.RootPath,
            new FakeStateStore(State(workspace.RootPath)),
            vectorStore,
            new FakeAnalyzer(),
            embeddingProvider: embeddingProvider);

        var exception = await Assert.ThrowsAsync<EmbeddingModelNotFoundException>(
            () => indexer.IndexTargetsAsync([workspace.RootPath]));

        Assert.Equal("missing-model", exception.Model);
        Assert.Empty(vectorStore.UpsertedChunks);
    }

    [Fact]
    public async Task IndexAsync_EmbeddingConcurrencyHonorsConfiguredCap()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.WriteFile("src/First.cs", "first");
        workspace.WriteFile("src/Second.cs", "second");
        workspace.WriteFile("src/Third.cs", "third");
        var embeddingProvider = new FakeEmbeddingProvider(async (_, cancellationToken) =>
        {
            await Task.Delay(50, cancellationToken);
            return new[] { 1f, 0f };
        });
        var options = new RagNetOptions
        {
            Indexing = new IndexingOptions
            {
                MaxEmbeddingConcurrency = 2,
                MaxEmbeddingBatchSize = 1
            }
        };
        var indexer = CreateIndexer(
            workspace.RootPath,
            new FakeStateStore(State(workspace.RootPath)),
            new FakeVectorStore(),
            new FakeAnalyzer(),
            embeddingProvider: embeddingProvider,
            options: options);

        await indexer.IndexAsync(workspace.RootPath);

        Assert.Equal(3, embeddingProvider.EmbedCallCount);
        Assert.Equal(2, embeddingProvider.MaxObservedConcurrency);
    }

    [Fact]
    public async Task IndexAsync_ReusesEmbeddingForDuplicateChunkContent()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.WriteFile("src/First.cs", "same content");
        workspace.WriteFile("src/Second.cs", "same content");
        var embeddingProvider = new FakeEmbeddingProvider();
        var indexer = CreateIndexer(
            workspace.RootPath,
            new FakeStateStore(State(workspace.RootPath)),
            new FakeVectorStore(),
            new FakeAnalyzer(),
            embeddingProvider: embeddingProvider);

        var result = await indexer.IndexAsync(workspace.RootPath);

        Assert.Equal(2, result.ChunksIndexed);
        Assert.Equal(1, embeddingProvider.EmbedCallCount);
    }

    [Fact]
    public async Task IndexAsync_EmbeddingBatchesHonorConfiguredBatchSize()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.WriteFile("src/First.cs", "first");
        workspace.WriteFile("src/Second.cs", "second");
        workspace.WriteFile("src/Third.cs", "third");
        var embeddingProvider = new FakeEmbeddingProvider();
        var indexer = CreateIndexer(
            workspace.RootPath,
            new FakeStateStore(State(workspace.RootPath)),
            new FakeVectorStore(),
            new FakeAnalyzer(),
            embeddingProvider: embeddingProvider,
            options: new RagNetOptions
            {
                Indexing = new IndexingOptions
                {
                    MaxEmbeddingConcurrency = 1,
                    MaxEmbeddingBatchSize = 2
                }
            });

        await indexer.IndexAsync(workspace.RootPath);

        Assert.Equal([2, 1], embeddingProvider.BatchSizes);
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

        Assert.Contains("Scoped indexing requires a compatible existing index state", exception.Message);
    }

    [Fact]
    public async Task IndexAsync_DirectoryTargetScansOnlyDirectorySubtree()
    {
        using var workspace = new TemporaryWorkspace();
        var apiFile = workspace.WriteFile("Api/Program.cs", "api");
        var webFile = workspace.WriteFile("Web/Program.cs", "web");
        var stateStore = new FakeStateStore(State(workspace.RootPath));
        var analyzer = new FakeAnalyzer();
        var indexer = CreateIndexer(workspace.RootPath, stateStore, new FakeVectorStore(), analyzer);

        var result = await indexer.IndexAsync(Path.Combine(workspace.RootPath, "Api"));

        Assert.Equal(1, result.FilesScanned);
        Assert.Contains(Path.GetFullPath(apiFile), analyzer.AnalyzedFiles);
        Assert.DoesNotContain(Path.GetFullPath(webFile), analyzer.AnalyzedFiles);
    }

    [Fact]
    public async Task IndexAsync_SolutionTargetExcludesUnrelatedSiblingProject()
    {
        using var workspace = new TemporaryWorkspace();
        var solution = workspace.WriteFile(
            "Product.sln",
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Api", "Api\Api.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            """);
        var apiProject = workspace.WriteFile("Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var apiFile = workspace.WriteFile("Api/Program.cs", "api");
        var webProject = workspace.WriteFile("Web/Web.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var webFile = workspace.WriteFile("Web/Program.cs", "web");
        var analyzer = new FakeAnalyzer();
        var indexer = CreateIndexer(workspace.RootPath, new FakeStateStore(State(workspace.RootPath)), new FakeVectorStore(), analyzer);

        var result = await indexer.IndexAsync(solution);

        Assert.Equal(3, result.FilesScanned);
        Assert.Contains(Path.GetFullPath(solution), analyzer.AnalyzedFiles);
        Assert.Contains(Path.GetFullPath(apiProject), analyzer.AnalyzedFiles);
        Assert.Contains(Path.GetFullPath(apiFile), analyzer.AnalyzedFiles);
        Assert.DoesNotContain(Path.GetFullPath(webProject), analyzer.AnalyzedFiles);
        Assert.DoesNotContain(Path.GetFullPath(webFile), analyzer.AnalyzedFiles);
    }

    [Fact]
    public async Task IndexTargetsAsync_UnionsSolutionsDirectoriesAndFiles()
    {
        using var workspace = new TemporaryWorkspace();
        var apiSolution = workspace.WriteFile(
            "Api.sln",
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Api", "Api\Api.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            """);
        var adminSolution = workspace.WriteFile(
            "Admin.sln",
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Admin", "Admin\Admin.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);
        var apiFile = workspace.WriteFile("Api/Program.cs", "api");
        var adminFile = workspace.WriteFile("Admin/Program.cs", "admin");
        var docsFile = workspace.WriteFile("docs/guide.md", "docs");
        var explicitFile = workspace.WriteFile("Shared/Shared.cs", "shared");
        var unrelatedFile = workspace.WriteFile("Experimental/Program.cs", "experiment");
        workspace.WriteFile("Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        workspace.WriteFile("Admin/Admin.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var analyzer = new FakeAnalyzer();
        var registry = new FakeIndexedWorkspaceRegistry();
        var indexer = CreateIndexer(workspace.RootPath, new FakeStateStore(State(workspace.RootPath)), new FakeVectorStore(), analyzer, registry);

        var results = await indexer.IndexTargetsAsync(
        [
            apiSolution,
            adminSolution,
            Path.Combine(workspace.RootPath, "docs"),
            explicitFile
        ]);

        Assert.Single(results);
        Assert.Contains(Path.GetFullPath(apiFile), analyzer.AnalyzedFiles);
        Assert.Contains(Path.GetFullPath(adminFile), analyzer.AnalyzedFiles);
        Assert.Contains(Path.GetFullPath(docsFile), analyzer.AnalyzedFiles);
        Assert.Contains(Path.GetFullPath(explicitFile), analyzer.AnalyzedFiles);
        Assert.DoesNotContain(Path.GetFullPath(unrelatedFile), analyzer.AnalyzedFiles);
        var record = Assert.Single(registry.Records);
        Assert.Equal(Path.GetFullPath(workspace.RootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), record.WorkspaceRoot);
        Assert.Contains(Path.GetFullPath(apiSolution), record.IndexedTargets);
        Assert.Contains(Path.GetFullPath(adminSolution), record.IndexedTargets);
        Assert.Contains(Path.GetFullPath(Path.Combine(workspace.RootPath, "docs")), record.IndexedTargets);
        Assert.Contains(Path.GetFullPath(explicitFile), record.IndexedTargets);
        Assert.False(string.IsNullOrWhiteSpace(record.WorkspaceId));
        Assert.False(string.IsNullOrWhiteSpace(record.CollectionName));
    }

    [Fact]
    public async Task IndexAsync_ScopedRunPreservesUnrelatedPreviousState()
    {
        using var workspace = new TemporaryWorkspace();
        var solution = workspace.WriteFile(
            "Product.sln",
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Api", "Api\Api.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            """);
        workspace.WriteFile("Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var changedApiFile = workspace.WriteFile("Api/Program.cs", "api v2");
        var removedApiFile = Path.Combine(workspace.RootPath, "Api", "Removed.cs");
        var removedWebFile = Path.Combine(workspace.RootPath, "Web", "Removed.cs");
        var stateStore = new FakeStateStore(State(
            workspace.RootPath,
            FileState(changedApiFile, fingerprint: "old-api-fingerprint"),
            new IndexedFileState(Path.GetFullPath(removedApiFile), "removed-api", 10, DateTimeOffset.UtcNow),
            new IndexedFileState(Path.GetFullPath(removedWebFile), "removed-web", 10, DateTimeOffset.UtcNow)));
        var vectorStore = new FakeVectorStore();
        var analyzer = new FakeAnalyzer();
        var indexer = CreateIndexer(workspace.RootPath, stateStore, vectorStore, analyzer);

        await indexer.IndexAsync(solution);

        Assert.Contains(Path.GetFullPath(changedApiFile), vectorStore.DeletedFiles);
        Assert.Contains(Path.GetFullPath(removedApiFile), vectorStore.DeletedFiles);
        Assert.DoesNotContain(Path.GetFullPath(removedWebFile), vectorStore.DeletedFiles);
        Assert.Contains(Path.GetFullPath(removedWebFile), stateStore.SavedState!.Files.Keys);
        Assert.DoesNotContain(Path.GetFullPath(removedApiFile), stateStore.SavedState.Files.Keys);
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
        FakeAnalyzer analyzer,
        FakeIndexedWorkspaceRegistry? registry = null,
        FakeEmbeddingProvider? embeddingProvider = null,
        RagNetOptions? options = null)
        => new(
            new FakeWorkspaceDetector(workspaceRoot),
            new FakeWorkspaceScopeResolver(workspaceRoot),
            registry ?? new FakeIndexedWorkspaceRegistry(),
            [analyzer],
            stateStore,
            embeddingProvider ?? new FakeEmbeddingProvider(),
            vectorStore,
            new FakeSourceIdentityResolver(),
            Options.Create(options ?? new RagNetOptions()));

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

    private static IndexedWorkspaceRecord IndexedWorkspace(string workspaceRoot)
    {
        var normalizedRoot = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return new IndexedWorkspaceRecord(
            normalizedRoot,
            "workspace-id",
            "collection",
            [],
            [normalizedRoot],
            DateTimeOffset.UtcNow,
            FilesScanned: 1,
            ChunksIndexed: 1,
            FullReindex: false);
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
        public List<IndexedWorkspaceRecord> Records { get; } = [];

        public Task MarkIndexedAsync(IndexedWorkspaceRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetIndexedWorkspaceRootsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Records.Select(record => record.WorkspaceRoot).ToArray());

        public Task<IReadOnlyList<IndexedWorkspaceRecord>> GetIndexedWorkspacesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IndexedWorkspaceRecord>>(Records.ToArray());

        public Task DeleteWorkspaceAsync(string workspaceRoot, CancellationToken cancellationToken = default)
        {
            Records.RemoveAll(record => string.Equals(
                record.WorkspaceRoot,
                Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }
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

        public Task DeleteAsync(string workspaceRoot, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeEmbeddingProvider(
        Func<string, CancellationToken, Task<float[]>>? embedAsync = null) : IEmbeddingProvider
    {
        private int _activeEmbeds;
        private int _embedCallCount;
        private int _maxObservedConcurrency;

        public int EmbedCallCount => Volatile.Read(ref _embedCallCount);

        public int MaxObservedConcurrency => Volatile.Read(ref _maxObservedConcurrency);

        public List<int> BatchSizes { get; } = [];

        public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
            => (await EmbedBatchAsync([text], cancellationToken))[0];

        public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _embedCallCount);
            var active = Interlocked.Increment(ref _activeEmbeds);
            TrackMaxObservedConcurrency(active);
            try
            {
                lock (BatchSizes)
                {
                    BatchSizes.Add(texts.Count);
                }

                var embeddings = new List<float[]>(texts.Count);
                foreach (var text in texts)
                {
                    embeddings.Add(embedAsync is null
                        ? [1f, 0f]
                        : await embedAsync(text, cancellationToken));
                }

                return embeddings;
            }
            finally
            {
                Interlocked.Decrement(ref _activeEmbeds);
            }
        }

        private void TrackMaxObservedConcurrency(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxObservedConcurrency);
                if (active <= current ||
                    Interlocked.CompareExchange(ref _maxObservedConcurrency, active, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class FakeVectorStore : IVectorStore
    {
        public List<string> DeletedFiles { get; } = [];

        public List<CodeChunk> UpsertedChunks { get; } = [];

        public List<float[]> UpsertedEmbeddings { get; } = [];

        public bool WorkspaceDeleted { get; private set; }

        public IReadOnlyList<SearchResult> SearchResults { get; init; } = [];

        public int LastSearchLimit { get; private set; }

        public bool LastSearchHybrid { get; private set; }

        public string? LastSearchContentType { get; private set; }

        public string? LastSearchIndexProfile { get; private set; }

        public Task UpsertAsync(string workspaceRoot, IReadOnlyList<CodeChunk> chunks, IReadOnlyList<float[]> embeddings, CancellationToken cancellationToken = default)
        {
            UpsertedChunks.AddRange(chunks);
            UpsertedEmbeddings.AddRange(embeddings);
            return Task.CompletedTask;
        }

        public Task DeleteByFileAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default)
            => DeleteByFilesAsync(workspaceRoot, [filePath], cancellationToken);

        public Task DeleteByFilesAsync(string workspaceRoot, IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
        {
            DeletedFiles.AddRange(filePaths.Select(Path.GetFullPath));
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
            => Path.GetExtension(filePath) is ".cs" or ".md" or ".sln" or ".csproj" or ".props" or ".targets" or ".config";

        public Task<IReadOnlyList<CodeChunk>> AnalyzeAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default)
        {
            var fullPath = Path.GetFullPath(filePath);
            AnalyzedFiles.Add(fullPath);
            var contentType = string.Equals(Path.GetExtension(fullPath), ".md", StringComparison.OrdinalIgnoreCase)
                ? IndexedContentTypes.Documentation
                : Path.GetExtension(fullPath) is ".cs"
                    ? IndexedContentTypes.Code
                    : IndexedContentTypes.ProjectMetadata;
            return Task.FromResult<IReadOnlyList<CodeChunk>>(
            [
                new CodeChunk(
                    $"{Path.GetRelativePath(workspaceRoot, fullPath)}:1:1:chunk",
                    workspaceRoot,
                    fullPath,
                    contentType switch
                    {
                        IndexedContentTypes.Documentation => "markdown",
                        IndexedContentTypes.ProjectMetadata => "xml",
                        _ => "csharp"
                    },
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
