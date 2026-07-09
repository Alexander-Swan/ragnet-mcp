using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Indexing;
using RagNet.Mcp.Indexing.Interfaces;
using RagNet.Mcp.Source.Interfaces;
using RagNet.Mcp.Storage;
using RagNet.Mcp.Workspace.Interfaces;

namespace RagNet.Mcp.Workspace;

public sealed class QdrantWorkspaceTransferService(
    HttpClient httpClient,
    IOptions<RagNetOptions> options,
    IIndexedWorkspaceRegistry workspaceRegistry,
    IWorkspaceGroupRegistry groupRegistry,
    IWorkspaceIndexStateStore stateStore,
    ISourceIdentityResolver sourceIdentityResolver,
    ILogger<QdrantWorkspaceTransferService> logger) : IWorkspaceTransferService
{
    private const int ScrollLimit = 256;
    private const int UpsertBatchSize = 128;
    private const string ManifestFileName = "ragnet-export-manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly RagNetOptions _options = options.Value;

    public async Task<WorkspaceExportResult> ExportWorkspaceAsync(
        string workspace,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var record = await ResolveWorkspaceRecordAsync(workspace, cancellationToken);
        return await ExportAsync("workspace", [record], [], outputDirectory, cancellationToken);
    }

    public async Task<WorkspaceExportResult> ExportGroupAsync(
        string group,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var groupRecord = await groupRegistry.GetGroupAsync(group, cancellationToken)
            ?? throw new InvalidOperationException($"Workspace group '{group}' was not found.");
        var records = new List<IndexedWorkspaceRecord>();
        foreach (var root in groupRecord.Roots)
        {
            records.Add(await ResolveWorkspaceRecordAsync(root, cancellationToken));
        }

        return await ExportAsync("group", records, [groupRecord], outputDirectory, cancellationToken);
    }

    public async Task<WorkspaceImportResult> ImportAsync(
        string inputDirectory,
        IReadOnlyDictionary<string, string> pathMap,
        string? workspaceRoot = null,
        string? expectedKind = null,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ReadManifestAsync(inputDirectory, cancellationToken);
        if (!string.Equals(manifest.Format, WorkspaceExportManifest.CurrentFormat, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported RagNet export format '{manifest.Format}'.");
        }

        if (!string.IsNullOrWhiteSpace(expectedKind) &&
            !string.Equals(manifest.Kind, expectedKind, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Export contains a {manifest.Kind} manifest. Use '{manifest.Kind} import' for this export.");
        }

        if (!string.Equals(manifest.SchemaVersion, IndexSchemaVersions.CurrentText, StringComparison.OrdinalIgnoreCase))
        {
            IndexSchemaVersions.EnsureCompatible(manifest.SchemaVersion, "RagNet export manifest");
        }

        var singleWorkspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot) ? null : NormalizePath(workspaceRoot);
        if (singleWorkspaceRoot is not null && manifest.Workspaces.Count != 1)
        {
            throw new ArgumentException("--workspace-root can only be used when importing an export with exactly one workspace.");
        }

        var imported = new List<ImportedWorkspaceResult>();
        var rootMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var workspace in manifest.Workspaces)
        {
            var newRoot = singleWorkspaceRoot ?? ResolveMappedRoot(workspace, pathMap);
            rootMap[NormalizePath(workspace.WorkspaceRoot)] = newRoot;

            var newCollectionName = QdrantCollectionNaming.GetCollectionName(_options.Qdrant.CollectionPrefix, newRoot);
            var exportedPoints = await ReadPointsAsync(Path.Combine(inputDirectory, workspace.PointsPath), cancellationToken);
            var importedPoints = exportedPoints
                .Select(point => RemapPoint(point, workspace, newRoot))
                .ToArray();

            await RecreateCollectionAsync(newCollectionName, workspace.VectorSize, cancellationToken);
            await UpsertPointsAsync(newCollectionName, importedPoints, cancellationToken);

            var mappedState = MapState(workspace.State, workspace.WorkspaceRoot, newRoot);
            if (mappedState is not null)
            {
                await stateStore.SaveAsync(mappedState, cancellationToken);
            }

            await workspaceRegistry.MarkIndexedAsync(MapWorkspaceRecord(workspace.Record, newRoot), cancellationToken);
            imported.Add(new ImportedWorkspaceResult(
                newRoot,
                QdrantCollectionNaming.GetWorkspaceId(newRoot),
                newCollectionName,
                importedPoints.Length));
        }

        var importedGroups = new List<string>();
        foreach (var group in manifest.Groups)
        {
            var mappedRoots = group.Roots
                .Select(root => rootMap.TryGetValue(NormalizePath(root), out var mapped) ? mapped : ResolvePathFromMap(root, pathMap))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            await groupRegistry.SaveGroupAsync(group.Name, mappedRoots, group.ExcludeDirectories, cancellationToken);
            importedGroups.Add(group.Name);
        }

        return new WorkspaceImportResult(NormalizePath(inputDirectory), imported, importedGroups);
    }

    public async Task<WorkspaceMigrationResult> MigrateAsync(CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var workspaces = await workspaceRegistry.GetIndexedWorkspacesAsync(cancellationToken);
        var updated = 0;
        foreach (var workspace in workspaces)
        {
            try
            {
                var identity = await sourceIdentityResolver.ResolveAsync(workspace.WorkspaceRoot, workspace.WorkspaceRoot, cancellationToken);
                await workspaceRegistry.MarkIndexedAsync(workspace with
                {
                    RepositoryRoot = identity.RepositoryRoot,
                    RepositoryRelativeWorkspaceRoot = GetRelativePathOrNull(identity.RepositoryRoot, workspace.WorkspaceRoot),
                    RemoteUrl = identity.RemoteUrl,
                    Branch = identity.Branch,
                    CommitSha = identity.CommitSha,
                    IndexedTargetRelativePaths = workspace.IndexedTargets
                        .Select(target => GetRelativePathOrNull(identity.RepositoryRoot, target))
                        .Where(relativePath => !string.IsNullOrWhiteSpace(relativePath))
                        .Select(relativePath => relativePath!)
                        .ToArray()
                }, cancellationToken);
                updated++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                warnings.Add($"{workspace.WorkspaceRoot}: {ex.Message}");
            }
        }

        return new WorkspaceMigrationResult(workspaces.Count, updated, warnings);
    }

    public async Task<WorkspaceCollectionStatusResult> ResolveCollectionsAsync(
        string? workspace = null,
        string? group = null,
        string? path = null,
        CancellationToken cancellationToken = default)
    {
        var records = await workspaceRegistry.GetIndexedWorkspacesAsync(cancellationToken);
        IEnumerable<IndexedWorkspaceRecord> matches = records;
        if (!string.IsNullOrWhiteSpace(group))
        {
            var groupRecord = await groupRegistry.GetGroupAsync(group, cancellationToken)
                ?? throw new InvalidOperationException($"Workspace group '{group}' was not found.");
            var roots = groupRecord.Roots.Select(NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            matches = matches.Where(record => roots.Contains(NormalizePath(record.WorkspaceRoot)));
        }

        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var record = await ResolveWorkspaceRecordAsync(workspace, cancellationToken);
            matches = matches.Where(candidate => string.Equals(candidate.WorkspaceRoot, record.WorkspaceRoot, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            var normalizedPath = NormalizePath(path);
            matches = matches
                .Where(record => IsPathUnderRoot(normalizedPath, NormalizePath(record.WorkspaceRoot)))
                .OrderByDescending(record => NormalizePath(record.WorkspaceRoot).Length);
        }

        return new WorkspaceCollectionStatusResult(matches
            .Select(record => new WorkspaceCollectionMapping(
                record.WorkspaceRoot,
                record.WorkspaceId,
                record.CollectionName,
                record.Groups,
                record.IndexedTargets))
            .ToArray());
    }

    private async Task<WorkspaceExportResult> ExportAsync(
        string kind,
        IReadOnlyList<IndexedWorkspaceRecord> records,
        IReadOnlyList<WorkspaceGroupRecord> groups,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var normalizedOutput = NormalizePath(outputDirectory);
        Directory.CreateDirectory(normalizedOutput);
        Directory.CreateDirectory(Path.Combine(normalizedOutput, "collections"));

        var workspaceExports = new List<ExportedWorkspace>();
        var resultWorkspaces = new List<WorkspaceCollectionExport>();
        foreach (var record in records.DistinctBy(record => record.WorkspaceRoot, StringComparer.OrdinalIgnoreCase))
        {
            var pointsPath = Path.Combine("collections", $"{record.CollectionName}.jsonl");
            var absolutePointsPath = Path.Combine(normalizedOutput, pointsPath);
            var pointsExported = await WritePointsAsync(record.CollectionName, absolutePointsPath, cancellationToken);
            var collectionInfo = await GetCollectionInfoAsync(record.CollectionName, cancellationToken);
            var state = await stateStore.LoadAsync(record.WorkspaceRoot, cancellationToken);
            var identity = await ResolveExportIdentityAsync(record, cancellationToken);

            workspaceExports.Add(new ExportedWorkspace(
                record,
                state.StateExists ? state : null,
                record.WorkspaceRoot,
                GetRelativePathOrNull(identity.RepositoryRoot, record.WorkspaceRoot),
                identity.RepositoryRoot,
                identity.RemoteUrl,
                identity.Branch,
                identity.CommitSha,
                record.IndexedTargets
                    .Select(target => new ExportedTarget(target, GetRelativePathOrNull(identity.RepositoryRoot, target)))
                    .ToArray(),
                collectionInfo.VectorSize,
                pointsPath.Replace(Path.DirectorySeparatorChar, '/'),
                pointsExported));
            resultWorkspaces.Add(new WorkspaceCollectionExport(
                record.WorkspaceRoot,
                record.WorkspaceId,
                record.CollectionName,
                pointsExported,
                pointsPath.Replace(Path.DirectorySeparatorChar, '/')));
        }

        var manifest = new WorkspaceExportManifest(
            WorkspaceExportManifest.CurrentFormat,
            IndexSchemaVersions.CurrentText,
            DateTimeOffset.UtcNow,
            _options.Qdrant.CollectionPrefix,
            kind,
            workspaceExports,
            groups);
        var manifestPath = Path.Combine(normalizedOutput, ManifestFileName);
        await using (var stream = File.Create(manifestPath))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
        }

        logger.LogInformation("Exported {Count} RagNet workspace(s) to {OutputDirectory}.", workspaceExports.Count, normalizedOutput);
        return new WorkspaceExportResult(kind, normalizedOutput, manifestPath, resultWorkspaces, groups.Select(group => group.Name).ToArray());
    }

    private async Task<int> WritePointsAsync(string collectionName, string path, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await using var writer = new StreamWriter(stream);
        var count = 0;
        JsonElement? offset = null;
        do
        {
            var body = new Dictionary<string, object?>
            {
                ["limit"] = ScrollLimit,
                ["with_payload"] = true,
                ["with_vector"] = true
            };
            if (offset is not null)
            {
                body["offset"] = offset.Value.Clone();
            }

            var response = await httpClient.PostAsJsonAsync(
                $"collections/{Uri.EscapeDataString(collectionName)}/points/scroll",
                body,
                JsonOptions,
                cancellationToken);
            await EnsureSuccessAsync(response, $"scroll Qdrant collection '{collectionName}'", cancellationToken);

            using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
            var result = document.RootElement.GetProperty("result");
            foreach (var point in result.GetProperty("points").EnumerateArray())
            {
                await writer.WriteLineAsync(point.GetRawText());
                count++;
            }

            offset = result.TryGetProperty("next_page_offset", out var nextOffset) && nextOffset.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
                ? nextOffset.Clone()
                : null;
        }
        while (offset is not null);

        return count;
    }

    private async Task<IReadOnlyList<ExportedQdrantPoint>> ReadPointsAsync(string path, CancellationToken cancellationToken)
    {
        var points = new List<ExportedQdrantPoint>();
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(line))
            {
                points.Add(JsonSerializer.Deserialize<ExportedQdrantPoint>(line, JsonOptions)
                    ?? throw new InvalidOperationException($"Invalid Qdrant point in '{path}'."));
            }
        }

        return points;
    }

    private async Task UpsertPointsAsync(string collectionName, IReadOnlyList<ExportedQdrantPoint> points, CancellationToken cancellationToken)
    {
        foreach (var batch in points.Chunk(UpsertBatchSize))
        {
            var response = await httpClient.PutAsJsonAsync(
                $"collections/{Uri.EscapeDataString(collectionName)}/points?wait=true",
                new { points = batch },
                JsonOptions,
                cancellationToken);
            await EnsureSuccessAsync(response, $"import {batch.Length} points into Qdrant collection '{collectionName}'", cancellationToken);
        }
    }

    private async Task RecreateCollectionAsync(string collectionName, int vectorSize, CancellationToken cancellationToken)
    {
        var deleteResponse = await httpClient.DeleteAsync($"collections/{Uri.EscapeDataString(collectionName)}?timeout=30", cancellationToken);
        if (deleteResponse.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(deleteResponse, $"delete existing Qdrant collection '{collectionName}'", cancellationToken);
        }

        var response = await httpClient.PutAsJsonAsync(
            $"collections/{Uri.EscapeDataString(collectionName)}",
            new
            {
                vectors = new
                {
                    size = vectorSize,
                    distance = "Cosine"
                }
            },
            JsonOptions,
            cancellationToken);
        await EnsureSuccessAsync(response, $"create Qdrant collection '{collectionName}'", cancellationToken);
    }

    private ExportedQdrantPoint RemapPoint(ExportedQdrantPoint point, ExportedWorkspace workspace, string newWorkspaceRoot)
    {
        var payload = point.Payload.Deserialize<JsonObject>(JsonOptions) ?? [];
        var oldWorkspaceRoot = NormalizePath(workspace.WorkspaceRoot);
        var relativePath = GetString(payload, "relative_path");
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            var filePath = GetString(payload, "file_path");
            relativePath = string.IsNullOrWhiteSpace(filePath)
                ? null
                : GetRelativePathOrNull(oldWorkspaceRoot, filePath);
        }

        payload["workspace_root"] = newWorkspaceRoot;
        payload["workspace_id"] = QdrantCollectionNaming.GetWorkspaceId(newWorkspaceRoot);
        payload["source_workspace_id"] = QdrantCollectionNaming.GetWorkspaceId(newWorkspaceRoot);
        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            payload["file_path"] = NormalizePath(Path.Combine(newWorkspaceRoot, FromPortableRelativePath(relativePath)));
            payload["relative_path"] = relativePath.Replace('\\', '/');
        }

        if (!string.IsNullOrWhiteSpace(workspace.RemoteUrl))
        {
            payload["remote_url"] = workspace.RemoteUrl;
        }

        if (!string.IsNullOrWhiteSpace(workspace.Branch))
        {
            payload["branch"] = workspace.Branch;
        }

        if (!string.IsNullOrWhiteSpace(workspace.CommitSha))
        {
            payload["commit_sha"] = workspace.CommitSha;
        }

        payload["repository_root"] = newWorkspaceRoot;
        return point with { Payload = JsonSerializer.SerializeToElement(payload, JsonOptions) };
    }

    private static WorkspaceIndexState? MapState(WorkspaceIndexState? state, string oldWorkspaceRoot, string newWorkspaceRoot)
    {
        if (state is null)
        {
            return null;
        }

        var normalizedOldRoot = NormalizePath(oldWorkspaceRoot);
        var files = state.Files.Values
            .Select(file =>
            {
                var relativePath = GetRelativePathOrNull(normalizedOldRoot, file.FilePath);
                var mappedFile = string.IsNullOrWhiteSpace(relativePath)
                    ? file.FilePath
                    : NormalizePath(Path.Combine(newWorkspaceRoot, FromPortableRelativePath(relativePath)));
                return file with { FilePath = mappedFile };
            })
            .ToDictionary(file => file.FilePath, StringComparer.OrdinalIgnoreCase);

        return state with
        {
            WorkspaceRoot = newWorkspaceRoot,
            Files = files,
            StateExists = true
        };
    }

    private IndexedWorkspaceRecord MapWorkspaceRecord(IndexedWorkspaceRecord record, string newWorkspaceRoot)
    {
        var oldWorkspaceRoot = NormalizePath(record.WorkspaceRoot);
        var mappedTargets = record.IndexedTargets
            .Select(target =>
            {
                var relativePath = GetRelativePathOrNull(oldWorkspaceRoot, target);
                return string.IsNullOrWhiteSpace(relativePath)
                    ? target
                    : NormalizePath(Path.Combine(newWorkspaceRoot, FromPortableRelativePath(relativePath)));
            })
            .ToArray();

        return record with
        {
            WorkspaceRoot = newWorkspaceRoot,
            WorkspaceId = QdrantCollectionNaming.GetWorkspaceId(newWorkspaceRoot),
            CollectionName = QdrantCollectionNaming.GetCollectionName(_options.Qdrant.CollectionPrefix, newWorkspaceRoot),
            IndexedTargets = mappedTargets,
            RepositoryRoot = newWorkspaceRoot,
            RepositoryRelativeWorkspaceRoot = ".",
            IndexedTargetRelativePaths = mappedTargets
                .Select(target => GetRelativePathOrNull(newWorkspaceRoot, target))
                .Where(relativePath => !string.IsNullOrWhiteSpace(relativePath))
                .Select(relativePath => relativePath!)
                .ToArray()
        };
    }

    private async Task<IndexedWorkspaceRecord> ResolveWorkspaceRecordAsync(string workspace, CancellationToken cancellationToken)
    {
        var records = await workspaceRegistry.GetIndexedWorkspacesAsync(cancellationToken);
        var trimmed = workspace.Trim();
        IndexedWorkspaceRecord[] matches;
        if (!Path.IsPathFullyQualified(trimmed) &&
            trimmed.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0 &&
            !Directory.Exists(trimmed) &&
            !File.Exists(trimmed))
        {
            matches = records
                .Where(record => string.Equals(Path.GetFileName(NormalizePath(record.WorkspaceRoot)), trimmed, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        else
        {
            var fullPath = NormalizePath(trimmed);
            matches = records
                .Where(record => string.Equals(NormalizePath(record.WorkspaceRoot), fullPath, StringComparison.OrdinalIgnoreCase) ||
                    IsPathUnderRoot(fullPath, NormalizePath(record.WorkspaceRoot)))
                .OrderByDescending(record => NormalizePath(record.WorkspaceRoot).Length)
                .ToArray();
        }

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"Indexed workspace '{workspace}' was not found. Use 'list workspaces' to see available workspaces."),
            _ => throw new InvalidOperationException($"Workspace '{workspace}' matches {matches.Length} indexed workspaces. Use a full workspace root.")
        };
    }

    private async Task<SourceIdentitySnapshot> ResolveExportIdentityAsync(IndexedWorkspaceRecord record, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(record.RepositoryRoot))
        {
            return new SourceIdentitySnapshot(
                NormalizePath(record.RepositoryRoot),
                record.RemoteUrl,
                record.Branch,
                record.CommitSha);
        }

        var identity = await sourceIdentityResolver.ResolveAsync(record.WorkspaceRoot, record.WorkspaceRoot, cancellationToken);
        return new SourceIdentitySnapshot(identity.RepositoryRoot, identity.RemoteUrl, identity.Branch, identity.CommitSha);
    }

    private async Task<QdrantCollectionInfo> GetCollectionInfoAsync(string collectionName, CancellationToken cancellationToken)
    {
        var response = await httpClient.GetAsync($"collections/{Uri.EscapeDataString(collectionName)}", cancellationToken);
        await EnsureSuccessAsync(response, $"read Qdrant collection '{collectionName}'", cancellationToken);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var vectorSize = document.RootElement
            .GetProperty("result")
            .GetProperty("config")
            .GetProperty("params")
            .GetProperty("vectors")
            .GetProperty("size")
            .GetInt32();
        return new QdrantCollectionInfo(vectorSize);
    }

    private static async Task<WorkspaceExportManifest> ReadManifestAsync(string inputDirectory, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(inputDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"RagNet export manifest was not found at '{manifestPath}'.");
        }

        await using var stream = File.OpenRead(manifestPath);
        return await JsonSerializer.DeserializeAsync<WorkspaceExportManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"RagNet export manifest '{manifestPath}' is invalid.");
    }

    private static string ResolveMappedRoot(ExportedWorkspace workspace, IReadOnlyDictionary<string, string> pathMap)
    {
        if (pathMap.Count == 0)
        {
            throw new ArgumentException("Import requires --workspace-root for single-workspace exports or one or more --path-map <old-root=new-root> entries.");
        }

        return ResolvePathFromMap(workspace.WorkspaceRoot, pathMap);
    }

    private static string ResolvePathFromMap(string path, IReadOnlyDictionary<string, string> pathMap)
    {
        var normalizedPath = NormalizePath(path);
        foreach (var pair in pathMap.OrderByDescending(pair => NormalizePath(pair.Key).Length))
        {
            var oldRoot = NormalizePath(pair.Key);
            if (IsPathUnderRoot(normalizedPath, oldRoot))
            {
                var relativePath = GetRelativePathOrNull(oldRoot, normalizedPath);
                return string.IsNullOrWhiteSpace(relativePath)
                    ? NormalizePath(pair.Value)
                    : NormalizePath(Path.Combine(pair.Value, FromPortableRelativePath(relativePath)));
            }
        }

        throw new ArgumentException($"No --path-map entry maps exported path '{path}'.");
    }

    private static string? GetString(JsonObject payload, string name)
        => payload.TryGetPropertyValue(name, out var value) && value is not null
            ? value.GetValue<string?>()
            : null;

    private static string FromPortableRelativePath(string path)
        => path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    private static string? GetRelativePathOrNull(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalizedRoot = NormalizePath(root);
        var normalizedPath = NormalizePath(path);
        if (!IsPathUnderRoot(normalizedPath, normalizedRoot))
        {
            return null;
        }

        return Path.GetRelativePath(normalizedRoot, normalizedPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static bool IsPathUnderRoot(string path, string root)
        => string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return fullPath.Length == root?.Length
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"Failed to {action}. Qdrant returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
            null,
            response.StatusCode);
    }

    private sealed record WorkspaceExportManifest(
        string Format,
        string SchemaVersion,
        DateTimeOffset ExportedAtUtc,
        string CollectionPrefix,
        string Kind,
        IReadOnlyList<ExportedWorkspace> Workspaces,
        IReadOnlyList<WorkspaceGroupRecord> Groups)
    {
        public const string CurrentFormat = "ragnet-qdrant-workspace-export-v1";
    }

    private sealed record ExportedWorkspace(
        IndexedWorkspaceRecord Record,
        WorkspaceIndexState? State,
        string WorkspaceRoot,
        string? RepositoryRelativeWorkspaceRoot,
        string RepositoryRoot,
        string? RemoteUrl,
        string? Branch,
        string? CommitSha,
        IReadOnlyList<ExportedTarget> IndexedTargets,
        int VectorSize,
        string PointsPath,
        int PointsExported);

    private sealed record ExportedTarget(string Path, string? RelativePath);

    private sealed record ExportedQdrantPoint(string Id, JsonElement Vector, JsonElement Payload);

    private sealed record QdrantCollectionInfo(int VectorSize);

    private sealed record SourceIdentitySnapshot(string RepositoryRoot, string? RemoteUrl, string? Branch, string? CommitSha);
}
