using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Indexing.Interfaces;
using RagNet.Mcp.Storage;

namespace RagNet.Mcp.Indexing;

public sealed class QdrantWorkspaceIndexStateStore(
    HttpClient httpClient,
    IOptions<RagNetOptions> options,
    ILogger<QdrantWorkspaceIndexStateStore> logger) : IWorkspaceIndexStateStore
{
    private const string Distance = "Cosine";
    private const int VectorSize = 1;
    private const int FilesPerPage = 512;
    private const int PagePointsPerUpsert = 16;
    private const int PagePointsPerLoad = 64;
    private const string ManifestRecordKind = "manifest";
    private const string FilePageRecordKind = "file_page";
    private const string PagedStateFormat = "paged";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RagNetOptions _options = options.Value;
    private readonly HttpClient _httpClient = httpClient;

    public async Task<WorkspaceIndexState> LoadAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        var collectionName = GetStateCollectionName();
        if (!await CollectionExistsAsync(collectionName, cancellationToken))
        {
            return Empty(workspaceRoot);
        }

        var normalizedWorkspaceRoot = NormalizePath(workspaceRoot);
        var workspaceId = QdrantCollectionNaming.GetWorkspaceId(normalizedWorkspaceRoot);
        var response = await _httpClient.PostAsJsonAsync(
            $"collections/{Uri.EscapeDataString(collectionName)}/points",
            new
            {
                ids = new[] { CreatePointId(workspaceId) },
                with_payload = true,
                with_vector = false
            },
            JsonOptions,
            cancellationToken);

        await EnsureSuccessAsync(response, $"load index state for '{workspaceRoot}' from Qdrant collection '{collectionName}'", cancellationToken);

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("result", out var result) || result.GetArrayLength() == 0)
        {
            return Empty(normalizedWorkspaceRoot);
        }

        foreach (var point in result.EnumerateArray())
        {
            if (!point.TryGetProperty("payload", out var payload))
            {
                continue;
            }

            if (IsPagedManifest(payload))
            {
                return await ReadPagedStateAsync(collectionName, payload, normalizedWorkspaceRoot, cancellationToken);
            }

            if (TryReadLegacyState(payload, normalizedWorkspaceRoot, out var state))
            {
                return state;
            }
        }

        return Empty(normalizedWorkspaceRoot);
    }

    public async Task SaveAsync(WorkspaceIndexState state, CancellationToken cancellationToken = default)
    {
        var collectionName = GetStateCollectionName();
        await EnsureCollectionAsync(collectionName, cancellationToken);

        var workspaceRoot = NormalizePath(state.WorkspaceRoot);
        var workspaceId = QdrantCollectionNaming.GetWorkspaceId(workspaceRoot);
        var orderedFiles = state.Files.Values
            .OrderBy(file => file.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var pageCount = (int)Math.Ceiling(orderedFiles.Length / (double)FilesPerPage);

        var manifestResponse = await _httpClient.PutAsJsonAsync(
            $"collections/{Uri.EscapeDataString(collectionName)}/points?wait=true",
            new
            {
                points = new[]
                {
                    new
                    {
                        id = CreatePointId(workspaceId),
                        vector = new[] { 1f },
                        payload = new
                        {
                            record_kind = ManifestRecordKind,
                            state_format = PagedStateFormat,
                            workspace_root = workspaceRoot,
                            workspace_id = workspaceId,
                            embedding_model = state.EmbeddingModel,
                            schema_version = IndexSchemaVersions.Current,
                            saved_at_utc = state.SavedAtUtc,
                            is_complete = state.IsComplete,
                            indexing_collection_name = state.IndexingCollectionName,
                            file_count = orderedFiles.Length,
                            page_count = pageCount,
                            page_size = FilesPerPage
                        }
                    }
                }
            },
            JsonOptions,
            cancellationToken);

        await EnsureSuccessAsync(manifestResponse, $"save index state manifest for '{state.WorkspaceRoot}' to Qdrant collection '{collectionName}'", cancellationToken);

        foreach (var pointBatch in CreateFilePagePoints(workspaceId, workspaceRoot, state, orderedFiles)
            .Chunk(PagePointsPerUpsert))
        {
            var pageResponse = await _httpClient.PutAsJsonAsync(
                $"collections/{Uri.EscapeDataString(collectionName)}/points?wait=true",
                new
                {
                    points = pointBatch
                },
                JsonOptions,
                cancellationToken);

            await EnsureSuccessAsync(pageResponse, $"save index state pages for '{state.WorkspaceRoot}' to Qdrant collection '{collectionName}'", cancellationToken);
        }

        logger.LogInformation("Saved index state for {WorkspaceRoot} in Qdrant collection {CollectionName}.", workspaceRoot, collectionName);
    }

    public async Task DeleteAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        var collectionName = GetStateCollectionName();
        if (!await CollectionExistsAsync(collectionName, cancellationToken))
        {
            return;
        }

        var workspaceId = QdrantCollectionNaming.GetWorkspaceId(NormalizePath(workspaceRoot));
        var response = await _httpClient.PostAsJsonAsync(
            $"collections/{Uri.EscapeDataString(collectionName)}/points/delete?wait=true",
            new
            {
                filter = new
                {
                    must = new[]
                    {
                        new
                        {
                            key = "workspace_id",
                            match = new
                            {
                                value = workspaceId
                            }
                        }
                    }
                }
            },
            JsonOptions,
            cancellationToken);

        await EnsureSuccessAsync(response, $"delete index state for '{workspaceRoot}' from Qdrant collection '{collectionName}'", cancellationToken);
        logger.LogInformation("Deleted index state for {WorkspaceRoot} from Qdrant collection {CollectionName}.", workspaceRoot, collectionName);
    }

    private async Task EnsureCollectionAsync(string collectionName, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync($"collections/{Uri.EscapeDataString(collectionName)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            await CreateCollectionAsync(collectionName, cancellationToken);
            return;
        }

        await EnsureSuccessAsync(response, $"read index state collection '{collectionName}'", cancellationToken);
    }

    private async Task CreateCollectionAsync(string collectionName, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PutAsJsonAsync(
            $"collections/{Uri.EscapeDataString(collectionName)}",
            new
            {
                vectors = new
                {
                    size = VectorSize,
                    distance = Distance
                }
            },
            JsonOptions,
            cancellationToken);

        await EnsureSuccessAsync(response, $"create index state collection '{collectionName}'", cancellationToken);
    }

    private async Task<bool> CollectionExistsAsync(string collectionName, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync($"collections/{Uri.EscapeDataString(collectionName)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, $"read index state collection '{collectionName}'", cancellationToken);
        return true;
    }

    private string GetStateCollectionName()
    {
        var prefix = QdrantCollectionNaming.SanitizeCollectionPart(_options.Qdrant.CollectionPrefix);
        return $"{(string.IsNullOrWhiteSpace(prefix) ? "ragnet" : prefix)}-index-state";
    }

    private async Task<WorkspaceIndexState> ReadPagedStateAsync(
        string collectionName,
        JsonElement manifestPayload,
        string fallbackWorkspaceRoot,
        CancellationToken cancellationToken)
    {
        var workspaceRoot = GetString(manifestPayload, "workspace_root");
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            workspaceRoot = fallbackWorkspaceRoot;
        }

        workspaceRoot = NormalizePath(workspaceRoot);
        var workspaceId = GetString(manifestPayload, "workspace_id");
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            workspaceId = QdrantCollectionNaming.GetWorkspaceId(workspaceRoot);
        }

        var pageCount = GetInt32(manifestPayload, "page_count");
        var files = new Dictionary<string, IndexedFileState>(StringComparer.OrdinalIgnoreCase);
        foreach (var pageIds in Enumerable.Range(0, pageCount)
            .Select(pageIndex => CreatePagePointId(workspaceId, pageIndex))
            .Chunk(PagePointsPerLoad))
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"collections/{Uri.EscapeDataString(collectionName)}/points",
                new
                {
                    ids = pageIds,
                    with_payload = true,
                    with_vector = false
                },
                JsonOptions,
                cancellationToken);

            await EnsureSuccessAsync(response, $"load index state pages for '{workspaceRoot}' from Qdrant collection '{collectionName}'", cancellationToken);

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var point in result.EnumerateArray())
            {
                if (!point.TryGetProperty("payload", out var pagePayload) ||
                    !string.Equals(GetString(pagePayload, "record_kind"), FilePageRecordKind, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var file in ReadFileStates(pagePayload))
                {
                    files[file.FilePath] = file;
                }
            }
        }

        return new WorkspaceIndexState(
            workspaceRoot,
            files,
            GetNullableString(manifestPayload, "embedding_model"),
            IndexSchemaVersions.ReadPayloadVersion(manifestPayload),
            GetNullableDateTimeOffset(manifestPayload, "saved_at_utc"),
            StateExists: true,
            IsComplete: GetNullableBool(manifestPayload, "is_complete") ?? true,
            IndexingCollectionName: GetNullableString(manifestPayload, "indexing_collection_name"));
    }

    private static IEnumerable<object> CreateFilePagePoints(
        string workspaceId,
        string workspaceRoot,
        WorkspaceIndexState state,
        IReadOnlyList<IndexedFileState> orderedFiles)
    {
        var pageIndex = 0;
        foreach (var page in orderedFiles.Chunk(FilesPerPage))
        {
            yield return new
            {
                id = CreatePagePointId(workspaceId, pageIndex),
                vector = new[] { 1f },
                payload = new
                {
                    record_kind = FilePageRecordKind,
                    state_format = PagedStateFormat,
                    workspace_root = workspaceRoot,
                    workspace_id = workspaceId,
                    embedding_model = state.EmbeddingModel,
                    schema_version = IndexSchemaVersions.Current,
                    saved_at_utc = state.SavedAtUtc,
                    page_index = pageIndex,
                    page_size = FilesPerPage,
                    files = page.Select(file => new
                    {
                        file_path = NormalizePath(file.FilePath),
                        fingerprint = file.Fingerprint,
                        size = file.Size,
                        last_write_time_utc = file.LastWriteTimeUtc,
                        chunk_count = file.ChunkCount
                    }).ToArray()
                }
            };

            pageIndex++;
        }
    }

    private static bool IsPagedManifest(JsonElement payload)
        => string.Equals(GetString(payload, "state_format"), PagedStateFormat, StringComparison.Ordinal) &&
            string.Equals(GetString(payload, "record_kind"), ManifestRecordKind, StringComparison.Ordinal);

    private static bool TryReadLegacyState(JsonElement payload, string fallbackWorkspaceRoot, out WorkspaceIndexState state)
    {
        var workspaceRoot = GetString(payload, "workspace_root");
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            workspaceRoot = fallbackWorkspaceRoot;
        }

        var files = ReadFileStates(payload)
            .ToDictionary(file => file.FilePath, file => file, StringComparer.OrdinalIgnoreCase);

        state = new WorkspaceIndexState(
            NormalizePath(workspaceRoot),
            files,
            GetNullableString(payload, "embedding_model"),
            IndexSchemaVersions.ReadPayloadVersion(payload),
            GetNullableDateTimeOffset(payload, "saved_at_utc"),
            StateExists: true,
            IsComplete: GetNullableBool(payload, "is_complete") ?? true,
            IndexingCollectionName: GetNullableString(payload, "indexing_collection_name"));
        return true;
    }

    private static IEnumerable<IndexedFileState> ReadFileStates(JsonElement payload)
    {
        if (!payload.TryGetProperty("files", out var fileElements) || fileElements.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var fileElement in fileElements.EnumerateArray())
        {
            var file = TryReadFileState(fileElement);
            if (file is not null)
            {
                yield return file;
            }
        }
    }

    private static IndexedFileState? TryReadFileState(JsonElement element)
    {
        var filePath = GetString(element, "file_path");
        var fingerprint = GetString(element, "fingerprint");
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(fingerprint))
        {
            return null;
        }

        return new IndexedFileState(
            NormalizePath(filePath),
            fingerprint,
            GetInt64(element, "size"),
            GetDateTimeOffset(element, "last_write_time_utc"),
            GetInt32(element, "chunk_count"));
    }

    private static WorkspaceIndexState Empty(string workspaceRoot)
        => new(
            NormalizePath(workspaceRoot),
            new Dictionary<string, IndexedFileState>(StringComparer.OrdinalIgnoreCase),
            EmbeddingModel: null,
            SchemaVersion: null,
            SavedAtUtc: null,
            StateExists: false);

    private static string CreatePointId(string workspaceId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"index-state:{workspaceId}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }

    private static string CreatePagePointId(string workspaceId, int pageIndex)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"index-state:{workspaceId}:page:{pageIndex}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string? GetNullableString(JsonElement element, string name)
    {
        var value = GetString(element, name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static long GetInt64(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result)
            ? result
            : 0L;

    private static int GetInt32(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result
            : 0;

    private static bool? GetNullableBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static DateTimeOffset GetDateTimeOffset(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetDateTimeOffset(out var result)
            ? result
            : DateTimeOffset.MinValue;

    private static DateTimeOffset? GetNullableDateTimeOffset(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetDateTimeOffset(out var result)
            ? result
            : null;

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
}
