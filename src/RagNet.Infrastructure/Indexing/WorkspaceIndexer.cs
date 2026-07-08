using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RagNet.Mcp.Analyzers.Interfaces;
using RagNet.Mcp.Configuration;
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
    IEnumerable<ICodeAnalyzer> analyzers,
    IWorkspaceIndexStateStore indexStateStore,
    IEmbeddingProvider embeddingProvider,
    IVectorStore vectorStore,
    ISourceIdentityResolver sourceIdentityResolver,
    IOptions<RagNetOptions> options) : IWorkspaceIndexer
{
    private const string IndexSchemaVersion = "ragnet-index-v2/analyzers-v1";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> IndexLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyList<ICodeAnalyzer> _analyzers = analyzers.ToArray();
    private readonly RagNetOptions _options = options.Value;

    public async Task<IndexWorkspaceResult> IndexAsync(
        string workspacePath,
        IReadOnlyList<string>? excludeDirectories = null,
        bool force = false,
        CancellationToken cancellationToken = default,
        IProgress<IndexingProgress>? progress = null)
    {
        var workspace = await workspaceDetector.DetectAsync(workspacePath, cancellationToken);
        EnsureAllowedWorkspaceRoot(workspace.RootPath);
        var workspaceRoot = NormalizePath(workspace.RootPath);
        Report(progress, workspaceRoot, IndexingProgressStage.Starting, 0, null, "Preparing workspace index.");
        var indexLock = IndexLocks.GetOrAdd(workspaceRoot, _ => new SemaphoreSlim(1, 1));
        await indexLock.WaitAsync(cancellationToken);

        try
        {
            return await IndexLockedAsync(workspaceRoot, excludeDirectories, force, cancellationToken, progress);
        }
        finally
        {
            indexLock.Release();
        }
    }

    private async Task<IndexWorkspaceResult> IndexLockedAsync(
        string workspaceRoot,
        IReadOnlyList<string>? excludeDirectories,
        bool force,
        CancellationToken cancellationToken,
        IProgress<IndexingProgress>? progress)
    {
        var warnings = new List<string>();
        var files = EnumerateFiles(workspaceRoot, excludeDirectories).ToArray();
        var currentFiles = new Dictionary<string, IndexedFileState>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;

        Report(progress, workspaceRoot, IndexingProgressStage.ScanningFiles, 0, files.Length, "Scanning files.");

        foreach (var file in files)
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
            Report(progress, workspaceRoot, IndexingProgressStage.ScanningFiles, scanned, files.Length, "Scanning files.");
        }

        Report(progress, workspaceRoot, IndexingProgressStage.ComparingState, 0, null, "Comparing with previous index state.");
        var previousState = await indexStateStore.LoadAsync(workspaceRoot, cancellationToken);
        var stateCompatible = IsStateCompatible(previousState);
        var fullReindex = force || !stateCompatible;
        if (force)
        {
            warnings.Add("Forced full reindex requested.");
        }
        else if (!stateCompatible)
        {
            warnings.Add("Index state metadata changed or was missing; full reindex required.");
        }

        if (fullReindex)
        {
            await vectorStore.DeleteWorkspaceAsync(workspaceRoot, cancellationToken);
            previousState = EmptyState(workspaceRoot);
        }

        var changedFiles = currentFiles.Values
            .Where(file => !previousState.Files.TryGetValue(file.FilePath, out var previous) ||
                !string.Equals(previous.Fingerprint, file.Fingerprint, StringComparison.Ordinal))
            .Select(file => file.FilePath)
            .ToArray();
        var deletedFiles = previousState.Files.Keys
            .Where(file => !currentFiles.ContainsKey(file))
            .ToArray();
        var chunks = new List<CodeChunk>();
        var workspaceSourceIdentity = await sourceIdentityResolver.ResolveAsync(workspaceRoot, workspaceRoot, cancellationToken);

        var filesToDelete = deletedFiles.Concat(changedFiles).ToArray();
        var deleted = 0;
        Report(progress, workspaceRoot, IndexingProgressStage.DeletingVectors, 0, filesToDelete.Length, "Deleting stale vectors.");
        foreach (var file in filesToDelete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await vectorStore.DeleteByFileAsync(workspaceRoot, file, cancellationToken);
            deleted++;
            Report(progress, workspaceRoot, IndexingProgressStage.DeletingVectors, deleted, filesToDelete.Length, "Deleting stale vectors.");
        }

        var analyzed = 0;
        Report(progress, workspaceRoot, IndexingProgressStage.AnalyzingFiles, 0, changedFiles.Length, "Analyzing changed files.");
        foreach (var file in changedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var analyzer = _analyzers.FirstOrDefault(candidate => candidate.CanAnalyze(file));
            if (analyzer is null)
            {
                analyzed++;
                Report(progress, workspaceRoot, IndexingProgressStage.AnalyzingFiles, analyzed, changedFiles.Length, "Analyzing changed files.");
                continue;
            }

            try
            {
                chunks.AddRange((await analyzer.AnalyzeAsync(workspaceRoot, file, cancellationToken))
                    .Select(chunk => chunk with { Source = workspaceSourceIdentity.ForFile(chunk.FilePath) }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{file}: {ex.Message}");
            }

            analyzed++;
            Report(progress, workspaceRoot, IndexingProgressStage.AnalyzingFiles, analyzed, changedFiles.Length, "Analyzing changed files.");
        }

        var embeddings = new List<float[]>(chunks.Count);
        var embedded = 0;
        Report(progress, workspaceRoot, IndexingProgressStage.CreatingEmbeddings, 0, chunks.Count, "Creating embeddings.");
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = chunk.Content.Length > _options.Indexing.ChunkMaxChars
                ? chunk.Content[.._options.Indexing.ChunkMaxChars]
                : chunk.Content;

            embeddings.Add(await embeddingProvider.EmbedAsync(content, cancellationToken));
            embedded++;
            Report(progress, workspaceRoot, IndexingProgressStage.CreatingEmbeddings, embedded, chunks.Count, "Creating embeddings.");
        }

        if (chunks.Count > 0)
        {
            Report(progress, workspaceRoot, IndexingProgressStage.UpsertingVectors, 0, chunks.Count, "Writing vectors to store.");
            await vectorStore.UpsertAsync(workspaceRoot, chunks, embeddings, cancellationToken);
            Report(progress, workspaceRoot, IndexingProgressStage.UpsertingVectors, chunks.Count, chunks.Count, "Writing vectors to store.");
        }

        Report(progress, workspaceRoot, IndexingProgressStage.SavingState, 0, null, "Saving index state.");
        await indexStateStore.SaveAsync(new WorkspaceIndexState(
            workspaceRoot,
            currentFiles,
            _options.Ollama.EmbeddingModel,
            IndexSchemaVersion,
            DateTimeOffset.UtcNow,
            StateExists: true), cancellationToken);
        indexedWorkspaceRegistry.MarkIndexed(workspaceRoot);
        Report(progress, workspaceRoot, IndexingProgressStage.Completed, chunks.Count, chunks.Count, "Indexing completed.");
        return new IndexWorkspaceResult(workspaceRoot, files.Length, chunks.Count, fullReindex, warnings);
    }

    public async Task<IReadOnlyList<IndexWorkspaceResult>> IndexGroupAsync(
        string workspaceGroup,
        IReadOnlyList<string>? excludeDirectories = null,
        bool force = false,
        CancellationToken cancellationToken = default,
        IProgress<IndexingProgress>? progress = null)
    {
        var workspaces = await workspaceScopeResolver.ResolveAsync(
            filePath: null,
            scope: "named_workspace_group",
            workspaceRoot: null,
            workspaceGroup: workspaceGroup,
            cancellationToken);
        var groupExcludes = GetWorkspaceGroupExcludes(workspaceGroup, excludeDirectories);

        var results = new List<IndexWorkspaceResult>();
        foreach (var workspace in workspaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await IndexAsync(workspace.RootPath, groupExcludes, force, cancellationToken, progress));
        }

        return results;
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
        var compatible = IsStateCompatible(state);

        if (state.StateExists && !compatible)
        {
            warnings.Add("Stored index state does not match the configured embedding model or index schema.");
        }

        return new IndexStatusResult(
            workspaceRoot,
            state.StateExists,
            state.SavedAtUtc,
            state.Files.Count,
            state.EmbeddingModel,
            state.SchemaVersion,
            state.StateExists && !compatible,
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
        CancellationToken cancellationToken = default)
    {
        var embedding = await embeddingProvider.EmbedAsync(query, cancellationToken);
        var workspaces = await workspaceScopeResolver.ResolveAsync(filePath, scope, workspaceRoot, workspaceGroup, cancellationToken);
        foreach (var workspace in workspaces)
        {
            EnsureAllowedWorkspaceRoot(workspace.RootPath);
        }

        var results = new List<SearchResult>();

        foreach (var workspace in workspaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.AddRange(await vectorStore.SearchAsync(workspace.RootPath, embedding, query, limit, hybrid, cancellationToken));
        }

        return results
            .OrderByDescending(result => result.Score)
            .Take(Math.Max(1, limit))
            .ToArray();
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
        var analyzer = _analyzers.FirstOrDefault(candidate => candidate.CanAnalyze(filePath));
        if (analyzer is null)
        {
            return null;
        }

        var chunks = await analyzer.AnalyzeAsync(workspace.RootPath, Path.GetFullPath(filePath), cancellationToken);
        return chunks.FirstOrDefault(chunk => string.Equals(chunk.SymbolName, symbolName, StringComparison.OrdinalIgnoreCase))?.Content;
    }

    private IEnumerable<string> EnumerateFiles(string rootPath, IReadOnlyList<string>? excludeDirectories)
    {
        var excluded = BuildExcludeSet(excludeDirectories);
        var fileExcludePatterns = BuildFileExcludePatterns();
        var pending = new Stack<string>();
        pending.Push(rootPath);

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

    private IReadOnlyList<string> GetWorkspaceGroupExcludes(string workspaceGroup, IReadOnlyList<string>? excludeDirectories)
    {
        var group = _options.WorkspaceGroups.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, workspaceGroup, StringComparison.OrdinalIgnoreCase));

        return _options.Workspace.ExcludeDirectories
            .Concat(group?.ExcludeDirectories ?? [])
            .Concat(excludeDirectories ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
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

    private bool IsStateCompatible(WorkspaceIndexState state)
        => state.StateExists &&
            string.Equals(state.EmbeddingModel, _options.Ollama.EmbeddingModel, StringComparison.Ordinal) &&
            string.Equals(state.SchemaVersion, IndexSchemaVersion, StringComparison.Ordinal);

    private void EnsureAllowedWorkspaceRoot(string workspaceRoot)
        => EnsureAllowedPath(workspaceRoot);

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
}
