using RagNet.Mcp.Indexing;
using RagNet.Mcp.Storage.Interfaces;

namespace RagNet.Mcp.Storage;

public sealed class InMemoryVectorStore : IVectorStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Entry>> _entriesByWorkspace = new(StringComparer.OrdinalIgnoreCase);

    public Task UpsertAsync(string workspaceRoot, IReadOnlyList<CodeChunk> chunks, IReadOnlyList<float[]> embeddings, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var entries = _entriesByWorkspace.GetValueOrDefault(workspaceRoot) ?? [];
            entries.RemoveAll(entry => chunks.Any(chunk => chunk.Id == entry.Chunk.Id));

            for (var index = 0; index < chunks.Count; index++)
            {
                entries.Add(new Entry(chunks[index], embeddings[index]));
            }

            _entriesByWorkspace[workspaceRoot] = entries;
        }

        return Task.CompletedTask;
    }

    public Task DeleteByFileAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default)
        => DeleteByFilesAsync(workspaceRoot, [filePath], cancellationToken);

    public Task DeleteByFilesAsync(string workspaceRoot, IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
    {
        if (filePaths.Count == 0)
        {
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            if (_entriesByWorkspace.TryGetValue(workspaceRoot, out var entries))
            {
                var files = filePaths.Select(NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                entries.RemoveAll(entry => files.Contains(NormalizePath(entry.Chunk.FilePath)));
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteWorkspaceAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _entriesByWorkspace.Remove(workspaceRoot);
        }

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
        List<Entry> entries;
        lock (_gate)
        {
            entries = _entriesByWorkspace.GetValueOrDefault(workspaceRoot)?.ToList() ?? [];
        }

        var normalizedContentType = NormalizeContentType(contentType);
        var normalizedIndexProfile = NormalizeIndexProfile(indexProfile);
        var tokens = query.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var results = entries
            .Where(entry => normalizedContentType is null ||
                string.Equals(entry.Chunk.ContentType, normalizedContentType, StringComparison.OrdinalIgnoreCase))
            .Where(entry => normalizedIndexProfile is null ||
                string.Equals(entry.Chunk.IndexProfile, normalizedIndexProfile, StringComparison.OrdinalIgnoreCase))
            .Select(entry =>
            {
                var semanticScore = CosineSimilarity(embedding, entry.Embedding);
                var keywordScore = hybrid ? KeywordScore(entry.Chunk.Content, tokens) : 0d;
                var score = semanticScore + keywordScore;
                return ToSearchResult(entry.Chunk, score);
            })
            .OrderByDescending(result => result.Score)
            .Take(Math.Max(1, limit))
            .ToArray();

        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }

    private static SearchResult ToSearchResult(CodeChunk chunk, double score)
    {
        var preview = chunk.Content.Length > 400
            ? string.Concat(chunk.Content.AsSpan(0, 400), "...")
            : chunk.Content;

        return new SearchResult(chunk.FilePath, chunk.SymbolName, chunk.SymbolKind, chunk.StartLine, chunk.EndLine, score, preview)
        {
            ContentType = chunk.ContentType,
            IndexProfile = chunk.IndexProfile,
            Language = chunk.Language
        };
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

    private static string NormalizePath(string path)
        => Path.GetFullPath(path);

    private static double KeywordScore(string text, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return 0;
        }

        var matches = tokens.Count(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
        return (double)matches / tokens.Count;
    }

    private static double CosineSimilarity(float[] left, float[] right)
    {
        if (left.Length == 0 || right.Length == 0 || left.Length != right.Length)
        {
            return 0;
        }

        double dot = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;

        for (var index = 0; index < left.Length; index++)
        {
            dot += left[index] * right[index];
            leftMagnitude += left[index] * left[index];
            rightMagnitude += right[index] * right[index];
        }

        var denominator = Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude);
        return denominator == 0 ? 0 : dot / denominator;
    }

    private sealed record Entry(CodeChunk Chunk, float[] Embedding);
}
