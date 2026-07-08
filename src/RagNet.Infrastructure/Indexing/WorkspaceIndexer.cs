using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RagNet.Mcp.Analyzers;
using RagNet.Mcp.Analyzers.Interfaces;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Embeddings;
using RagNet.Mcp.Embeddings.Interfaces;
using RagNet.Mcp.Indexing.Interfaces;
using RagNet.Mcp.Source.Interfaces;
using RagNet.Mcp.Storage;
using RagNet.Mcp.Storage.Interfaces;
using RagNet.Mcp.Workspace;
using RagNet.Mcp.Workspace.Interfaces;

namespace RagNet.Mcp.Indexing;

public sealed class WorkspaceIndexer(
    IWorkspaceDetector workspaceDetector,
    IWorkspaceScopeResolver workspaceScopeResolver,
    IIndexedWorkspaceRegistry indexedWorkspaceRegistry,
    IWorkspaceGroupRegistry workspaceGroupRegistry,
    IEnumerable<ICodeAnalyzer> analyzers,
    IWorkspaceIndexStateStore indexStateStore,
    IEmbeddingProvider embeddingProvider,
    IEmbeddingModelCatalog embeddingModelCatalog,
    IVectorStore vectorStore,
    ISourceIdentityResolver sourceIdentityResolver,
    IOptions<RagNetOptions> options) : IWorkspaceIndexer
{
    private const string IndexSchemaVersion = "ragnet-index-v2/analyzers-v8";
    private const int SearchCandidateMultiplier = 5;
    private const int MaxEmbeddingConcurrencyLimit = 16;
    private const int MaxEmbeddingBatchSizeLimit = 128;
    private static readonly Regex SolutionProjectRegex = new(
        "^Project\\(\"\\{[^}]+\\}\"\\)\\s*=\\s*\"[^\"]+\",\\s*\"([^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SlnxProjectRegex = new(
        "(?:Path|path|File|file)\\s*=\\s*\"([^\"]+\\.csproj)\"|\"([^\"]+\\.csproj)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> SharedDotNetMetadataFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "global.json",
        "NuGet.config"
    };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> IndexLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyList<ICodeAnalyzer> _analyzers = analyzers.ToArray();
    private readonly RagNetOptions _options = options.Value;

    public async Task<IndexWorkspaceResult> IndexAsync(
        string workspacePath,
        IReadOnlyList<string>? excludeDirectories = null,
        bool force = false,
        string? indexProfile = null,
        CancellationToken cancellationToken = default,
        IProgress<IndexingProgress>? progress = null)
        => (await IndexTargetsAsync([workspacePath], excludeDirectories, force, indexProfile, cancellationToken, progress))[0];

    public async Task<IReadOnlyList<IndexWorkspaceResult>> IndexTargetsAsync(
        IReadOnlyList<string> workspacePaths,
        IReadOnlyList<string>? excludeDirectories = null,
        bool force = false,
        string? indexProfile = null,
        CancellationToken cancellationToken = default,
        IProgress<IndexingProgress>? progress = null)
    {
        if (workspacePaths.Count == 0)
        {
            throw new ArgumentException("At least one workspace target is required.", nameof(workspacePaths));
        }

        var normalizedIndexProfile = IndexProfiles.Normalize(indexProfile);
        var plans = new Dictionary<string, WorkspaceIndexTargetPlan>(StringComparer.OrdinalIgnoreCase);
        foreach (var workspacePath in workspacePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetPlan = await CreateTargetPlanAsync(workspacePath, excludeDirectories, cancellationToken);
            if (plans.TryGetValue(targetPlan.WorkspaceRoot, out var existing))
            {
                plans[targetPlan.WorkspaceRoot] = existing.Merge(targetPlan);
            }
            else
            {
                plans[targetPlan.WorkspaceRoot] = targetPlan;
            }
        }

        var results = new List<IndexWorkspaceResult>();
        foreach (var plan in plans.Values)
        {
            Report(progress, plan.WorkspaceRoot, IndexingProgressStage.Starting, 0, null, "Preparing workspace index.");
            var indexLock = IndexLocks.GetOrAdd(plan.WorkspaceRoot, _ => new SemaphoreSlim(1, 1));
            await indexLock.WaitAsync(cancellationToken);

            try
            {
                results.Add(await IndexLockedAsync(plan, force, normalizedIndexProfile, cancellationToken, progress));
            }
            finally
            {
                indexLock.Release();
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<DryRunIndexWorkspaceResult>> DryRunIndexTargetsAsync(
        IReadOnlyList<string> workspacePaths,
        IReadOnlyList<string>? excludeDirectories = null,
        bool force = false,
        string? indexProfile = null,
        CancellationToken cancellationToken = default,
        IProgress<IndexingProgress>? progress = null)
    {
        if (workspacePaths.Count == 0)
        {
            throw new ArgumentException("At least one workspace target is required.", nameof(workspacePaths));
        }

        var normalizedIndexProfile = IndexProfiles.Normalize(indexProfile);
        var plans = new Dictionary<string, WorkspaceIndexTargetPlan>(StringComparer.OrdinalIgnoreCase);
        foreach (var workspacePath in workspacePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetPlan = await CreateTargetPlanAsync(workspacePath, excludeDirectories, cancellationToken);
            if (plans.TryGetValue(targetPlan.WorkspaceRoot, out var existing))
            {
                plans[targetPlan.WorkspaceRoot] = existing.Merge(targetPlan);
            }
            else
            {
                plans[targetPlan.WorkspaceRoot] = targetPlan;
            }
        }

        var results = new List<DryRunIndexWorkspaceResult>();
        foreach (var plan in plans.Values)
        {
            Report(progress, plan.WorkspaceRoot, IndexingProgressStage.Starting, 0, null, "Preparing workspace index dry run.");
            var indexLock = IndexLocks.GetOrAdd(plan.WorkspaceRoot, _ => new SemaphoreSlim(1, 1));
            await indexLock.WaitAsync(cancellationToken);

            try
            {
                results.Add(await DryRunIndexLockedAsync(plan, force, normalizedIndexProfile, cancellationToken, progress));
            }
            finally
            {
                indexLock.Release();
            }
        }

        return results;
    }

    private async Task<IndexWorkspaceResult> IndexLockedAsync(
        WorkspaceIndexTargetPlan targetPlan,
        bool force,
        string indexProfile,
        CancellationToken cancellationToken,
        IProgress<IndexingProgress>? progress)
    {
        var modelResolution = await embeddingModelCatalog.ResolveEmbeddingModelAsync(cancellationToken);
        var analysis = await AnalyzeTargetPlanAsync(
            targetPlan,
            force,
            indexProfile,
            modelResolution.SelectedModel,
            allowIncompatibleScopedState: false,
            cancellationToken,
            progress);
        if (!string.IsNullOrWhiteSpace(modelResolution.Message))
        {
            analysis.Warnings.Add(modelResolution.Message);
        }

        var workspaceRoot = targetPlan.WorkspaceRoot;

        if (analysis.FullReindex && !analysis.MergeScopedState)
        {
            await vectorStore.DeleteWorkspaceAsync(workspaceRoot, cancellationToken);
        }

        var filesToDelete = analysis.DeletedFiles.Concat(analysis.ChangedFiles).ToArray();
        Report(progress, workspaceRoot, IndexingProgressStage.DeletingVectors, 0, filesToDelete.Length, "Deleting stale vectors.");
        if (filesToDelete.Length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await vectorStore.DeleteByFilesAsync(workspaceRoot, filesToDelete, cancellationToken);
        }

        Report(progress, workspaceRoot, IndexingProgressStage.DeletingVectors, filesToDelete.Length, filesToDelete.Length, "Deleting stale vectors.");

        var embeddedBatch = await CreateEmbeddingsAsync(workspaceRoot, analysis.Chunks, analysis.Warnings, cancellationToken, progress);
        var embeddedChunks = embeddedBatch.Chunks;
        var embeddings = embeddedBatch.Embeddings;

        if (embeddedChunks.Count > 0)
        {
            Report(progress, workspaceRoot, IndexingProgressStage.UpsertingVectors, 0, embeddedChunks.Count, "Writing vectors to store.");
            await vectorStore.UpsertAsync(workspaceRoot, embeddedChunks, embeddings, cancellationToken);
            Report(progress, workspaceRoot, IndexingProgressStage.UpsertingVectors, embeddedChunks.Count, embeddedChunks.Count, "Writing vectors to store.");
        }

        Report(progress, workspaceRoot, IndexingProgressStage.SavingState, 0, null, "Saving index state.");
        var embeddedChunkCountsByFile = CountChunksByFile(embeddedChunks);
        var currentFilesWithChunkCounts = ApplyChunkCounts(
            analysis.CurrentFiles,
            embeddedChunkCountsByFile,
            analysis.PreviousState.Files);
        var filesToSave = analysis.MergeScopedState
            ? analysis.PreviousState.Files
                .Where(file => !FileMatchesProfile(file.Key, indexProfile) || !analysis.TargetPlan.CanDeleteMissingFile(file.Key))
                .Select(file => file.Value)
                .Concat(currentFilesWithChunkCounts.Values)
                .ToDictionary(file => file.FilePath, StringComparer.OrdinalIgnoreCase)
            : currentFilesWithChunkCounts;
        var totalChunksIndexed = SumChunkCounts(filesToSave);
        if (totalChunksIndexed == 0 && embeddedChunks.Count == 0)
        {
            totalChunksIndexed = await GetPreviousRegistryChunkCountAsync(workspaceRoot, cancellationToken);
        }

        var indexedAtUtc = DateTimeOffset.UtcNow;
        await indexStateStore.SaveAsync(new WorkspaceIndexState(
            workspaceRoot,
            filesToSave,
            modelResolution.SelectedModel,
            IndexSchemaVersion,
            indexedAtUtc,
            StateExists: true), cancellationToken);

        await indexedWorkspaceRegistry.MarkIndexedAsync(new IndexedWorkspaceRecord(
            workspaceRoot,
            QdrantCollectionNaming.GetWorkspaceId(workspaceRoot),
            QdrantCollectionNaming.GetCollectionName(_options.Qdrant.CollectionPrefix, workspaceRoot),
            await GetWorkspaceGroupsForRootAsync(workspaceRoot, cancellationToken),
            targetPlan.IndexedTargets,
            indexedAtUtc,
            analysis.FilesScanned,
            totalChunksIndexed,
            analysis.FullReindex), cancellationToken);

        Report(progress, workspaceRoot, IndexingProgressStage.Completed, embeddedChunks.Count, analysis.Chunks.Count, "Indexing completed.");
        return new IndexWorkspaceResult(
            workspaceRoot,
            analysis.FilesScanned,
            embeddedChunks.Count,
            analysis.FullReindex,
            analysis.Warnings,
            totalChunksIndexed);
    }

    private async Task<EmbeddedChunkBatch> CreateEmbeddingsAsync(
        string workspaceRoot,
        IReadOnlyList<CodeChunk> chunks,
        List<string> warnings,
        CancellationToken cancellationToken,
        IProgress<IndexingProgress>? progress)
    {
        var results = new EmbeddedChunk?[chunks.Count];
        var workItems = CreateEmbeddingWorkItems(chunks);
        var completed = 0;
        var warningLock = new object();
        Report(progress, workspaceRoot, IndexingProgressStage.CreatingEmbeddings, 0, chunks.Count, "Creating embeddings.");

        await Parallel.ForEachAsync(
            workItems.Chunk(GetMaxEmbeddingBatchSize()),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = GetMaxEmbeddingConcurrency()
            },
            async (batch, token) =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var embeddings = await embeddingProvider.EmbedBatchAsync(batch.Select(item => item.Content).ToArray(), token);
                    for (var index = 0; index < batch.Length; index++)
                    {
                        AssignEmbeddingResult(batch[index], embeddings[index], chunks, results);
                        var current = Interlocked.Add(ref completed, batch[index].ChunkIndexes.Count);
                        Report(progress, workspaceRoot, IndexingProgressStage.CreatingEmbeddings, current, chunks.Count, "Creating embeddings.");
                    }
                }
                catch (EmbeddingModelNotFoundException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                    await EmbedBatchIndividuallyAsync(batch, chunks, results, warnings, warningLock, () =>
                    {
                        var current = Interlocked.Add(ref completed, 1);
                        Report(progress, workspaceRoot, IndexingProgressStage.CreatingEmbeddings, current, chunks.Count, "Creating embeddings.");
                    }, token);
                }
            });

        var embeddedChunks = new List<CodeChunk>(chunks.Count);
        var embeddings = new List<float[]>(chunks.Count);
        foreach (var result in results)
        {
            if (result is null)
            {
                continue;
            }

            embeddedChunks.Add(result.Chunk);
            embeddings.Add(result.Embedding);
        }

        return new EmbeddedChunkBatch(embeddedChunks, embeddings);
    }

    private int GetMaxEmbeddingConcurrency()
        => Math.Clamp(_options.Indexing.MaxEmbeddingConcurrency, 1, MaxEmbeddingConcurrencyLimit);

    private int GetMaxEmbeddingBatchSize()
        => Math.Clamp(_options.Indexing.MaxEmbeddingBatchSize, 1, MaxEmbeddingBatchSizeLimit);

    private IReadOnlyList<EmbeddingWorkItem> CreateEmbeddingWorkItems(IReadOnlyList<CodeChunk> chunks)
    {
        var workItems = new List<EmbeddingWorkItem>(chunks.Count);
        var workItemsByContent = new Dictionary<string, EmbeddingWorkItem>(StringComparer.Ordinal);
        for (var index = 0; index < chunks.Count; index++)
        {
            var content = chunks[index].Content.Length > _options.Indexing.ChunkMaxChars
                ? chunks[index].Content[.._options.Indexing.ChunkMaxChars]
                : chunks[index].Content;
            var cacheKey = CreateEmbeddingCacheKey(content);
            if (workItemsByContent.TryGetValue(cacheKey, out var existing))
            {
                existing.ChunkIndexes.Add(index);
                continue;
            }

            var workItem = new EmbeddingWorkItem(cacheKey, content, [index]);
            workItemsByContent[cacheKey] = workItem;
            workItems.Add(workItem);
        }

        return workItems;
    }

    private async Task EmbedBatchIndividuallyAsync(
        IReadOnlyList<EmbeddingWorkItem> batch,
        IReadOnlyList<CodeChunk> chunks,
        EmbeddedChunk?[] results,
        List<string> warnings,
        object warningLock,
        Action reportChunkCompleted,
        CancellationToken cancellationToken)
    {
        foreach (var item in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                AssignEmbeddingResult(item, await embeddingProvider.EmbedAsync(item.Content, cancellationToken), chunks, results);
            }
            catch (EmbeddingModelNotFoundException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                lock (warningLock)
                {
                    foreach (var chunkIndex in item.ChunkIndexes)
                    {
                        var chunk = chunks[chunkIndex];
                        warnings.Add($"Embedding skipped for {chunk.FilePath}:{chunk.StartLine}-{chunk.EndLine} ({chunk.SymbolName}): {ex.Message}");
                    }
                }
            }
            finally
            {
                foreach (var _ in item.ChunkIndexes)
                {
                    reportChunkCompleted();
                }
            }
        }
    }

    private static void AssignEmbeddingResult(
        EmbeddingWorkItem item,
        float[] embedding,
        IReadOnlyList<CodeChunk> chunks,
        EmbeddedChunk?[] results)
    {
        foreach (var chunkIndex in item.ChunkIndexes)
        {
            results[chunkIndex] = new EmbeddedChunk(chunks[chunkIndex], embedding);
        }
    }

    private static string CreateEmbeddingCacheKey(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private async Task<DryRunIndexWorkspaceResult> DryRunIndexLockedAsync(
        WorkspaceIndexTargetPlan targetPlan,
        bool force,
        string indexProfile,
        CancellationToken cancellationToken,
        IProgress<IndexingProgress>? progress)
    {
        var analysis = await AnalyzeTargetPlanAsync(
            targetPlan,
            force,
            indexProfile,
            _options.Ollama.EmbeddingModel,
            allowIncompatibleScopedState: true,
            cancellationToken,
            progress);

        Report(
            progress,
            targetPlan.WorkspaceRoot,
            IndexingProgressStage.Completed,
            analysis.Chunks.Count,
            analysis.Chunks.Count,
            "Index dry run completed.");

        var estimatedChunkCountsByFile = CountChunksByFile(analysis.Chunks);
        var currentFilesWithEstimates = ApplyChunkCounts(
            analysis.CurrentFiles,
            estimatedChunkCountsByFile,
            analysis.PreviousState.Files);
        var estimatedFilesAfterIndex = analysis.MergeScopedState
            ? analysis.PreviousState.Files
                .Where(file => !FileMatchesProfile(file.Key, indexProfile) || !analysis.TargetPlan.CanDeleteMissingFile(file.Key))
                .Select(file => file.Value)
                .Concat(currentFilesWithEstimates.Values)
                .ToDictionary(file => file.FilePath, StringComparer.OrdinalIgnoreCase)
            : currentFilesWithEstimates;
        var chunkEstimateRows = CreateFileChunkEstimates(analysis, estimatedChunkCountsByFile);

        return new DryRunIndexWorkspaceResult(
            targetPlan.WorkspaceRoot,
            analysis.FilesScanned,
            analysis.Chunks.Count,
            chunkEstimateRows.Sum(file => file.EstimatedChunksToDelete),
            SumChunkCounts(estimatedFilesAfterIndex),
            indexProfile,
            analysis.FullReindex,
            analysis.StateCompatible,
            analysis.ChangedFiles.Count,
            analysis.DeletedFiles.Count,
            analysis.UnchangedFileCount,
            chunkEstimateRows,
            targetPlan.IndexedTargets,
            analysis.Warnings);
    }

    private async Task<WorkspaceIndexAnalysis> AnalyzeTargetPlanAsync(
        WorkspaceIndexTargetPlan targetPlan,
        bool force,
        string indexProfile,
        string embeddingModel,
        bool allowIncompatibleScopedState,
        CancellationToken cancellationToken,
        IProgress<IndexingProgress>? progress)
    {
        var warnings = new List<string>();
        var workspaceRoot = targetPlan.WorkspaceRoot;
        var profileFiles = targetPlan.EnumerateFiles()
            .Where(file => FileMatchesProfile(file, indexProfile))
            .ToArray();
        var currentFiles = new Dictionary<string, IndexedFileState>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;

        Report(progress, workspaceRoot, IndexingProgressStage.ScanningFiles, 0, profileFiles.Length, "Scanning files.");

        foreach (var file in profileFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var fileState = ComputeIndexedFileState(file);
                currentFiles[fileState.FilePath] = fileState;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{file}: {ex.Message}");
            }

            scanned++;
            Report(progress, workspaceRoot, IndexingProgressStage.ScanningFiles, scanned, profileFiles.Length, "Scanning files.");
        }

        Report(progress, workspaceRoot, IndexingProgressStage.ComparingState, 0, null, "Comparing with previous index state.");
        var previousState = await indexStateStore.LoadAsync(workspaceRoot, cancellationToken);
        var stateCompatible = IsStateCompatible(previousState, embeddingModel);
        var profileScoped = indexProfile != IndexProfiles.All;
        var targetScoped = !targetPlan.IsFullWorkspace;
        var mergeScopedState = profileScoped || targetScoped;
        var fullReindex = force || !stateCompatible;
        if (force)
        {
            warnings.Add(mergeScopedState
                ? $"Forced reindex requested for profile '{indexProfile}'."
                : "Forced full reindex requested.");
        }
        else if (!stateCompatible)
        {
            warnings.Add("Index state metadata changed or was missing; full reindex required.");
        }

        if (fullReindex && !mergeScopedState)
        {
            previousState = EmptyState(workspaceRoot);
        }
        else if (!stateCompatible)
        {
            if (!allowIncompatibleScopedState)
            {
                throw new InvalidOperationException("Scoped indexing requires a compatible existing index state. Run a full workspace reindex first.");
            }

            warnings.Add("Scoped indexing would require a compatible existing index state. Run a full workspace reindex before applying this scoped index.");
            previousState = EmptyState(workspaceRoot);
        }

        var changedFiles = currentFiles.Values
            .Where(file => force ||
                !previousState.Files.TryGetValue(file.FilePath, out var previous) ||
                !string.Equals(previous.Fingerprint, file.Fingerprint, StringComparison.Ordinal))
            .Select(file => file.FilePath)
            .ToArray();
        var deletedFiles = previousState.Files.Keys
            .Where(file => FileMatchesProfile(file, indexProfile) &&
                targetPlan.CanDeleteMissingFile(file) &&
                !currentFiles.ContainsKey(file))
            .ToArray();
        var changedSet = changedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unchangedFileCount = currentFiles.Keys.Count(file => !changedSet.Contains(file));
        var chunks = new List<CodeChunk>();
        var workspaceSourceIdentity = await sourceIdentityResolver.ResolveAsync(workspaceRoot, workspaceRoot, cancellationToken);

        var analyzed = 0;
        Report(progress, workspaceRoot, IndexingProgressStage.AnalyzingFiles, 0, changedFiles.Length, "Analyzing changed files.");
        foreach (var file in changedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var analyzer = await SelectAnalyzerAsync(workspaceRoot, file, cancellationToken);
            if (analyzer is null)
            {
                analyzed++;
                Report(progress, workspaceRoot, IndexingProgressStage.AnalyzingFiles, analyzed, changedFiles.Length, "Analyzing changed files.");
                continue;
            }

            try
            {
                chunks.AddRange((await analyzer.AnalyzeAsync(workspaceRoot, file, cancellationToken))
                    .Select(chunk => chunk with
                    {
                        Source = workspaceSourceIdentity.ForFile(chunk.FilePath),
                        IndexProfile = GetIndexProfile(chunk.FilePath, chunk.ContentType)
                    }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{file}: {ex.Message}");
            }

            analyzed++;
            Report(progress, workspaceRoot, IndexingProgressStage.AnalyzingFiles, analyzed, changedFiles.Length, "Analyzing changed files.");
        }

        return new WorkspaceIndexAnalysis(
            targetPlan,
            previousState,
            currentFiles,
            changedFiles,
            deletedFiles,
            chunks,
            warnings,
            profileFiles.Length,
            fullReindex,
            stateCompatible,
            mergeScopedState,
            unchangedFileCount);
    }

    public async Task<IReadOnlyList<IndexWorkspaceResult>> IndexGroupAsync(
        string workspaceGroup,
        IReadOnlyList<string>? excludeDirectories = null,
        bool force = false,
        string? indexProfile = null,
        CancellationToken cancellationToken = default,
        IProgress<IndexingProgress>? progress = null)
    {
        var group = await workspaceGroupRegistry.GetGroupAsync(workspaceGroup, cancellationToken);
        if (group is null)
        {
            throw new InvalidOperationException($"Workspace group '{workspaceGroup}' was not found.");
        }

        var groupExcludes = GetWorkspaceGroupExcludes(group, excludeDirectories);

        return await IndexTargetsAsync(
            group.Roots,
            groupExcludes,
            force,
            indexProfile,
            cancellationToken,
            progress);
    }

    public async Task<IReadOnlyList<DryRunIndexWorkspaceResult>> DryRunIndexGroupAsync(
        string workspaceGroup,
        IReadOnlyList<string>? excludeDirectories = null,
        bool force = false,
        string? indexProfile = null,
        CancellationToken cancellationToken = default,
        IProgress<IndexingProgress>? progress = null)
    {
        var group = await workspaceGroupRegistry.GetGroupAsync(workspaceGroup, cancellationToken);
        if (group is null)
        {
            throw new InvalidOperationException($"Workspace group '{workspaceGroup}' was not found.");
        }

        var groupExcludes = GetWorkspaceGroupExcludes(group, excludeDirectories);

        return await DryRunIndexTargetsAsync(
            group.Roots,
            groupExcludes,
            force,
            indexProfile,
            cancellationToken,
            progress);
    }

    private async Task<WorkspaceIndexTargetPlan> CreateTargetPlanAsync(
        string workspacePath,
        IReadOnlyList<string>? excludeDirectories,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            throw new ArgumentException("Workspace target cannot be empty.", nameof(workspacePath));
        }

        workspacePath = await ResolveWorkspaceTargetPathAsync(workspacePath, cancellationToken);
        var workspace = await workspaceDetector.DetectAsync(workspacePath, cancellationToken);
        EnsureAllowedWorkspaceRoot(workspace.RootPath);
        var workspaceRoot = NormalizePath(workspace.RootPath);
        var fullPath = NormalizePath(workspacePath);
        EnsureAllowedPath(fullPath);

        if (Directory.Exists(fullPath))
        {
            if (string.Equals(fullPath, workspaceRoot, StringComparison.OrdinalIgnoreCase))
            {
                return WorkspaceIndexTargetPlan.FullWorkspace(
                    workspaceRoot,
                    EnumerateFiles(workspaceRoot, workspaceRoot, excludeDirectories).ToArray());
            }

            EnsurePathUnderWorkspace(fullPath, workspaceRoot);
            return WorkspaceIndexTargetPlan.Scoped(
                workspaceRoot,
                indexedTargets: [fullPath],
                EnumerateFiles(workspaceRoot, fullPath, excludeDirectories).ToArray(),
                deleteRoots: [fullPath],
                deleteFiles: []);
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Workspace target '{fullPath}' does not exist.", fullPath);
        }

        EnsurePathUnderWorkspace(fullPath, workspaceRoot);
        if (IsSolutionPath(fullPath))
        {
            return CreateSolutionTargetPlan(workspaceRoot, fullPath, excludeDirectories, cancellationToken);
        }

        var fileExcludePatterns = BuildFileExcludePatterns();
        var files = _analyzers.Any(analyzer => analyzer.CanAnalyze(fullPath)) &&
            !IsExcludedFile(workspaceRoot, fullPath, fileExcludePatterns)
                ? new[] { fullPath }
                : [];

        return WorkspaceIndexTargetPlan.Scoped(
            workspaceRoot,
            indexedTargets: [fullPath],
            files,
            deleteRoots: [],
            deleteFiles: [fullPath]);
    }

    private WorkspaceIndexTargetPlan CreateSolutionTargetPlan(
        string workspaceRoot,
        string solutionPath,
        IReadOnlyList<string>? excludeDirectories,
        CancellationToken cancellationToken)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { solutionPath };
        var deleteRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deleteFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { solutionPath };
        var projectPaths = ResolveSolutionProjects(solutionPath, cancellationToken);

        foreach (var projectPath in projectPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsurePathUnderWorkspace(projectPath, workspaceRoot);
            files.Add(projectPath);
            deleteFiles.Add(projectPath);

            var projectDirectory = NormalizePath(Path.GetDirectoryName(projectPath)!);
            deleteRoots.Add(projectDirectory);

            foreach (var file in EnumerateFiles(workspaceRoot, projectDirectory, excludeDirectories))
            {
                files.Add(file);
            }

            foreach (var sharedMetadata in EnumerateSharedDotNetMetadata(workspaceRoot, projectDirectory))
            {
                files.Add(sharedMetadata);
                deleteFiles.Add(sharedMetadata);
            }
        }

        foreach (var sharedMetadata in EnumerateSharedDotNetMetadata(workspaceRoot, Path.GetDirectoryName(solutionPath)!))
        {
            files.Add(sharedMetadata);
            deleteFiles.Add(sharedMetadata);
        }

        return WorkspaceIndexTargetPlan.Scoped(
            workspaceRoot,
            indexedTargets: [solutionPath],
            files.Where(file => _analyzers.Any(analyzer => analyzer.CanAnalyze(file))).ToArray(),
            deleteRoots.ToArray(),
            deleteFiles.ToArray());
    }

    public async Task<IndexStatusResult> GetStatusAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceDetector.DetectAsync(workspacePath, cancellationToken);
        EnsureAllowedWorkspaceRoot(workspace.RootPath);
        var workspaceRoot = NormalizePath(workspace.RootPath);
        var state = await indexStateStore.LoadAsync(workspaceRoot, cancellationToken);
        var warnings = new List<string>();
        var compatible = IsStateCompatible(state, _options.Ollama.EmbeddingModel);

        if (state.StateExists && !compatible)
        {
            warnings.Add("Stored index state does not match the configured embedding model or index schema.");
        }

        return new IndexStatusResult(
            workspaceRoot,
            state.StateExists,
            state.SavedAtUtc,
            state.Files.Count,
            SumChunkCounts(state.Files),
            state.EmbeddingModel,
            state.SchemaVersion,
            state.StateExists && !compatible,
            state.Files.Values
                .OrderBy(file => file.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(file => new IndexFileChunkEstimate(
                    file.FilePath,
                    file.ChunkCount,
                    EstimatedChunksToEmbed: 0,
                    EstimatedChunksToDelete: 0))
                .ToArray(),
            warnings);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string? filePath,
        string query,
        int limit,
        bool hybrid,
        string? scope,
        string? workspaceRoot,
        string? workspaceGroup,
        string? contentType = null,
        string? retrievalMode = null,
        string? searchProfile = null,
        CancellationToken cancellationToken = default)
    {
        var embedding = await embeddingProvider.EmbedAsync(query, cancellationToken);
        var workspaces = await workspaceScopeResolver.ResolveAsync(filePath, scope, workspaceRoot, workspaceGroup, cancellationToken);
        foreach (var workspace in workspaces)
        {
            EnsureAllowedWorkspaceRoot(workspace.RootPath);
        }

        var results = new List<SearchResult>();
        var normalizedContentType = NormalizeContentType(contentType);
        var normalizedRetrievalMode = NormalizeRetrievalMode(retrievalMode);
        var normalizedSearchProfile = NormalizeSearchProfile(searchProfile);
        var candidateLimit = Math.Min(50, Math.Max(Math.Max(1, limit), Math.Max(1, limit) * SearchCandidateMultiplier));

        foreach (var workspace in workspaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.AddRange(await vectorStore.SearchAsync(
                workspace.RootPath,
                embedding,
                query,
                candidateLimit,
                hybrid,
                normalizedContentType,
                normalizedSearchProfile == IndexProfiles.All ? null : normalizedSearchProfile,
                cancellationToken));
        }

        var reranked = results
            .Where(result => MatchesSearchProfile(result, normalizedSearchProfile))
            .Select(result => ApplyRetrievalModeBoost(result, normalizedRetrievalMode))
            .OrderByDescending(result => result.Score)
            .ToArray();

        return SearchResultPacker.Pack(reranked, limit);
    }

    public async Task<string> GetCodeContextAsync(string filePath, int line, int before, int after, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(filePath);
        EnsureAllowedPath(fullPath);
        var lines = await File.ReadAllLinesAsync(fullPath, cancellationToken);
        var start = Math.Max(1, line - Math.Max(0, before));
        var end = Math.Min(lines.Length, line + Math.Max(0, after));

        return string.Join(Environment.NewLine, Enumerable.Range(start, end - start + 1)
            .Select(number => $"{number,5}: {lines[number - 1]}"));
    }

    public async Task<string?> GetSymbolDetailsAsync(string filePath, string symbolName, CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceDetector.DetectAsync(filePath, cancellationToken);
        EnsureAllowedWorkspaceRoot(workspace.RootPath);
        EnsureAllowedPath(filePath);
        var fullPath = Path.GetFullPath(filePath);
        var analyzer = await SelectAnalyzerAsync(workspace.RootPath, fullPath, cancellationToken);
        if (analyzer is null)
        {
            return null;
        }

        var chunks = await analyzer.AnalyzeAsync(workspace.RootPath, fullPath, cancellationToken);
        return chunks.FirstOrDefault(chunk => string.Equals(chunk.SymbolName, symbolName, StringComparison.OrdinalIgnoreCase))?.Content;
    }

    private IEnumerable<string> EnumerateFiles(string rootPath, string scanRootPath, IReadOnlyList<string>? excludeDirectories)
    {
        var excluded = BuildExcludeSet(excludeDirectories);
        var fileExcludePatterns = BuildFileExcludePatterns();
        var pending = new Stack<string>();
        pending.Push(scanRootPath);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            IEnumerable<string> subdirectories;
            IEnumerable<string> files;
            try
            {
                subdirectories = Directory.EnumerateDirectories(directory);
                files = Directory.EnumerateFiles(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in subdirectories)
            {
                if (!IsExcludedDirectory(rootPath, child, excluded))
                {
                    pending.Push(child);
                }
            }

            foreach (var file in files)
            {
                if (_analyzers.Any(analyzer => analyzer.CanAnalyze(file)) && !IsExcludedFile(rootPath, file, fileExcludePatterns))
                {
                    yield return file;
                }
            }
        }
    }

    private IReadOnlyList<string> ResolveSolutionProjects(string solutionPath, CancellationToken cancellationToken)
    {
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(ReadProjectsFromSolution(solutionPath));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectPath = pending.Dequeue();
            if (!projects.Add(projectPath))
            {
                continue;
            }

            foreach (var reference in ReadProjectReferences(projectPath))
            {
                pending.Enqueue(reference);
            }
        }

        return projects.ToArray();
    }

    private static IReadOnlyList<string> ReadProjectsFromSolution(string solutionPath)
    {
        var solutionDirectory = Path.GetDirectoryName(solutionPath)!;
        var extension = Path.GetExtension(solutionPath);
        var projects = new List<string>();

        foreach (var line in File.ReadLines(solutionPath))
        {
            var matches = string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase)
                ? SlnxProjectRegex.Matches(line).Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
                : SolutionProjectRegex.Matches(line).Select(match => match.Groups[1].Value);

            foreach (var project in matches)
            {
                if (!project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var projectPath = NormalizePath(Path.Combine(solutionDirectory, project));
                if (File.Exists(projectPath))
                {
                    projects.Add(projectPath);
                }
            }
        }

        return projects.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> ReadProjectReferences(string projectPath)
    {
        try
        {
            var projectDirectory = Path.GetDirectoryName(projectPath)!;
            var document = XDocument.Load(projectPath, LoadOptions.None);
            return document
                .Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "ProjectReference", StringComparison.OrdinalIgnoreCase))
                .Select(element => element.Attribute("Include")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => NormalizePath(Path.Combine(projectDirectory, value!)))
                .Where(File.Exists)
                .Where(value => string.Equals(Path.GetExtension(value), ".csproj", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateSharedDotNetMetadata(string workspaceRoot, string startDirectory)
    {
        var root = NormalizePath(workspaceRoot);
        var cursor = new DirectoryInfo(NormalizePath(startDirectory));

        while (cursor is not null)
        {
            foreach (var fileName in SharedDotNetMetadataFiles)
            {
                var filePath = Path.Combine(cursor.FullName, fileName);
                if (File.Exists(filePath))
                {
                    yield return NormalizePath(filePath);
                }
            }

            if (string.Equals(NormalizePath(cursor.FullName), root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            cursor = cursor.Parent;
        }
    }

    private async Task<ICodeAnalyzer?> SelectAnalyzerAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken)
    {
        ICodeAnalyzer? selected = null;
        AnalyzerMatch selectedMatch = AnalyzerMatch.No;

        foreach (var analyzer in _analyzers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var match = analyzer switch
            {
                IContentAwareAnalyzer contentAware => await contentAware.MatchAsync(workspaceRoot, filePath, cancellationToken),
                _ when analyzer.CanAnalyze(filePath) => AnalyzerMatch.Supported(),
                _ => AnalyzerMatch.No
            };

            if (!match.CanAnalyze)
            {
                continue;
            }

            if (selected is null || match.Confidence > selectedMatch.Confidence)
            {
                selected = analyzer;
                selectedMatch = match;
            }
        }

        return selected;
    }

    private IReadOnlyList<string> GetWorkspaceGroupExcludes(WorkspaceGroupRecord group, IReadOnlyList<string>? excludeDirectories)
    {
        return _options.Workspace.ExcludeDirectories
            .Concat(group.ExcludeDirectories)
            .Concat(excludeDirectories ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<string>> GetWorkspaceGroupsForRootAsync(string workspaceRoot, CancellationToken cancellationToken)
    {
        var normalizedWorkspaceRoot = NormalizePath(workspaceRoot);
        return (await workspaceGroupRegistry.GetGroupsAsync(cancellationToken))
            .Where(group => group.Roots
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(NormalizePath)
                .Any(root => string.Equals(root, normalizedWorkspaceRoot, StringComparison.OrdinalIgnoreCase)))
            .Select(group => group.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private HashSet<string> BuildExcludeSet(IReadOnlyList<string>? excludeDirectories)
        => _options.Workspace.ExcludeDirectories
            .Concat(excludeDirectories ?? [])
            .Select(NormalizeExcludeDirectory)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<Regex> BuildFileExcludePatterns()
        => _options.Workspace.ExcludeFilePatterns
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => new Regex(WildcardToRegex(value), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToArray();

    private static bool IsExcludedDirectory(string rootPath, string directoryPath, HashSet<string> excluded)
    {
        var directoryName = Path.GetFileName(directoryPath);
        if (excluded.Contains(directoryName))
        {
            return true;
        }

        var relativePath = Path.GetRelativePath(rootPath, directoryPath);
        return excluded.Contains(NormalizeExcludeDirectory(relativePath));
    }

    private static string NormalizeExcludeDirectory(string directory)
        => directory.Trim()
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private bool IsExcludedFile(string rootPath, string filePath, IReadOnlyList<Regex> fileExcludePatterns)
    {
        var relativePath = Path.GetRelativePath(rootPath, filePath)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var fileName = Path.GetFileName(filePath);

        if (fileExcludePatterns.Any(pattern => pattern.IsMatch(fileName) || pattern.IsMatch(relativePath)))
        {
            return true;
        }

        return _options.Workspace.ExcludeAutoGeneratedFiles && IsAutoGeneratedFile(filePath);
    }

    private static bool IsAutoGeneratedFile(string filePath)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new StreamReader(stream, leaveOpen: false);
            var buffer = new char[Math.Min(4096, (int)Math.Min(stream.Length, int.MaxValue))];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read).Contains("<auto-generated", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string WildcardToRegex(string pattern)
    {
        var normalized = pattern.Trim()
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return "^" + Regex.Escape(normalized)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
    }

    private async Task<int> GetPreviousRegistryChunkCountAsync(string workspaceRoot, CancellationToken cancellationToken)
    {
        var previous = (await indexedWorkspaceRegistry.GetIndexedWorkspacesAsync(cancellationToken))
            .FirstOrDefault(workspace => string.Equals(workspace.WorkspaceRoot, workspaceRoot, StringComparison.OrdinalIgnoreCase));

        return previous?.ChunksIndexed ?? 0;
    }

    private static IReadOnlyDictionary<string, int> CountChunksByFile(IReadOnlyList<CodeChunk> chunks)
        => chunks
            .GroupBy(chunk => NormalizePath(chunk.FilePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, IndexedFileState> ApplyChunkCounts(
        IReadOnlyDictionary<string, IndexedFileState> files,
        IReadOnlyDictionary<string, int> chunkCountsByFile,
        IReadOnlyDictionary<string, IndexedFileState> previousFiles)
        => files.Values.ToDictionary(
            file => file.FilePath,
            file => file with
            {
                ChunkCount = chunkCountsByFile.TryGetValue(file.FilePath, out var chunkCount)
                    ? chunkCount
                    : previousFiles.TryGetValue(file.FilePath, out var previous)
                        ? previous.ChunkCount
                        : file.ChunkCount
            },
            StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<IndexFileChunkEstimate> CreateFileChunkEstimates(
        WorkspaceIndexAnalysis analysis,
        IReadOnlyDictionary<string, int> estimatedChunkCountsByFile)
    {
        var changedSet = analysis.ChangedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deletedSet = analysis.DeletedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = analysis.CurrentFiles.Keys
            .Concat(analysis.DeletedFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase);

        return paths.Select(file =>
        {
            var currentChunks = analysis.PreviousState.Files.TryGetValue(file, out var previous)
                ? previous.ChunkCount
                : 0;
            var estimatedChunksToEmbed = changedSet.Contains(file) && estimatedChunkCountsByFile.TryGetValue(file, out var chunkCount)
                ? chunkCount
                : 0;
            var estimatedChunksToDelete = (changedSet.Contains(file) || deletedSet.Contains(file)) ? currentChunks : 0;

            return new IndexFileChunkEstimate(file, currentChunks, estimatedChunksToEmbed, estimatedChunksToDelete);
        }).ToArray();
    }

    private static int SumChunkCounts(IReadOnlyDictionary<string, IndexedFileState> files)
        => SumChunkCounts(files.Values);

    private static int SumChunkCounts(IEnumerable<IndexedFileState> files)
        => files.Sum(file => file.ChunkCount);

    private static IndexedFileState ComputeIndexedFileState(string filePath)
    {
        var info = new FileInfo(filePath);
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return new IndexedFileState(
            NormalizePath(filePath),
            Convert.ToHexString(hash),
            info.Length,
            info.LastWriteTimeUtc);
    }

    private static bool IsStateCompatible(WorkspaceIndexState state, string embeddingModel)
        => state.StateExists &&
            string.Equals(state.EmbeddingModel, embeddingModel, StringComparison.Ordinal) &&
            string.Equals(state.SchemaVersion, IndexSchemaVersion, StringComparison.Ordinal);

    private static string? NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType) ||
            string.Equals(contentType, IndexedContentTypes.All, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = contentType.Trim().ToLowerInvariant();
        return normalized switch
        {
            IndexedContentTypes.Code or
                IndexedContentTypes.Documentation or
                IndexedContentTypes.Markup or
                IndexedContentTypes.ProjectMetadata => normalized,
            _ => throw new ArgumentException(
                $"Unsupported content_type '{contentType}'. Use code, documentation, markup, project_metadata, or all.",
                nameof(contentType))
        };
    }

    private static string NormalizeRetrievalMode(string? retrievalMode)
    {
        if (string.IsNullOrWhiteSpace(retrievalMode))
        {
            return RetrievalModes.Balanced;
        }

        var normalized = retrievalMode.Trim().ToLowerInvariant();
        return normalized switch
        {
            RetrievalModes.Balanced or RetrievalModes.DocsFirst or RetrievalModes.CodeFirst => normalized,
            _ => throw new ArgumentException(
                $"Unsupported retrieval_mode '{retrievalMode}'. Use balanced, docs_first, or code_first.",
                nameof(retrievalMode))
        };
    }

    private static SearchResult ApplyRetrievalModeBoost(SearchResult result, string retrievalMode)
    {
        var multiplier = retrievalMode switch
        {
            RetrievalModes.DocsFirst when string.Equals(result.ContentType, IndexedContentTypes.Documentation, StringComparison.OrdinalIgnoreCase) => 1.15d,
            RetrievalModes.DocsFirst when string.Equals(result.ContentType, IndexedContentTypes.ProjectMetadata, StringComparison.OrdinalIgnoreCase) => 1.05d,
            RetrievalModes.CodeFirst when string.Equals(result.ContentType, IndexedContentTypes.Code, StringComparison.OrdinalIgnoreCase) => 1.15d,
            RetrievalModes.CodeFirst when string.Equals(result.ContentType, IndexedContentTypes.Markup, StringComparison.OrdinalIgnoreCase) => 1.05d,
            _ => 1d
        };

        return multiplier == 1d ? result : result with { Score = result.Score * multiplier };
    }

    private static string NormalizeSearchProfile(string? searchProfile)
    {
        if (string.IsNullOrWhiteSpace(searchProfile))
        {
            return IndexProfiles.All;
        }

        var normalized = IndexProfiles.Normalize(searchProfile);
        return normalized switch
        {
            IndexProfiles.All => IndexProfiles.All,
            IndexProfiles.Code => IndexProfiles.Code,
            IndexProfiles.Documentation => IndexProfiles.Documentation,
            IndexProfiles.Metadata => IndexProfiles.Metadata,
            IndexProfiles.Frontend => IndexProfiles.Frontend,
            IndexProfiles.Tests => IndexProfiles.Tests,
            _ => throw new ArgumentException(
                $"Unsupported search_profile '{searchProfile}'. Use {string.Join(", ", IndexProfiles.Supported)}.",
                nameof(searchProfile))
        };
    }

    private static bool MatchesSearchProfile(SearchResult result, string searchProfile)
        => searchProfile switch
        {
            IndexProfiles.All => true,
            IndexProfiles.Code => string.Equals(result.IndexProfile, IndexProfiles.Code, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(result.ContentType, IndexedContentTypes.Code, StringComparison.OrdinalIgnoreCase),
            IndexProfiles.Documentation => string.Equals(result.IndexProfile, IndexProfiles.Documentation, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(result.ContentType, IndexedContentTypes.Documentation, StringComparison.OrdinalIgnoreCase),
            IndexProfiles.Metadata => string.Equals(result.IndexProfile, IndexProfiles.Metadata, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(result.ContentType, IndexedContentTypes.ProjectMetadata, StringComparison.OrdinalIgnoreCase),
            IndexProfiles.Frontend => string.Equals(result.IndexProfile, IndexProfiles.Frontend, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(result.ContentType, IndexedContentTypes.Markup, StringComparison.OrdinalIgnoreCase) ||
                IsFrontendLanguage(result.Language),
            IndexProfiles.Tests => string.Equals(result.IndexProfile, IndexProfiles.Tests, StringComparison.OrdinalIgnoreCase) ||
                IsTestPath(result.FilePath) ||
                result.SymbolName.Contains("test", StringComparison.OrdinalIgnoreCase),
            _ => true
        };

    private static bool IsFrontendLanguage(string language)
        => language.Equals("javascript", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("typescript", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("jsx", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("tsx", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("html", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("css", StringComparison.OrdinalIgnoreCase);

    private static bool FileMatchesProfile(string filePath, string profile)
        => profile == IndexProfiles.All ||
            string.Equals(GetIndexProfile(filePath, GuessContentType(filePath)), profile, StringComparison.OrdinalIgnoreCase);

    private static string GetIndexProfile(string filePath, string contentType)
    {
        if (string.Equals(contentType, IndexedContentTypes.Documentation, StringComparison.OrdinalIgnoreCase))
        {
            return IndexProfiles.Documentation;
        }

        if (string.Equals(contentType, IndexedContentTypes.ProjectMetadata, StringComparison.OrdinalIgnoreCase))
        {
            return IndexProfiles.Metadata;
        }

        if (IsTestPath(filePath))
        {
            return IndexProfiles.Tests;
        }

        if (string.Equals(contentType, IndexedContentTypes.Markup, StringComparison.OrdinalIgnoreCase) ||
            IsFrontendPath(filePath))
        {
            return IndexProfiles.Frontend;
        }

        return IndexProfiles.Code;
    }

    private static string GuessContentType(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".mdx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase))
        {
            return IndexedContentTypes.Documentation;
        }

        if (IsProjectMetadataPath(filePath))
        {
            return IndexedContentTypes.ProjectMetadata;
        }

        if (IsMarkupPath(filePath))
        {
            return IndexedContentTypes.Markup;
        }

        return IndexedContentTypes.Code;
    }

    private static bool IsSolutionPath(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProjectMetadataPath(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var extension = Path.GetExtension(filePath);
        return string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".props", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".targets", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "Directory.Packages.props", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "global.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "appsettings.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "launchSettings.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "NuGet.config", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, ".editorconfig", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "Dockerfile", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".yaml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFrontendPath(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (extension is ".jsx" or ".tsx" or ".vue" or ".svelte" or ".css" or ".scss")
        {
            return true;
        }

        var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            string.Equals(segment, "frontend", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "client", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "ui", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "wwwroot", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsMarkupPath(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension is ".html" or ".htm" or ".razor" or ".cshtml";
    }

    private static bool IsTestPath(string filePath)
    {
        var normalized = filePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var fileName = Path.GetFileNameWithoutExtension(normalized);
        return normalized.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains($"{Path.DirectorySeparatorChar}test{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("Test", StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureAllowedWorkspaceRoot(string workspaceRoot)
        => EnsureAllowedPath(workspaceRoot);

    private static void EnsurePathUnderWorkspace(string path, string workspaceRoot)
    {
        if (!IsPathUnderRoot(NormalizePath(path), NormalizePath(workspaceRoot)))
        {
            throw new InvalidOperationException($"Workspace target '{path}' is outside detected workspace root '{workspaceRoot}'.");
        }
    }

    private void EnsureAllowedPath(string path)
    {
        if (_options.Workspace.AllowedPaths.Count == 0)
        {
            return;
        }

        var fullPath = NormalizePath(path);
        var allowed = _options.Workspace.AllowedPaths
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizePath);

        if (allowed.Any(root => IsPathUnderRoot(fullPath, root)))
        {
            return;
        }

        throw new UnauthorizedAccessException($"Path '{fullPath}' is not under any configured RagNet:Workspace:AllowedPaths root.");
    }

    private static bool IsPathUnderRoot(string path, string root)
        => string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return fullPath.Length == root?.Length
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private async Task<string> ResolveWorkspaceTargetPathAsync(string workspacePath, CancellationToken cancellationToken)
    {
        if (!IsWorkspaceNameAlias(workspacePath))
        {
            return workspacePath;
        }

        var workspaceName = workspacePath.Trim();
        var matches = (await indexedWorkspaceRegistry.GetIndexedWorkspacesAsync(cancellationToken))
            .Where(workspace => string.Equals(Path.GetFileName(NormalizePath(workspace.WorkspaceRoot)), workspaceName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0].WorkspaceRoot,
            0 => throw new InvalidOperationException($"Workspace '{workspaceName}' has not been indexed yet. Use a full path for the first index, then the workspace name can be used for incremental indexing."),
            _ => throw new InvalidOperationException($"Workspace name '{workspaceName}' matches {matches.Length} indexed workspaces. Use a full path.")
        };
    }

    private static bool IsWorkspaceNameAlias(string workspacePath)
    {
        var trimmed = workspacePath.Trim();
        return !Path.IsPathFullyQualified(trimmed) &&
            trimmed.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0 &&
            !Directory.Exists(trimmed) &&
            !File.Exists(trimmed);
    }

    private static WorkspaceIndexState EmptyState(string workspaceRoot)
        => new(
            workspaceRoot,
            new Dictionary<string, IndexedFileState>(StringComparer.OrdinalIgnoreCase),
            EmbeddingModel: null,
            SchemaVersion: null,
            SavedAtUtc: null,
            StateExists: false);

    private static void Report(
        IProgress<IndexingProgress>? progress,
        string workspaceRoot,
        IndexingProgressStage stage,
        int current,
        int? total,
        string message)
        => progress?.Report(new IndexingProgress(workspaceRoot, stage, current, total, message));

    private sealed record WorkspaceIndexTargetPlan(
        string WorkspaceRoot,
        bool IsFullWorkspace,
        IReadOnlyList<string> IndexedTargets,
        IReadOnlyList<string> Files,
        IReadOnlyList<string> DeleteRoots,
        IReadOnlyList<string> DeleteFiles)
    {
        public static WorkspaceIndexTargetPlan FullWorkspace(string workspaceRoot, IReadOnlyList<string> files)
            => new(workspaceRoot, true, [NormalizePath(workspaceRoot)], NormalizeDistinct(files), [workspaceRoot], []);

        public static WorkspaceIndexTargetPlan Scoped(
            string workspaceRoot,
            IReadOnlyList<string> indexedTargets,
            IReadOnlyList<string> files,
            IReadOnlyList<string> deleteRoots,
            IReadOnlyList<string> deleteFiles)
            => new(
                workspaceRoot,
                false,
                NormalizeDistinct(indexedTargets),
                NormalizeDistinct(files),
                NormalizeDistinct(deleteRoots),
                NormalizeDistinct(deleteFiles));

        public WorkspaceIndexTargetPlan Merge(WorkspaceIndexTargetPlan other)
        {
            if (!string.Equals(WorkspaceRoot, other.WorkspaceRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Cannot merge index target plans from different workspace roots.");
            }

            if (IsFullWorkspace)
            {
                return this;
            }

            if (other.IsFullWorkspace)
            {
                return other;
            }

            return Scoped(
                WorkspaceRoot,
                IndexedTargets.Concat(other.IndexedTargets).ToArray(),
                Files.Concat(other.Files).ToArray(),
                DeleteRoots.Concat(other.DeleteRoots).ToArray(),
                DeleteFiles.Concat(other.DeleteFiles).ToArray());
        }

        public IEnumerable<string> EnumerateFiles()
            => Files;

        public bool CanDeleteMissingFile(string filePath)
        {
            var normalized = NormalizePath(filePath);
            return IsFullWorkspace ||
                DeleteFiles.Any(file => string.Equals(file, normalized, StringComparison.OrdinalIgnoreCase)) ||
                DeleteRoots.Any(root => IsPathUnderRoot(normalized, root));
        }

        private static IReadOnlyList<string> NormalizeDistinct(IEnumerable<string> paths)
            => paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    private sealed record WorkspaceIndexAnalysis(
        WorkspaceIndexTargetPlan TargetPlan,
        WorkspaceIndexState PreviousState,
        IReadOnlyDictionary<string, IndexedFileState> CurrentFiles,
        IReadOnlyList<string> ChangedFiles,
        IReadOnlyList<string> DeletedFiles,
        IReadOnlyList<CodeChunk> Chunks,
        List<string> Warnings,
        int FilesScanned,
        bool FullReindex,
        bool StateCompatible,
        bool MergeScopedState,
        int UnchangedFileCount);

    private sealed record EmbeddedChunk(CodeChunk Chunk, float[] Embedding);

    private sealed record EmbeddedChunkBatch(IReadOnlyList<CodeChunk> Chunks, IReadOnlyList<float[]> Embeddings);

    private sealed record EmbeddingWorkItem(string CacheKey, string Content, List<int> ChunkIndexes);
}
