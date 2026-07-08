namespace RagNet.Mcp.Indexing;

internal static class SearchResultPacker
{
    private const int MaxPreviewChars = 600;

    public static IReadOnlyList<SearchResult> Pack(IEnumerable<SearchResult> results, int limit)
    {
        var packed = new List<SearchResult>();
        var seen = new Dictionary<string, SearchResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in results.OrderByDescending(result => result.Score))
        {
            var key = $"{NormalizePath(result.FilePath)}|{result.ContentType}|{result.SymbolName}";
            if (seen.TryGetValue(key, out var existing) && RangesOverlapOrTouch(existing, result))
            {
                var merged = Merge(existing, result);
                seen[key] = merged;
                var index = packed.IndexOf(existing);
                if (index >= 0)
                {
                    packed[index] = merged;
                }

                continue;
            }

            var trimmed = TrimPreview(result);
            packed.Add(trimmed);
            seen[key] = trimmed;

            if (packed.Count >= Math.Max(1, limit))
            {
                break;
            }
        }

        return packed
            .OrderByDescending(result => result.Score)
            .Take(Math.Max(1, limit))
            .ToArray();
    }

    private static SearchResult Merge(SearchResult left, SearchResult right)
    {
        var preview = left.Preview.Contains(right.Preview, StringComparison.Ordinal)
            ? left.Preview
            : string.Join(Environment.NewLine, left.Preview, right.Preview);

        return TrimPreview(left with
        {
            StartLine = Math.Min(left.StartLine, right.StartLine),
            EndLine = Math.Max(left.EndLine, right.EndLine),
            Score = Math.Max(left.Score, right.Score),
            Preview = preview
        });
    }

    private static SearchResult TrimPreview(SearchResult result)
        => result.Preview.Length <= MaxPreviewChars
            ? result
            : result with { Preview = string.Concat(result.Preview.AsSpan(0, MaxPreviewChars), "...") };

    private static bool RangesOverlapOrTouch(SearchResult left, SearchResult right)
        => left.StartLine <= right.EndLine + 1 && right.StartLine <= left.EndLine + 1;

    private static string NormalizePath(string path)
        => Path.GetFullPath(path);
}
