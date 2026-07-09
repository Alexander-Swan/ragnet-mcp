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

public sealed class QdrantWorkspaceGroupRegistry(
    HttpClient httpClient,
    IOptions<RagNetOptions> options,
    ILogger<QdrantWorkspaceGroupRegistry> logger) : IWorkspaceGroupRegistry
{
    private const string Distance = "Cosine";
    private const int VectorSize = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RagNetOptions _options = options.Value;
    private readonly HttpClient _httpClient = httpClient;

    public async Task<IReadOnlyList<WorkspaceGroupRecord>> GetGroupsAsync(CancellationToken cancellationToken = default)
    {
        var groups = new Dictionary<string, WorkspaceGroupRecord>(StringComparer.OrdinalIgnoreCase);
        AddGroups(groups, await GetSharedGroupsAsync(cancellationToken));
        AddGroups(groups, GetLocalGroups());
        AddGroups(groups, GetConfiguredGroups());

        return groups.Values
            .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<WorkspaceGroupRecord?> GetGroupAsync(string name, CancellationToken cancellationToken = default)
        => (await GetGroupsAsync(cancellationToken))
            .FirstOrDefault(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase));

    public async Task<WorkspaceGroupRecord> SaveGroupAsync(
        string name,
        IReadOnlyList<string> roots,
        IReadOnlyList<string>? excludeDirectories = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeGroupName(name);
        EnsureConfiguredGroupCanNotBeMutated(normalizedName);

        var group = new WorkspaceGroupRecord(
            normalizedName,
            WorkspaceGroupSources.Shared,
            NormalizeDistinctPaths(roots),
            NormalizeDistinctNames(excludeDirectories ?? []),
            IsReadOnly: false);
        if (group.Roots.Count == 0)
        {
            throw new ArgumentException("At least one workspace root is required.", nameof(roots));
        }

        var collectionName = GetRegistryCollectionName();
        await EnsureCollectionAsync(collectionName, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var response = await _httpClient.PutAsJsonAsync(
            $"collections/{Uri.EscapeDataString(collectionName)}/points?wait=true",
            new
            {
                points = new[]
                {
                    new
                    {
                        id = CreatePointId(group.Name),
                        vector = new[] { 1f },
                        payload = new
                        {
                            group_name = group.Name,
                            schema_version = IndexSchemaVersions.Current,
                            source = WorkspaceGroupSources.Shared,
                            roots = group.Roots,
                            exclude_directories = group.ExcludeDirectories,
                            is_read_only = false,
                            updated_at_utc = now
                        }
                    }
                }
            },
            JsonOptions,
            cancellationToken);

        await EnsureSuccessAsync(response, $"upsert workspace group '{group.Name}'", cancellationToken);
        logger.LogInformation("Saved shared workspace group {WorkspaceGroup} in Qdrant registry {CollectionName}.", group.Name, collectionName);
        return group;
    }

    public async Task DeleteGroupAsync(string name, CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeGroupName(name);
        EnsureConfiguredGroupCanNotBeMutated(normalizedName);

        var collectionName = GetRegistryCollectionName();
        if (!await CollectionExistsAsync(collectionName, cancellationToken))
        {
            return;
        }

        var response = await _httpClient.PostAsJsonAsync(
            $"collections/{Uri.EscapeDataString(collectionName)}/points/delete?wait=true",
            new
            {
                points = new[] { CreatePointId(normalizedName) }
            },
            JsonOptions,
            cancellationToken);

        await EnsureSuccessAsync(response, $"delete workspace group '{normalizedName}'", cancellationToken);
        logger.LogInformation("Deleted shared workspace group {WorkspaceGroup} from Qdrant registry {CollectionName}.", normalizedName, collectionName);
    }

    private static void AddGroups(
        Dictionary<string, WorkspaceGroupRecord> groups,
        IEnumerable<WorkspaceGroupRecord> candidates)
    {
        foreach (var group in candidates)
        {
            if (!string.IsNullOrWhiteSpace(group.Name))
            {
                groups[group.Name] = group;
            }
        }
    }

    private IReadOnlyList<WorkspaceGroupRecord> GetConfiguredGroups()
        => _options.WorkspaceGroups
            .Where(group => !string.IsNullOrWhiteSpace(group.Name))
            .Select(group => new WorkspaceGroupRecord(
                group.Name.Trim(),
                WorkspaceGroupSources.Configured,
                NormalizeDistinctPaths(group.Roots),
                NormalizeDistinctNames(group.ExcludeDirectories),
                IsReadOnly: true))
            .ToArray();

    private static IReadOnlyList<WorkspaceGroupRecord> GetLocalGroups()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), ".ragnet", "indexer-workspace-groups.json");
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var stream = File.OpenRead(path);
            var store = JsonSerializer.Deserialize<LocalWorkspaceGroupStore>(stream, JsonOptions);
            return store?.Groups
                .Where(group => !string.IsNullOrWhiteSpace(group.Name))
                .Select(group => new WorkspaceGroupRecord(
                    group.Name.Trim(),
                    WorkspaceGroupSources.Local,
                    NormalizeDistinctPaths(group.Roots),
                    [],
                    IsReadOnly: false))
                .ToArray() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<WorkspaceGroupRecord>> GetSharedGroupsAsync(CancellationToken cancellationToken)
    {
        var collectionName = GetRegistryCollectionName();
        if (!await CollectionExistsAsync(collectionName, cancellationToken))
        {
            return [];
        }

        var groups = new List<WorkspaceGroupRecord>();
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
            await EnsureSuccessAsync(response, $"scroll workspace group registry '{collectionName}'", cancellationToken);

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var result = document.RootElement.GetProperty("result");
            if (result.TryGetProperty("points", out var points))
            {
                foreach (var point in points.EnumerateArray())
                {
                    if (point.TryGetProperty("payload", out var payload) && TryReadGroup(payload, out var group))
                    {
                        groups.Add(group);
                    }
                }
            }

            nextPageOffset = result.TryGetProperty("next_page_offset", out var offset) && offset.ValueKind == JsonValueKind.String
                ? offset.GetString()
                : null;
        }
        while (!string.IsNullOrWhiteSpace(nextPageOffset));

        return groups;
    }

    private async Task EnsureCollectionAsync(string collectionName, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync($"collections/{Uri.EscapeDataString(collectionName)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            await CreateCollectionAsync(collectionName, cancellationToken);
            return;
        }

        await EnsureSuccessAsync(response, $"read workspace group registry collection '{collectionName}'", cancellationToken);
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

        await EnsureSuccessAsync(response, $"create workspace group registry collection '{collectionName}'", cancellationToken);
    }

    private async Task<bool> CollectionExistsAsync(string collectionName, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync($"collections/{Uri.EscapeDataString(collectionName)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, $"read workspace group registry collection '{collectionName}'", cancellationToken);
        return true;
    }

    private void EnsureConfiguredGroupCanNotBeMutated(string name)
    {
        if (_options.WorkspaceGroups.Any(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Configured workspace group '{name}' is read-only.");
        }
    }

    private string GetRegistryCollectionName()
        => $"{SanitizeCollectionPart(_options.Qdrant.CollectionPrefix)}-workspace-groups";

    private static bool TryReadGroup(JsonElement payload, out WorkspaceGroupRecord group)
    {
        var name = GetString(payload, "group_name");
        if (string.IsNullOrWhiteSpace(name))
        {
            group = default!;
            return false;
        }

        IndexSchemaVersions.EnsureCompatible(
            IndexSchemaVersions.ReadPayloadVersion(payload),
            $"Qdrant workspace group record '{name}'");
        group = new WorkspaceGroupRecord(
            name.Trim(),
            WorkspaceGroupSources.Shared,
            NormalizeDistinctPaths(GetStringArray(payload, "roots")),
            NormalizeDistinctNames(GetStringArray(payload, "exclude_directories")),
            IsReadOnly: false);
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

    private static string NormalizeGroupName(string name)
        => string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Workspace group name is required.", nameof(name))
            : name.Trim();

    private static IReadOnlyList<string> NormalizeDistinctPaths(IEnumerable<string> paths)
        => paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> NormalizeDistinctNames(IEnumerable<string> values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string CreatePointId(string groupName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"workspace-group:{groupName.Trim().ToLowerInvariant()}"));
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

    private sealed record LocalWorkspaceGroupStore(IReadOnlyList<LocalWorkspaceGroup> Groups);

    private sealed record LocalWorkspaceGroup(string Name, IReadOnlyList<string> Roots);
}
