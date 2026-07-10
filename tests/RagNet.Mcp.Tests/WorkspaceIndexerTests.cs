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
    private const string CurrentSchemaVersion = "1";

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
            new IndexedFileState(Path.GetFullPath(removed), "removed-fingerprint", 10, DateTimeOffset.UtcNow, ChunkCount: 1)));
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
            new IndexedFileState(Path.GetFullPath(removed), "removed-fingerprint", 10, DateTimeOffset.UtcNow, ChunkCount: 1)));
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
        Assert.Equal(2, result.ChunksThatWouldBeDeleted);
        Assert.Equal(2, result.TotalChunksAfterIndex);
        Assert.False(result.FullReindex);
        Assert.True(result.StateCompatible);
        Assert.Equal(1, result.ChangedFiles);
        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(1, result.UnchangedFiles);
        Assert.Contains(result.FileChunkEstimates, file =>
            string.Equals(file.FilePath, Path.GetFullPath(changed), StringComparison.OrdinalIgnoreCase) &&
            file.CurrentChunks == 1 &&
            file.EstimatedChunksToEmbed == 1 &&
            file.EstimatedChunksToDelete == 1);
        Assert.Contains(result.FileChunkEstimates, file =>
            string.Equals(file.FilePath, Path.GetFullPath(removed), StringComparison.OrdinalIgnoreCase) &&
            file.CurrentChunks == 1 &&
            file.EstimatedChunksToEmbed == 0 &&
            file.EstimatedChunksToDelete == 1);
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
    public async Task IndexAsync_NoOpIncrementalRunPreservesRegistryTotalChunks()
    {
        using var workspace = new TemporaryWorkspace();
        var sourceFile = workspace.WriteFile("src/Program.cs", "program");
        var stateStore = new FakeStateStore(State(
            workspace.RootPath,
            FileState(sourceFile, chunkCount: 3)));
        var registry = new FakeIndexedWorkspaceRegistry();
        registry.Records.Add(IndexedWorkspace(workspace.RootPath, chunksIndexed: 3));
        var vectorStore = new FakeVectorStore();
        var analyzer = new FakeAnalyzer();
        var indexer = CreateIndexer(workspace.RootPath, stateStore, vectorStore, analyzer, registry);

        var result = await indexer.IndexAsync(workspace.RootPath);

        Assert.Equal(0, result.ChunksIndexed);
        Assert.Equal(3, result.TotalChunksIndexed);
        Assert.Empty(analyzer.AnalyzedFiles);
        Assert.Empty(vectorStore.UpsertedChunks);
        Assert.Equal(3, registry.Records[^1].ChunksIndexed);
        Assert.Equal(3, Assert.Single(stateStore.SavedState!.Files.Values).ChunkCount);
    }

    [Fact]
    public async Task IndexAsync_UsesSourceChangeDetectorWhenAvailable()
    {
        using var workspace = new TemporaryWorkspace();
        var unchanged = workspace.WriteFile("src/Unchanged.cs", "unchanged");
        var changed = workspace.WriteFile("src/Changed.cs", "changed v2");
        var removed = Path.Combine(workspace.RootPath, "src", "Removed.cs");
        var detector = new FakeSourceChangeDetector(changeSet: new SourceChangeSet(
            "git",
            IsAvailable: true,
            [Path.GetFullPath(changed)],
            [Path.GetFullPath(removed)])
        {
            IsComplete = true
        });
        var stateStore = new FakeStateStore(State(
            workspace.RootPath,
            FileState(unchanged),
            FileState(changed, fingerprint: "old-fingerprint"),
            new IndexedFileState(Path.GetFullPath(removed), "removed-fingerprint", 10, DateTimeOffset.UtcNow, ChunkCount: 1)));
        var vectorStore = new FakeVectorStore();
        var analyzer = new FakeAnalyzer();
        var indexer = CreateIndexer(
            workspace.RootPath,
            stateStore,
            vectorStore,
            analyzer,
            sourceChangeDetector: detector);

        var result = await indexer.IndexAsync(workspace.RootPath);

        Assert.Equal(1, result.FilesScanned);
        Assert.Equal([Path.GetFullPath(changed)], analyzer.AnalyzedFiles);
        Assert.Equal([Path.GetFullPath(removed), Path.GetFullPath(changed)], vectorStore.DeletedFiles);
        Assert.Contains(Path.GetFullPath(unchanged), stateStore.SavedState!.Files.Keys);
        Assert.DoesNotContain(Path.GetFullPath(removed), stateStore.SavedState.Files.Keys);
        Assert.True(detector.WasCalled);
    }

    [Fact]
    public async Task IndexAsync_IncompleteSourceChangeDetectorFallsBackToFingerprintComparison()
    {
        using var workspace = new TemporaryWorkspace();
        var changed = workspace.WriteFile("src/Changed.cs", "changed v2");
        var unchanged = workspace.WriteFile("src/Unchanged.cs", "unchanged");
        var detector = new FakeSourceChangeDetector(changeSet: new SourceChangeSet(
            "git",
            IsAvailable: true,
            ChangedFiles: [],
            DeletedFiles: []));
        var stateStore = new FakeStateStore(State(
            workspace.RootPath,
            FileState(changed, fingerprint: "old-fingerprint"),
            FileState(unchanged)));
        var vectorStore = new FakeVectorStore();
        var analyzer = new FakeAnalyzer();
        var indexer = CreateIndexer(
            workspace.RootPath,
            stateStore,
            vectorStore,
            analyzer,
            sourceChangeDetector: detector);

        var result = await indexer.IndexAsync(workspace.RootPath);

        Assert.Equal(2, result.FilesScanned);
        Assert.Equal([Path.GetFullPath(changed)], analyzer.AnalyzedFiles);
        Assert.Equal([Path.GetFullPath(changed)], vectorStore.DeletedFiles);
        Assert.True(detector.WasCalled);
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
        var analyzer = new FakeAnalyzer();
        var embeddingProvider = new FakeEmbeddingProvider(
            resolveException: new EmbeddingModelNotFoundException(
                "missing-model",
                "model missing",
                [new EmbeddingModelInfo("nomic-embed-text:latest")],
                "nomic-embed-text",
                fallbackAvailable: true));
        var indexer = CreateIndexer(
            workspace.RootPath,
            new FakeStateStore(State(workspace.RootPath)),
            vectorStore,
            analyzer,
            embeddingProvider: embeddingProvider);

        var exception = await Assert.ThrowsAsync<EmbeddingModelNotFoundException>(
            () => indexer.IndexTargetsAsync([workspace.RootPath]));

        Assert.Equal("missing-model", exception.Model);
        Assert.True(exception.FallbackAvailable);
        Assert.Equal(1, embeddingProvider.ResolveCallCount);
        Assert.Equal(0, embeddingProvider.EmbedCallCount);
        Assert.Empty(analyzer.AnalyzedFiles);
        Assert.Empty(vectorStore.UpsertedChunks);
    }

    [Fact]
    public async Task IndexTargetsAsync_ConfiguredFallbackUsesResolvedInstalledModel()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.WriteFile("src/First.cs", "first");
        var stateStore = new FakeStateStore(EmptyState(workspace.RootPath));
        var embeddingProvider = new FakeEmbeddingProvider(
            modelResolution: new EmbeddingModelResolution(
                "missing-model",
                "nomic-embed-text",
                UsedFallback: true,
                [new EmbeddingModelInfo("nomic-embed-text:latest")],
                "Ollama embedding model 'missing-model' is not installed; using configured fallback 'nomic-embed-text'."));
        var options = new RagNetOptions
        {
            Ollama = new OllamaOptions
            {
                EmbeddingModel = "missing-model",
                AllowInstalledEmbeddingModelFallback = true
            }
        };
        var indexer = CreateIndexer(
            workspace.RootPath,
            stateStore,
            new FakeVectorStore(),
            new FakeAnalyzer(),
            embeddingProvider: embeddingProvider,
            options: options);

        var result = await indexer.IndexAsync(workspace.RootPath);

        Assert.Equal(1, result.ChunksIndexed);
        Assert.Equal("nomic-embed-text", stateStore.SavedState!.EmbeddingModel);
        Assert.Contains(result.Warnings, warning => warning.Contains("using configured fallback 'nomic-embed-text'", StringComparison.Ordinal));
        Assert.Equal(1, embeddingProvider.ResolveCallCount);
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
    public async Task IndexAsync_IncompatibleAllProfileStateRequiresExplicitSafePath()
    {
        using var workspace = new TemporaryWorkspace();
        var codeFile = workspace.WriteFile("src/Program.cs", "code");
        var stateStore = new FakeStateStore(State(
            workspace.RootPath,
            schemaVersion: "old-schema",
            FileState(codeFile)));
        var vectorStore = new FakeVectorStore();
        var analyzer = new FakeAnalyzer();
        var indexer = CreateIndexer(workspace.RootPath, stateStore, vectorStore, analyzer);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => indexer.IndexAsync(workspace.RootPath));

        Assert.Contains("will not delete or overwrite the active index automatically", exception.Message);
        Assert.Contains("--force", exception.Message);
        Assert.Contains("migrate", exception.Message);
        Assert.False(vectorStore.WorkspaceDeleted);
        Assert.Empty(vectorStore.UpsertedChunks);
        Assert.Null(stateStore.SavedState);
    }

    [Fact]
    public async Task IndexAsync_ForceFullReindexStagesReplacementAndPreservesActiveCollectionUntilPromotion()
    {
        using var workspace = new TemporaryWorkspace();
        var sourceFile = workspace.WriteFile("src/Program.cs", "code");
        var activeCollection = "active-collection";
        var registry = new FakeIndexedWorkspaceRegistry();
        registry.Records.Add(IndexedWorkspace(workspace.RootPath) with { CollectionName = activeCollection });
        var vectorStore = new FakeVectorStore();
        var analyzer = new FakeAnalyzer();
        var indexer = CreateIndexer(
            workspace.RootPath,
            new FakeStateStore(State(workspace.RootPath, FileState(sourceFile, fingerprint: "old-fingerprint"))),
            vectorStore,
            analyzer,
            registry);

        var result = await indexer.IndexAsync(workspace.RootPath, force: true);

        Assert.True(result.FullReindex);
        Assert.False(vectorStore.WorkspaceDeleted);
        Assert.Empty(vectorStore.DeletedFiles);
        Assert.Single(vectorStore.UpsertedChunks);
        Assert.NotEqual(activeCollection, vectorStore.LastUpsertCollectionName);
        Assert.Contains("-stage-", vectorStore.LastUpsertCollectionName, StringComparison.Ordinal);
        Assert.Equal(vectorStore.LastUpsertCollectionName, registry.Records[^1].CollectionName);
    }

    [Fact]
    public async Task IndexAsync_FailedFullReindexDoesNotPromoteStagingCollection()
    {
        using var workspace = new TemporaryWorkspace();
        var sourceFile = workspace.WriteFile("src/Program.cs", "code");
        var activeCollection = "active-collection";
        var registry = new FakeIndexedWorkspaceRegistry();
        registry.Records.Add(IndexedWorkspace(workspace.RootPath) with { CollectionName = activeCollection });
        var vectorStore = new FakeVectorStore { ThrowOnUpsert = true };
        var indexer = CreateIndexer(
            workspace.RootPath,
            new FakeStateStore(State(workspace.RootPath, FileState(sourceFile, fingerprint: "old-fingerprint"))),
            vectorStore,
            new FakeAnalyzer(),
            registry);

        await Assert.ThrowsAsync<InvalidOperationException>(() => indexer.IndexAsync(workspace.RootPath, force: true));

        Assert.False(vectorStore.WorkspaceDeleted);
        Assert.Empty(vectorStore.DeletedFiles);
        Assert.Equal(activeCollection, registry.Records[^1].CollectionName);
    }

    [Fact]
    public async Task IndexAsync_ProfileScopedRunRequiresCompatibleExistingState()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.WriteFile("docs/guide.md", "docs");
        var stateStore = new FakeStateStore(State(workspace.RootPath, schemaVersion: "old-schema"));
        var indexer = CreateIndexer(workspace.RootPath, stateStore, new FakeVectorStore(), new FakeAnalyzer());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            indexer.IndexAsync(workspace.RootPath, indexProfile: IndexProfiles.Documentation));

        Assert.Contains("will not delete or overwrite the active index automatically", exception.Message);
    }

    [Fact]
    public async Task IndexAsync_TargetScopedFirstRunAllowsMissingState()
    {
        using var workspace = new TemporaryWorkspace();
        var apiFile = workspace.WriteFile("Api/Program.cs", "api");
        var webFile = workspace.WriteFile("Web/Program.cs", "web");
        var stateStore = new FakeStateStore(EmptyState(workspace.RootPath));
        var analyzer = new FakeAnalyzer();
        var vectorStore = new FakeVectorStore();
        var indexer = CreateIndexer(workspace.RootPath, stateStore, vectorStore, analyzer);

        var result = await indexer.IndexAsync(Path.Combine(workspace.RootPath, "Api"));

        Assert.False(result.FullReindex);
        Assert.False(vectorStore.WorkspaceDeleted);
        Assert.Equal(1, result.FilesScanned);
        Assert.Contains(Path.GetFullPath(apiFile), analyzer.AnalyzedFiles);
        Assert.DoesNotContain(Path.GetFullPath(webFile), analyzer.AnalyzedFiles);
        Assert.Contains(Path.GetFullPath(apiFile), stateStore.SavedState!.Files.Keys);
        Assert.DoesNotContain(Path.GetFullPath(webFile), stateStore.SavedState.Files.Keys);
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
    public async Task IndexAsync_UsesDefaultHeavyDirectoryExcludes()
    {
        using var workspace = new TemporaryWorkspace();
        var sourceFile = workspace.WriteFile("src/Program.cs", "source");
        var excludedFiles = new[]
        {
            workspace.WriteFile(".agents/notes.md", "agents"),
            workspace.WriteFile(".codex/session.md", "codex"),
            workspace.WriteFile(".dotnet-home/state.md", "dotnet"),
            workspace.WriteFile(".git/config.md", "git"),
            workspace.WriteFile("artifacts/log.md", "artifacts"),
            workspace.WriteFile("bin/Debug/Generated.cs", "bin"),
            workspace.WriteFile("coverage/report.md", "coverage"),
            workspace.WriteFile("dist/bundle.md", "dist"),
            workspace.WriteFile("obj/Debug/Generated.cs", "obj"),
            workspace.WriteFile("node_modules/pkg/index.md", "node"),
            workspace.WriteFile("TestResults/result.md", "results")
        };
        var analyzer = new FakeAnalyzer();
        var indexer = CreateIndexer(workspace.RootPath, new FakeStateStore(State(workspace.RootPath)), new FakeVectorStore(), analyzer);

        var result = await indexer.IndexAsync(workspace.RootPath);

        Assert.Equal(1, result.FilesScanned);
        Assert.Equal([Path.GetFullPath(sourceFile)], analyzer.AnalyzedFiles);
        foreach (var excludedFile in excludedFiles)
        {
            Assert.DoesNotContain(Path.GetFullPath(excludedFile), analyzer.AnalyzedFiles);
        }
    }

    [Fact]
    public async Task IndexAsync_PreservesManualExcludeDirectories()
    {
        using var workspace = new TemporaryWorkspace();
        var sourceFile = workspace.WriteFile("src/Program.cs", "source");
        var vendorFile = workspace.WriteFile("vendor/Generated.cs", "vendor");
        var analyzer = new FakeAnalyzer();
        var indexer = CreateIndexer(workspace.RootPath, new FakeStateStore(State(workspace.RootPath)), new FakeVectorStore(), analyzer);

        var result = await indexer.IndexAsync(workspace.RootPath, excludeDirectories: ["vendor"]);

        Assert.Equal(1, result.FilesScanned);
        Assert.Contains(Path.GetFullPath(sourceFile), analyzer.AnalyzedFiles);
        Assert.DoesNotContain(Path.GetFullPath(vendorFile), analyzer.AnalyzedFiles);
    }

    [Fact]
    public async Task IndexAsync_ConfiguredExcludeDirectoriesReplaceDefaults()
    {
        using var workspace = new TemporaryWorkspace();
        var sourceFile = workspace.WriteFile("src/Program.cs", "source");
        var binFile = workspace.WriteFile("bin/Debug/Generated.cs", "bin");
        var generatedFile = workspace.WriteFile("generated/Generated.cs", "generated");
        var analyzer = new FakeAnalyzer();
        var options = new RagNetOptions
        {
            Workspace = new WorkspaceOptions
            {
                ExcludeDirectories = ["generated"]
            }
        };
        var indexer = CreateIndexer(workspace.RootPath, new FakeStateStore(State(workspace.RootPath)), new FakeVectorStore(), analyzer, options: options);

        var result = await indexer.IndexAsync(workspace.RootPath);

        Assert.Equal(2, result.FilesScanned);
        Assert.Contains(Path.GetFullPath(sourceFile), analyzer.AnalyzedFiles);
        Assert.Contains(Path.GetFullPath(binFile), analyzer.AnalyzedFiles);
        Assert.DoesNotContain(Path.GetFullPath(generatedFile), analyzer.AnalyzedFiles);
    }

    [Fact]
    public async Task IndexAsync_ReportsSequentialEmbeddingProgress()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.WriteFile("src/First.cs", "first");
        workspace.WriteFile("src/Second.cs", "second");
        workspace.WriteFile("src/Third.cs", "third");
        var progress = new RecordingProgress();
        var indexer = CreateIndexer(
            workspace.RootPath,
            new FakeStateStore(State(workspace.RootPath)),
            new FakeVectorStore(),
            new FakeAnalyzer(),
            options: new RagNetOptions
            {
                Indexing = new IndexingOptions
                {
                    MaxEmbeddingConcurrency = 2,
                    MaxEmbeddingBatchSize = 1
                }
            });

        await indexer.IndexAsync(workspace.RootPath, progress: progress);

        var embeddingProgress = progress.Reports
            .Where(report => report.Stage == IndexingProgressStage.CreatingEmbeddings)
            .Select(report => (report.Current, report.Total))
            .ToArray();

        Assert.Equal((0, 3), embeddingProgress[0]);
        Assert.Equal([1, 2, 3], embeddingProgress.Skip(1).Select(report => report.Current).Order().ToArray());
        Assert.All(embeddingProgress, report => Assert.Equal(3, report.Total));
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
        var registry = new FakeIndexedWorkspaceRegistry();
        registry.Records.Add(IndexedWorkspace(workspace.RootPath) with { CollectionName = "active-search-collection" });
        var indexer = CreateIndexer(workspace.RootPath, new FakeStateStore(State(workspace.RootPath)), vectorStore, new FakeAnalyzer(), registry);

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
        Assert.Equal("active-search-collection", vectorStore.LastSearchCollectionName);
        Assert.Equal(IndexedContentTypes.Code, vectorStore.LastSearchContentType);
        Assert.Equal(IndexProfiles.Tests, vectorStore.LastSearchIndexProfile);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, result => result.SymbolName == "SearchTests" && result.StartLine == 1 && result.EndLine == 8);
    }

    [Fact]
    public async Task GetCodeContextAsync_UsesIndexedChunksWhenFileIsNotReadableLocally()
    {
        using var workspace = new TemporaryWorkspace();
        var hostFile = @"C:\Product\Api\Program.cs";
        var vectorStore = new FakeVectorStore();
        vectorStore.IndexedChunks.Add(new CodeChunk(
            "program:1:4",
            workspace.RootPath,
            hostFile,
            "csharp",
            "Program",
            "File",
            1,
            4,
            "var builder = WebApplication.CreateBuilder(args);\nvar app = builder.Build();\napp.MapGet(\"/\", () => \"ok\");\napp.Run();"));
        var registry = new FakeIndexedWorkspaceRegistry();
        registry.Records.Add(IndexedWorkspace(workspace.RootPath) with { CollectionName = "active-code-context" });
        var indexer = CreateIndexer(workspace.RootPath, new FakeStateStore(State(workspace.RootPath)), vectorStore, new FakeAnalyzer(), registry);

        var context = await indexer.GetCodeContextAsync(hostFile, line: 3, before: 1, after: 1);

        Assert.Contains("    2: var app = builder.Build();", context);
        Assert.Contains("    3: app.MapGet", context);
        Assert.Contains("    4: app.Run();", context);
        Assert.Equal("active-code-context", vectorStore.LastChunkLookupCollectionName);
        Assert.Equal(hostFile, vectorStore.LastChunkLookupFilePath);
    }

    [Fact]
    public async Task GetSymbolDetailsAsync_UsesIndexedChunksWhenFileIsNotReadableLocally()
    {
        using var workspace = new TemporaryWorkspace();
        var hostFile = @"C:\Product\Api\Controllers\OrdersController.cs";
        var vectorStore = new FakeVectorStore();
        vectorStore.IndexedChunks.Add(new CodeChunk(
            "orders:10:20",
            workspace.RootPath,
            hostFile,
            "csharp",
            "GetOrders",
            "Method",
            10,
            20,
            "public IActionResult GetOrders() => Ok();"));
        var registry = new FakeIndexedWorkspaceRegistry();
        registry.Records.Add(IndexedWorkspace(workspace.RootPath) with { CollectionName = "active-symbol-details" });
        var indexer = CreateIndexer(workspace.RootPath, new FakeStateStore(State(workspace.RootPath)), vectorStore, new FakeAnalyzer(), registry);

        var details = await indexer.GetSymbolDetailsAsync(hostFile, "GetOrders");

        Assert.Equal("public IActionResult GetOrders() => Ok();", details);
        Assert.Equal("active-symbol-details", vectorStore.LastChunkLookupCollectionName);
        Assert.Equal(hostFile, vectorStore.LastChunkLookupFilePath);
    }

    private static WorkspaceIndexer CreateIndexer(
        string workspaceRoot,
        FakeStateStore stateStore,
        FakeVectorStore vectorStore,
        FakeAnalyzer analyzer,
        FakeIndexedWorkspaceRegistry? registry = null,
        FakeEmbeddingProvider? embeddingProvider = null,
        RagNetOptions? options = null,
        FakeSourceChangeDetector? sourceChangeDetector = null)
    {
        var provider = embeddingProvider ?? new FakeEmbeddingProvider();
        return new(
            new FakeWorkspaceDetector(workspaceRoot),
            new FakeWorkspaceScopeResolver(workspaceRoot),
            registry ?? new FakeIndexedWorkspaceRegistry(),
            new FakeWorkspaceGroupRegistry(),
            [analyzer],
            stateStore,
            provider,
            provider,
            vectorStore,
            new FakeSourceIdentityResolver(),
            sourceChangeDetector ?? new FakeSourceChangeDetector(),
            Options.Create(options ?? new RagNetOptions()));
    }

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

    private static WorkspaceIndexState EmptyState(string workspaceRoot)
        => new(
            Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            new Dictionary<string, IndexedFileState>(StringComparer.OrdinalIgnoreCase),
            EmbeddingModel: null,
            SchemaVersion: null,
            SavedAtUtc: null,
            StateExists: false);

    private static IndexedFileState FileState(string filePath, string? fingerprint = null, int chunkCount = 1)
    {
        var fullPath = Path.GetFullPath(filePath);
        var info = new FileInfo(fullPath);
        return new IndexedFileState(
            fullPath,
            fingerprint ?? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fullPath))),
            info.Length,
            info.LastWriteTimeUtc,
            chunkCount);
    }

    private static IndexedWorkspaceRecord IndexedWorkspace(string workspaceRoot, int chunksIndexed = 1)
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
            ChunksIndexed: chunksIndexed,
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
            bool includeGroupedWorkspaces = false,
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

    private sealed class FakeWorkspaceGroupRegistry : IWorkspaceGroupRegistry
    {
        public Task<IReadOnlyList<WorkspaceGroupRecord>> GetGroupsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceGroupRecord>>([]);

        public Task<WorkspaceGroupRecord?> GetGroupAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkspaceGroupRecord?>(null);

        public Task<WorkspaceGroupRecord> SaveGroupAsync(
            string name,
            IReadOnlyList<string> roots,
            IReadOnlyList<string>? excludeDirectories = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceGroupRecord(name, WorkspaceGroupSources.Local, roots, excludeDirectories ?? [], IsReadOnly: false));

        public Task DeleteGroupAsync(string name, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
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
        Func<string, CancellationToken, Task<float[]>>? embedAsync = null,
        EmbeddingModelResolution? modelResolution = null,
        Exception? resolveException = null) : IEmbeddingProvider, IEmbeddingModelCatalog
    {
        private int _activeEmbeds;
        private int _embedCallCount;
        private int _maxObservedConcurrency;
        private int _resolveCallCount;

        public int EmbedCallCount => Volatile.Read(ref _embedCallCount);

        public int ResolveCallCount => Volatile.Read(ref _resolveCallCount);

        public int MaxObservedConcurrency => Volatile.Read(ref _maxObservedConcurrency);

        public List<int> BatchSizes { get; } = [];

        public Task<IReadOnlyList<EmbeddingModelInfo>> ListInstalledModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<EmbeddingModelInfo>>(
                modelResolution?.InstalledModels ?? [new EmbeddingModelInfo("mxbai-embed-large:latest")]);

        public Task<EmbeddingModelResolution> ResolveEmbeddingModelAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _resolveCallCount);
            if (resolveException is not null)
            {
                throw resolveException;
            }

            return Task.FromResult(modelResolution ?? new EmbeddingModelResolution(
                "mxbai-embed-large",
                "mxbai-embed-large",
                UsedFallback: false,
                [new EmbeddingModelInfo("mxbai-embed-large:latest")],
                Message: null));
        }

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

        public string? LastUpsertCollectionName { get; private set; }

        public bool WorkspaceDeleted { get; private set; }

        public bool ThrowOnUpsert { get; init; }

        public IReadOnlyList<SearchResult> SearchResults { get; init; } = [];

        public List<CodeChunk> IndexedChunks { get; } = [];

        public int LastSearchLimit { get; private set; }

        public bool LastSearchHybrid { get; private set; }

        public string? LastSearchCollectionName { get; private set; }

        public string? LastSearchContentType { get; private set; }

        public string? LastSearchIndexProfile { get; private set; }

        public string? LastChunkLookupCollectionName { get; private set; }

        public string? LastChunkLookupFilePath { get; private set; }

        public Task UpsertAsync(string workspaceRoot, IReadOnlyList<CodeChunk> chunks, IReadOnlyList<float[]> embeddings, CancellationToken cancellationToken = default)
            => UpsertAsync(workspaceRoot, workspaceRoot, chunks, embeddings, cancellationToken);

        public Task UpsertAsync(string workspaceRoot, string collectionName, IReadOnlyList<CodeChunk> chunks, IReadOnlyList<float[]> embeddings, CancellationToken cancellationToken = default)
        {
            if (ThrowOnUpsert)
            {
                throw new InvalidOperationException("upsert failed");
            }

            LastUpsertCollectionName = collectionName;
            UpsertedChunks.AddRange(chunks);
            UpsertedEmbeddings.AddRange(embeddings);
            return Task.CompletedTask;
        }

        public Task DeleteByFileAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default)
            => DeleteByFilesAsync(workspaceRoot, [filePath], cancellationToken);

        public Task DeleteByFilesAsync(string workspaceRoot, IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
            => DeleteByFilesAsync(workspaceRoot, workspaceRoot, filePaths, cancellationToken);

        public Task DeleteByFilesAsync(string workspaceRoot, string collectionName, IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
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
            => SearchAsync(workspaceRoot, workspaceRoot, embedding, query, limit, hybrid, contentType, indexProfile, cancellationToken);

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            string workspaceRoot,
            string collectionName,
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
            LastSearchCollectionName = collectionName;
            LastSearchContentType = contentType;
            LastSearchIndexProfile = indexProfile;
            return Task.FromResult(SearchResults);
        }

        public Task<IReadOnlyList<CodeChunk>> GetChunksByFileAsync(
            string workspaceRoot,
            string collectionName,
            string filePath,
            CancellationToken cancellationToken = default)
        {
            LastChunkLookupCollectionName = collectionName;
            LastChunkLookupFilePath = filePath;
            var normalizedFilePath = NormalizePath(filePath);
            return Task.FromResult<IReadOnlyList<CodeChunk>>(IndexedChunks
                .Where(chunk => string.Equals(NormalizePath(chunk.FilePath), normalizedFilePath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(chunk => chunk.StartLine)
                .ToArray());
        }

        private static string NormalizePath(string path)
        {
            var trimmed = path.Trim();
            if (trimmed.Length >= 3 &&
                char.IsAsciiLetter(trimmed[0]) &&
                trimmed[1] == ':' &&
                (trimmed[2] == '\\' || trimmed[2] == '/'))
            {
                return trimmed.Replace('/', '\\').TrimEnd('\\');
            }

            return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

    }

    private sealed class RecordingProgress : IProgress<IndexingProgress>
    {
        private readonly object _gate = new();

        public List<IndexingProgress> Reports { get; } = [];

        public void Report(IndexingProgress value)
        {
            lock (_gate)
            {
                Reports.Add(value);
            }
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

    private sealed class FakeSourceChangeDetector(SourceChangeSet? changeSet = null) : ISourceChangeDetector
    {
        public bool WasCalled { get; private set; }

        public Task<SourceChangeSet> DetectChangesAsync(
            string workspaceRoot,
            IReadOnlyList<string> candidateFiles,
            IReadOnlyList<string> previouslyIndexedFiles,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(changeSet ?? SourceChangeSet.Unavailable("test"));
        }
    }
}
