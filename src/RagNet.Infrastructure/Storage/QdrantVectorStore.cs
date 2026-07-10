using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Indexing;
using RagNet.Mcp.Storage.Interfaces;

namespace RagNet.Mcp.Storage;

public sealed class QdrantVectorStore(
    HttpClient httpClient,
    IOptions<RagNetOptions> options,
    ILogger<QdrantVectorStore> logger) : IVectorStore
{
    private const string Distance = "Cosine";
    private const int HybridCandidateMultiplier = 5;
    private const int DeleteFileBatchSize = 64;
    private const int MaxUpsertBatchSize = 2_048;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RagNetOptions _options = options.Value;
    private readonly HttpClient _httpClient = httpClient;

    public async Task UpsertAsync(string workspaceRoot, IReadOnlyList<CodeChunk> chunks, IReadOnlyList<float[]> embeddings, CancellationToken cancellationToken = default)
        => await UpsertAsync(
            workspaceRoot,
            QdrantCollectionNaming.GetCollectionName(_options.Qdrant.CollectionPrefix, workspaceRoot),
            chunks,
            embeddings,
            cancellationToken);

    public async Task UpsertAsync(string workspaceRoot, string collectionName, IReadOnlyList<CodeChunk> chunks, IReadOnlyList<float[]> embeddings, CancellationToken cancellationToken = default)
    {
        if (chunks.Count != embeddings.Count)
        {
            throw new ArgumentException("Chunk and embedding counts must match.", nameof(embeddings));
        }

        if (chunks.Count == 0)
        {
            return;
        }

        var vectorSize = embeddings[0].Length;
        if (vectorSize == 0)
        {
            throw new InvalidOperationException("Qdrant upsert requires non-empty embeddings.");
        }

        if (embeddings.Any(embedding => embedding.Length != vectorSize))
        {
            throw new InvalidOperationException("Qdrant upsert requires all embeddings in a batch to have the same vector size.");
        }

        var workspaceId = QdrantCollectionNaming.GetWorkspaceId(workspaceRoot);
        await EnsureCollectionAsync(collectionName, vectorSize, cancellationToken);

        // TODO: Add a configurable Qdrant bulk-load mode that temporarily lowers
        // optimizer indexing_threshold for staging collections, then restores it
        // after the load. Keep this behind config because older Qdrant versions
        // and non-Qdrant test stores do not all support the same PATCH contract.
        foreach (var batchIndexes in Enumerable.Range(0, chunks.Count).Chunk(GetUpsertBatchSize()))
        {
            var points = batchIndexes.Select(index => new
            {
                id = CreatePointId(workspaceId, chunks[index].Id),
                vector = embeddings[index],
                payload = CreatePayload(workspaceId, workspaceRoot, chunks[index])
            }).ToArray();

            var response = await _httpClient.PutAsJsonAsync(
                $"collections/{Uri.EscapeDataString(collectionName)}/points?wait=true",
                new { points },
                JsonOptions,
                cancellationToken);

            await EnsureSuccessAsync(response, $"upsert {points.Length} points into Qdrant collection '{collectionName}'", cancellationToken);
        }

        logger.LogInformation("Upserted {Count} chunks into Qdrant collection {CollectionName}.", chunks.Count, collectionName);
    }

    private int GetUpsertBatchSize()
        => Math.Clamp(_options.Qdrant.UpsertBatchSize, 1, MaxUpsertBatchSize);

    public async Task DeleteByFileAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default)
        => await DeleteByFilesAsync(workspaceRoot, [filePath], cancellationToken);

    public async Task DeleteByFilesAsync(string workspaceRoot, IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
        => await DeleteByFilesAsync(
            workspaceRoot,
            QdrantCollectionNaming.GetCollectionName(_options.Qdrant.CollectionPrefix, workspaceRoot),
            filePaths,
            cancellationToken);

    public async Task DeleteByFilesAsync(string workspaceRoot, string collectionName, IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
    {
        if (filePaths.Count == 0)
        {
            return;
        }

        if (!await CollectionExistsAsync(collectionName, cancellationToken))
        {
            return;
        }

        var workspaceId = QdrantCollectionNaming.GetWorkspaceId(workspaceRoot);
        var normalizedFilePaths = filePaths
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var batch in normalizedFilePaths.Chunk(DeleteFileBatchSize))
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"collections/{Uri.EscapeDataString(collectionName)}/points/delete?wait=true",
                new
                {
                    filter = new
                    {
                        must = new object[]
                        {
                            MatchKeyword("workspace_id", workspaceId)
                        },
                        should = batch.Select(filePath => MatchKeyword("file_path", filePath)).ToArray()
                    }
                },
                JsonOptions,
                cancellationToken);

            await EnsureSuccessAsync(response, $"delete {batch.Length} files from Qdrant collection '{collectionName}'", cancellationToken);
        }

        logger.LogInformation(
            "Deleted indexed chunks for {FileCount} files from Qdrant collection {CollectionName}.",
            normalizedFilePaths.Length,
            collectionName);
    }

    public async Task DeleteWorkspaceAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        var collectionName = QdrantCollectionNaming.GetCollectionName(_options.Qdrant.CollectionPrefix, workspaceRoot);
        var response = await _httpClient.DeleteAsync($"collections/{Uri.EscapeDataString(collectionName)}?timeout=30", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, $"delete Qdrant collection '{collectionName}'", cancellationToken);
        logger.LogInformation("Deleted Qdrant collection {CollectionName}.", collectionName);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string workspaceRoot,
        float[] embedding,
        string query,
        int limit,
        bool hybrid,
        string? contentType = null,
        string? indexProfile = null,
        CancellationToken cancellationToken = default)
        => await SearchAsync(
            workspaceRoot,
            QdrantCollectionNaming.GetCollectionName(_options.Qdrant.CollectionPrefix, workspaceRoot),
            embedding,
            query,
            limit,
            hybrid,
            contentType,
            indexProfile,
            cancellationToken);

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
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
        if (embedding.Length == 0)
        {
            return [];
        }

        if (!await CollectionExistsAsync(collectionName, cancellationToken))
        {
            return [];
        }

        var candidateLimit = Math.Max(1, limit) * (hybrid ? HybridCandidateMultiplier : 1);
        var filter = CreateSearchFilter(workspaceRoot, contentType, indexProfile);
        var response = await _httpClient.PostAsJsonAsync(
            $"collections/{Uri.EscapeDataString(collectionName)}/points/query",
            new
            {
                query = embedding,
                limit = candidateLimit,
                with_payload = true,
                with_vector = false,
                filter
            },
            JsonOptions,
            cancellationToken);

        await EnsureSuccessAsync(response, $"search Qdrant collection '{collectionName}'", cancellationToken);

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var tokens = query.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var resultElement = document.RootElement.GetProperty("result");
        var points = resultElement.ValueKind == JsonValueKind.Object && resultElement.TryGetProperty("points", out var resultPoints)
            ? resultPoints
            : resultElement;
        if (points.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return points
            .EnumerateArray()
            .Select(point => ToSearchResult(point, tokens, hybrid))
            .Where(result => result is not null)
            .Select(result => result!)
            .OrderByDescending(result => result.Score)
            .Take(Math.Max(1, limit))
            .ToArray();
    }

    public async Task<IReadOnlyList<CodeChunk>> GetChunksByFileAsync(
        string workspaceRoot,
        string collectionName,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!await CollectionExistsAsync(collectionName, cancellationToken))
        {
            return [];
        }

        var chunks = new List<CodeChunk>();
        object? offset = null;
        do
        {
            var request = new Dictionary<string, object?>
            {
                ["limit"] = 256,
                ["with_payload"] = true,
                ["with_vector"] = false,
                ["filter"] = new
                {
                    must = new object[]
                    {
                        MatchKeyword("workspace_id", QdrantCollectionNaming.GetWorkspaceId(workspaceRoot)),
                        MatchKeyword("file_path", NormalizePath(filePath))
                    }
                }
            };
            if (offset is not null)
            {
                request["offset"] = offset;
            }

            var response = await _httpClient.PostAsJsonAsync(
                $"collections/{Uri.EscapeDataString(collectionName)}/points/scroll",
                request,
                JsonOptions,
                cancellationToken);

            await EnsureSuccessAsync(response, $"scroll Qdrant collection '{collectionName}' for file chunks", cancellationToken);

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("result", out var result))
            {
                return chunks;
            }

            if (result.TryGetProperty("points", out var points) && points.ValueKind == JsonValueKind.Array)
            {
                foreach (var point in points.EnumerateArray())
                {
                    var chunk = ToCodeChunk(point, workspaceRoot);
                    if (chunk is not null)
                    {
                        chunks.Add(chunk);
                    }
                }
            }

            offset = ReadNextPageOffset(result);
        }
        while (offset is not null);

        return chunks
            .OrderBy(chunk => chunk.StartLine)
            .ThenBy(chunk => chunk.EndLine)
            .ToArray();
    }

    private async Task EnsureCollectionAsync(string collectionName, int vectorSize, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync($"collections/{Uri.EscapeDataString(collectionName)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            await CreateCollectionAsync(collectionName, vectorSize, cancellationToken);
            return;
        }

        await EnsureSuccessAsync(response, $"read Qdrant collection '{collectionName}'", cancellationToken);
        var existingVectorSize = await ReadVectorSizeAsync(response, cancellationToken);
        if (existingVectorSize is not null && existingVectorSize != vectorSize)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Qdrant collection '{collectionName}' has vector size {existingVectorSize}, but the current embedding size is {vectorSize}. Force a full reindex or delete the collection before indexing with a different embedding model."));
        }
    }

    private async Task CreateCollectionAsync(string collectionName, int vectorSize, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PutAsJsonAsync(
            $"collections/{Uri.EscapeDataString(collectionName)}",
            new
            {
                vectors = new
                {
                    size = vectorSize,
                    distance = Distance
                }
            },
            JsonOptions,
            cancellationToken);

        await EnsureSuccessAsync(response, $"create Qdrant collection '{collectionName}'", cancellationToken);
        logger.LogInformation("Created Qdrant collection {CollectionName} with vector size {VectorSize}.", collectionName, vectorSize);
    }

    private async Task<bool> CollectionExistsAsync(string collectionName, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync($"collections/{Uri.EscapeDataString(collectionName)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, $"read Qdrant collection '{collectionName}'", cancellationToken);
        return true;
    }

    private static async Task<int?> ReadVectorSizeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("config", out var config) ||
            !config.TryGetProperty("params", out var parameters) ||
            !parameters.TryGetProperty("vectors", out var vectors))
        {
            return null;
        }

        if (vectors.TryGetProperty("size", out var unnamedSize))
        {
            return unnamedSize.GetInt32();
        }

        foreach (var namedVector in vectors.EnumerateObject())
        {
            if (namedVector.Value.TryGetProperty("size", out var namedSize))
            {
                return namedSize.GetInt32();
            }
        }

        return null;
    }

    private static object CreatePayload(string workspaceId, string workspaceRoot, CodeChunk chunk)
        => new
        {
            workspace_id = workspaceId,
            workspace_root = NormalizePath(workspaceRoot),
            repository_root = chunk.Source?.RepositoryRoot,
            remote_url = chunk.Source?.RemoteUrl,
            branch = chunk.Source?.Branch,
            commit_sha = chunk.Source?.CommitSha,
            schema_version = IndexSchemaVersions.Current,
            relative_path = chunk.Source?.RelativePath,
            source_workspace_id = chunk.Source?.WorkspaceId,
            source_project_id = chunk.Source?.ProjectId,
            is_git_repository = chunk.Source?.IsGitRepository ?? false,
            file_path = NormalizePath(chunk.FilePath),
            content_type = chunk.ContentType,
            index_profile = chunk.IndexProfile,
            language = chunk.Language,
            symbol_name = chunk.SymbolName,
            symbol_kind = chunk.SymbolKind,
            start_line = chunk.StartLine,
            end_line = chunk.EndLine,
            preview = CreatePreview(chunk.Content),
            content = chunk.Content
        };

    private static SearchResult? ToSearchResult(JsonElement point, IReadOnlyList<string> tokens, bool hybrid)
    {
        if (!point.TryGetProperty("payload", out var payload))
        {
            return null;
        }

        var semanticScore = point.TryGetProperty("score", out var score) ? score.GetDouble() : 0d;
        var content = GetString(payload, "content");
        var keywordScore = hybrid ? KeywordScore(content, tokens) : 0d;
        IndexSchemaVersions.EnsureCompatible(
            IndexSchemaVersions.ReadPayloadVersion(payload),
            $"Qdrant vector payload for '{GetString(payload, "file_path")}'");

        var indexProfile = GetString(payload, "index_profile");
        return new SearchResult(
            GetString(payload, "file_path"),
            GetString(payload, "symbol_name"),
            GetString(payload, "symbol_kind"),
            GetInt32(payload, "start_line"),
            GetInt32(payload, "end_line"),
            semanticScore + keywordScore,
            GetString(payload, "preview"))
        {
            ContentType = GetString(payload, "content_type"),
            IndexProfile = string.IsNullOrWhiteSpace(indexProfile) ? IndexProfiles.Code : indexProfile,
            Language = GetString(payload, "language")
        };
    }

    private static CodeChunk? ToCodeChunk(JsonElement point, string fallbackWorkspaceRoot)
    {
        if (!point.TryGetProperty("payload", out var payload))
        {
            return null;
        }

        IndexSchemaVersions.EnsureCompatible(
            IndexSchemaVersions.ReadPayloadVersion(payload),
            $"Qdrant vector payload for '{GetString(payload, "file_path")}'");

        var filePath = GetString(payload, "file_path");
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var workspaceRoot = GetString(payload, "workspace_root");
        var content = GetString(payload, "content");
        var contentType = GetString(payload, "content_type");
        var indexProfile = GetString(payload, "index_profile");
        return new CodeChunk(
            Id: GetString(point, "id"),
            WorkspaceRoot: string.IsNullOrWhiteSpace(workspaceRoot) ? fallbackWorkspaceRoot : workspaceRoot,
            FilePath: filePath,
            Language: GetString(payload, "language"),
            SymbolName: GetString(payload, "symbol_name"),
            SymbolKind: GetString(payload, "symbol_kind"),
            StartLine: GetInt32(payload, "start_line"),
            EndLine: GetInt32(payload, "end_line"),
            Content: content)
        {
            ContentType = string.IsNullOrWhiteSpace(contentType) ? IndexedContentTypes.Code : contentType,
            IndexProfile = string.IsNullOrWhiteSpace(indexProfile) ? IndexProfiles.Code : indexProfile
        };
    }

    private static object? ReadNextPageOffset(JsonElement result)
    {
        if (!result.TryGetProperty("next_page_offset", out var nextOffset) ||
            nextOffset.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return nextOffset.ValueKind switch
        {
            JsonValueKind.String => nextOffset.GetString(),
            JsonValueKind.Number when nextOffset.TryGetInt64(out var number) => number,
            _ => JsonSerializer.Deserialize<object>(nextOffset.GetRawText(), JsonOptions)
        };
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt32(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result
            : 0;

    private static object MatchKeyword(string key, string value)
        => new
        {
            key,
            match = new
            {
                value
            }
        };

    private static object CreateSearchFilter(string workspaceRoot, string? contentType, string? indexProfile)
    {
        var must = new List<object>
        {
            MatchKeyword("workspace_id", QdrantCollectionNaming.GetWorkspaceId(workspaceRoot))
        };

        var normalizedContentType = NormalizeContentType(contentType);
        if (normalizedContentType is not null)
        {
            must.Add(MatchKeyword("content_type", normalizedContentType));
        }

        var normalizedIndexProfile = NormalizeIndexProfile(indexProfile);
        if (normalizedIndexProfile is not null)
        {
            must.Add(MatchKeyword("index_profile", normalizedIndexProfile));
        }

        return new { must };
    }

    private static string? NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType) ||
            string.Equals(contentType, IndexedContentTypes.All, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return contentType.Trim();
    }

    private static string? NormalizeIndexProfile(string? indexProfile)
        => IndexProfiles.NormalizeFilter(indexProfile);

    private static string CreatePointId(string workspaceId, string chunkId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{workspaceId}:{chunkId}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }

    private static string CreatePreview(string content)
        => content.Length > 400
            ? string.Concat(content.AsSpan(0, 400), "...")
            : content;

    private static double KeywordScore(string text, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return 0;
        }

        var matches = tokens.Count(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
        return (double)matches / tokens.Count;
    }

    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim();
        if (IsWindowsFullyQualifiedPath(trimmed))
        {
            return trimmed.Replace('/', '\\').TrimEnd('\\');
        }

        var fullPath = Path.GetFullPath(trimmed);
        var root = Path.GetPathRoot(fullPath);
        return fullPath.Length == root?.Length
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsWindowsFullyQualifiedPath(string path)
        => path.Length >= 3 &&
            char.IsAsciiLetter(path[0]) &&
            path[1] == ':' &&
            (path[2] == '\\' || path[2] == '/');

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

internal static partial class QdrantCollectionNaming
{
    private const int WorkspaceIdHexLength = 32;
    private static readonly Regex UnsafeCollectionCharacters = UnsafeCollectionCharactersRegex();

    public static string GetCollectionName(string collectionPrefix, string workspaceRoot)
    {
        var prefix = SanitizeCollectionPart(collectionPrefix);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            prefix = "ragnet";
        }

        return $"{prefix}-{GetWorkspaceId(workspaceRoot)}";
    }

    public static string GetStagingCollectionName(string collectionPrefix, string workspaceRoot, DateTimeOffset createdAtUtc)
    {
        var prefix = SanitizeCollectionPart(collectionPrefix);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            prefix = "ragnet";
        }

        var name = $"{prefix}-{GetWorkspaceId(workspaceRoot)}-stage-{createdAtUtc:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        return name.Length <= 63 ? name : name[..63].Trim('-', '_', '.');
    }

    public static string GetWorkspaceId(string workspaceRoot)
    {
        var normalized = NormalizeWorkspaceRoot(workspaceRoot);
        var identity = OperatingSystem.IsWindows() || IsWindowsFullyQualifiedPath(normalized)
            ? normalized.ToUpperInvariant()
            : normalized;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash)[..WorkspaceIdHexLength].ToLowerInvariant();
    }

    public static string NormalizeWorkspaceRoot(string workspaceRoot)
    {
        var trimmed = workspaceRoot.Trim();
        if (IsWindowsFullyQualifiedPath(trimmed))
        {
            return trimmed.Replace('/', '\\').TrimEnd('\\');
        }

        var fullPath = Path.GetFullPath(trimmed);
        var root = Path.GetPathRoot(fullPath);
        return fullPath.Length == root?.Length
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string SanitizeCollectionPart(string value)
    {
        var sanitized = UnsafeCollectionCharacters.Replace(value.Trim().ToLowerInvariant(), "-").Trim('-', '_', '.');
        return sanitized.Length <= 64 ? sanitized : sanitized[..64].Trim('-', '_', '.');
    }

    [GeneratedRegex("[^a-zA-Z0-9_.-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeCollectionCharactersRegex();

    private static bool IsWindowsFullyQualifiedPath(string path)
        => path.Length >= 3 &&
            char.IsAsciiLetter(path[0]) &&
            path[1] == ':' &&
            (path[2] == '\\' || path[2] == '/');
}
