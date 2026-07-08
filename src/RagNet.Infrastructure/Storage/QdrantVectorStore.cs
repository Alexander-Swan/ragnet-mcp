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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RagNetOptions _options = options.Value;
    private readonly HttpClient _httpClient = httpClient;

    public async Task UpsertAsync(string workspaceRoot, IReadOnlyList<CodeChunk> chunks, IReadOnlyList<float[]> embeddings, CancellationToken cancellationToken = default)
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
        var collectionName = QdrantCollectionNaming.GetCollectionName(_options.Qdrant.CollectionPrefix, workspaceRoot);
        await EnsureCollectionAsync(collectionName, vectorSize, cancellationToken);

        var points = chunks.Select((chunk, index) => new
        {
            id = CreatePointId(workspaceId, chunk.Id),
            vector = embeddings[index],
            payload = CreatePayload(workspaceId, workspaceRoot, chunk)
        }).ToArray();

        var response = await _httpClient.PutAsJsonAsync(
            $"collections/{Uri.EscapeDataString(collectionName)}/points?wait=true",
            new { points },
            JsonOptions,
            cancellationToken);

        await EnsureSuccessAsync(response, $"upsert points into Qdrant collection '{collectionName}'", cancellationToken);
        logger.LogInformation("Upserted {Count} chunks into Qdrant collection {CollectionName}.", chunks.Count, collectionName);
    }

    public async Task DeleteByFileAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default)
    {
        var collectionName = QdrantCollectionNaming.GetCollectionName(_options.Qdrant.CollectionPrefix, workspaceRoot);
        if (!await CollectionExistsAsync(collectionName, cancellationToken))
        {
            return;
        }

        var workspaceId = QdrantCollectionNaming.GetWorkspaceId(workspaceRoot);
        var normalizedFilePath = NormalizePath(filePath);
        var response = await _httpClient.PostAsJsonAsync(
            $"collections/{Uri.EscapeDataString(collectionName)}/points/delete?wait=true",
            new
            {
                filter = new
                {
                    must = new object[]
                    {
                        MatchKeyword("workspace_id", workspaceId),
                        MatchKeyword("file_path", normalizedFilePath)
                    }
                }
            },
            JsonOptions,
            cancellationToken);

        await EnsureSuccessAsync(response, $"delete file '{normalizedFilePath}' from Qdrant collection '{collectionName}'", cancellationToken);
        logger.LogInformation("Deleted indexed chunks for {FilePath} from Qdrant collection {CollectionName}.", normalizedFilePath, collectionName);
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

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string workspaceRoot, float[] embedding, string query, int limit, bool hybrid, CancellationToken cancellationToken = default)
    {
        if (embedding.Length == 0)
        {
            return [];
        }

        var collectionName = QdrantCollectionNaming.GetCollectionName(_options.Qdrant.CollectionPrefix, workspaceRoot);
        if (!await CollectionExistsAsync(collectionName, cancellationToken))
        {
            return [];
        }

        var candidateLimit = Math.Max(1, limit) * (hybrid ? HybridCandidateMultiplier : 1);
        var response = await _httpClient.PostAsJsonAsync(
            $"collections/{Uri.EscapeDataString(collectionName)}/points/query",
            new
            {
                query = embedding,
                limit = candidateLimit,
                with_payload = true,
                with_vector = false,
                filter = new
                {
                    must = new[]
                    {
                        MatchKeyword("workspace_id", QdrantCollectionNaming.GetWorkspaceId(workspaceRoot))
                    }
                }
            },
            JsonOptions,
            cancellationToken);

        await EnsureSuccessAsync(response, $"search Qdrant collection '{collectionName}'", cancellationToken);

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var tokens = query.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var points = document.RootElement.GetProperty("result").TryGetProperty("points", out var resultPoints)
            ? resultPoints
            : document.RootElement.GetProperty("result");

        return points
            .EnumerateArray()
            .Select(point => ToSearchResult(point, tokens, hybrid))
            .Where(result => result is not null)
            .Select(result => result!)
            .OrderByDescending(result => result.Score)
            .Take(Math.Max(1, limit))
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
            relative_path = chunk.Source?.RelativePath,
            source_workspace_id = chunk.Source?.WorkspaceId,
            source_project_id = chunk.Source?.ProjectId,
            is_git_repository = chunk.Source?.IsGitRepository ?? false,
            file_path = NormalizePath(chunk.FilePath),
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

        return new SearchResult(
            GetString(payload, "file_path"),
            GetString(payload, "symbol_name"),
            GetString(payload, "symbol_kind"),
            GetInt32(payload, "start_line"),
            GetInt32(payload, "end_line"),
            semanticScore + keywordScore,
            GetString(payload, "preview"));
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

    public static string GetWorkspaceId(string workspaceRoot)
    {
        var normalized = NormalizeWorkspaceRoot(workspaceRoot);
        var identity = OperatingSystem.IsWindows()
            ? normalized.ToUpperInvariant()
            : normalized;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash)[..WorkspaceIdHexLength].ToLowerInvariant();
    }

    public static string NormalizeWorkspaceRoot(string workspaceRoot)
    {
        var fullPath = Path.GetFullPath(workspaceRoot);
        var root = Path.GetPathRoot(fullPath);
        return fullPath.Length == root?.Length
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string SanitizeCollectionPart(string value)
    {
        var sanitized = UnsafeCollectionCharacters.Replace(value.Trim().ToLowerInvariant(), "-").Trim('-', '_', '.');
        return sanitized.Length <= 64 ? sanitized : sanitized[..64].Trim('-', '_', '.');
    }

    [GeneratedRegex("[^a-zA-Z0-9_.-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeCollectionCharactersRegex();
}
