using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Indexing;
using RagNet.Mcp.Storage;
using RagNet.Mcp.Workspace.Interfaces;

namespace RagNet.Mcp.Workspace;

public sealed class QdrantIndexedWorkspaceRegistry(
    HttpClient httpClient,
    IOptions<RagNetOptions> options,
    ILogger<QdrantIndexedWorkspaceRegistry> logger) : IIndexedWorkspaceRegistry
{
    private const string Distance = "Cosine";
    private const int VectorSize = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RagNetOptions _options = options.Value;
    private readonly HttpClient _httpClient = httpClient;

    public async Task MarkIndexedAsync(IndexedWorkspaceRecord record, CancellationToken cancellationToken = default)
    {
        var collectionName = GetRegistryCollectionName();
        await EnsureCollectionAsync(collectionName, cancellationToken);

        var response = await _httpClient.PutAsJsonAsync(
            $"collections/{Uri.EscapeDataString(collectionName)}/points?wait=true",
            new
            {
                points = new[]
                {
                    new
                    {
                        id = CreatePointId(record.WorkspaceId),
                        vector = new[] { 1f },
                        payload = new
                        {
                            workspace_root = NormalizePath(record.WorkspaceRoot),
                            workspace_id = record.WorkspaceId,
                            schema_version = IndexSchemaVersions.Current,
                            collection_name = record.CollectionName,
                            groups = record.Groups,
                            indexed_targets = record.IndexedTargets.Select(NormalizePath).ToArray(),
                            repository_root = NormalizeNullablePath(record.RepositoryRoot),
                            repository_relative_workspace_root = record.RepositoryRelativeWorkspaceRoot,
                            remote_url = record.RemoteUrl,
                            branch = record.Branch,
                            commit_sha = record.CommitSha,
                            indexed_target_relative_paths = record.IndexedTargetRelativePaths ?? [],
                            last_indexed_utc = record.LastIndexedUtc,
                            files_scanned = record.FilesScanned,
                            chunks_indexed = record.ChunksIndexed,
                            full_reindex = record.FullReindex
                        }
                    }
                }
            },
            JsonOptions,
            cancellationToken);

        await EnsureSuccessAsync(response, $"upsert workspace registry record for '{record.WorkspaceRoot}'", cancellationToken);
        logger.LogInformation("Registered indexed workspace {WorkspaceRoot} in Qdrant registry {CollectionName}.", record.WorkspaceRoot, collectionName);
    }

    public async Task<IReadOnlyList<string>> GetIndexedWorkspaceRootsAsync(CancellationToken cancellationToken = default)
        => (await GetIndexedWorkspacesAsync(cancellationToken))
            .Select(record => record.WorkspaceRoot)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async Task<IReadOnlyList<IndexedWorkspaceRecord>> GetIndexedWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        var collectionName = GetRegistryCollectionName();
        if (!await CollectionExistsAsync(collectionName, cancellationToken))
        {
            return [];
        }

        var records = new List<IndexedWorkspaceRecord>();
        string? nextPageOffset = null;
        do
        {
            var body = new Dictionary<string, object?>
            {
                ["limit"] = 100,
                ["with_payload"] = true,
                ["with_vector"] = false
            };
            if (!string.IsNullOrWhiteSpace(nextPageOffset))
            {
                body["offset"] = nextPageOffset;
            }

            var response = await _httpClient.PostAsJsonAsync(
                $"collections/{Uri.EscapeDataString(collectionName)}/points/scroll",
                body,
                JsonOptions,
                cancellationToken);
            await EnsureSuccessAsync(response, $"scroll workspace registry '{collectionName}'", cancellationToken);

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var result = document.RootElement.GetProperty("result");
            if (result.TryGetProperty("points", out var points))
            {
                foreach (var point in points.EnumerateArray())
                {
                    if (point.TryGetProperty("payload", out var payload) && TryReadRecord(payload, out var record))
                    {
                        records.Add(record);
                    }
                }
            }

            nextPageOffset = result.TryGetProperty("next_page_offset", out var offset) && offset.ValueKind == JsonValueKind.String
                ? offset.GetString()
                : null;
        }
        while (!string.IsNullOrWhiteSpace(nextPageOffset));

        return records
            .OrderBy(record => record.WorkspaceRoot, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task DeleteWorkspaceAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        var collectionName = GetRegistryCollectionName();
        if (!await CollectionExistsAsync(collectionName, cancellationToken))
        {
            return;
        }

        var workspaceId = QdrantCollectionNaming.GetWorkspaceId(workspaceRoot);
        var response = await _httpClient.PostAsJsonAsync(
            $"collections/{Uri.EscapeDataString(collectionName)}/points/delete?wait=true",
            new
            {
                points = new[] { CreatePointId(workspaceId) }
            },
            JsonOptions,
            cancellationToken);

        await EnsureSuccessAsync(response, $"delete workspace registry record for '{workspaceRoot}'", cancellationToken);
        logger.LogInformation("Deleted indexed workspace {WorkspaceRoot} from Qdrant registry {CollectionName}.", workspaceRoot, collectionName);
    }

    private async Task EnsureCollectionAsync(string collectionName, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync($"collections/{Uri.EscapeDataString(collectionName)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            await CreateCollectionAsync(collectionName, cancellationToken);
            return;
        }

        await EnsureSuccessAsync(response, $"read workspace registry collection '{collectionName}'", cancellationToken);
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

        await EnsureSuccessAsync(response, $"create workspace registry collection '{collectionName}'", cancellationToken);
    }

    private async Task<bool> CollectionExistsAsync(string collectionName, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync($"collections/{Uri.EscapeDataString(collectionName)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, $"read workspace registry collection '{collectionName}'", cancellationToken);
        return true;
    }

    private string GetRegistryCollectionName()
        => $"{SanitizeCollectionPart(_options.Qdrant.CollectionPrefix)}-workspace-registry";

    private static bool TryReadRecord(JsonElement payload, out IndexedWorkspaceRecord record)
    {
        var workspaceRoot = GetString(payload, "workspace_root");
        var workspaceId = GetString(payload, "workspace_id");
        var collectionName = GetString(payload, "collection_name");
        if (string.IsNullOrWhiteSpace(workspaceRoot) ||
            string.IsNullOrWhiteSpace(workspaceId) ||
            string.IsNullOrWhiteSpace(collectionName))
        {
            record = default!;
            return false;
        }

        IndexSchemaVersions.EnsureCompatible(
            IndexSchemaVersions.ReadPayloadVersion(payload),
            $"Qdrant workspace registry record for '{workspaceRoot}'");
        record = new IndexedWorkspaceRecord(
            workspaceRoot,
            workspaceId,
            collectionName,
            GetStringArray(payload, "groups"),
            GetStringArray(payload, "indexed_targets"),
            GetDateTimeOffset(payload, "last_indexed_utc"),
            GetInt32(payload, "files_scanned"),
            GetInt32(payload, "chunks_indexed"),
            GetBool(payload, "full_reindex"),
            NullIfWhiteSpace(GetString(payload, "repository_root")),
            NullIfWhiteSpace(GetString(payload, "repository_relative_workspace_root")),
            NullIfWhiteSpace(GetString(payload, "remote_url")),
            NullIfWhiteSpace(GetString(payload, "branch")),
            NullIfWhiteSpace(GetString(payload, "commit_sha")),
            GetStringArray(payload, "indexed_target_relative_paths"));
        return true;
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray()
            : [];

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt32(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result
            : 0;

    private static bool GetBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset GetDateTimeOffset(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetDateTimeOffset(out var result)
            ? result
            : DateTimeOffset.MinValue;

    private static string? NullIfWhiteSpace(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? NormalizeNullablePath(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : NormalizePath(path);

    private static string CreatePointId(string workspaceId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"workspace-registry:{workspaceId}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return fullPath.Length == root?.Length
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string SanitizeCollectionPart(string value)
    {
        var sanitized = QdrantCollectionNaming.SanitizeCollectionPart(value);
        return string.IsNullOrWhiteSpace(sanitized) ? "ragnet" : sanitized;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"Failed to {action}. Qdrant returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    }
}
