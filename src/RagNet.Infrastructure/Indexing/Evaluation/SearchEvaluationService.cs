using System.Text.Json;
using RagNet.Mcp.Indexing.Evaluation;
using RagNet.Mcp.Indexing.Interfaces;

namespace RagNet.Mcp.Indexing.Evaluation;

public sealed class SearchEvaluationService(IWorkspaceIndexer indexer) : ISearchEvaluationService
{
    private const int DefaultLimit = 10;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public async Task<SearchEvaluationReport> RunAsync(
        SearchEvaluationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.QueriesPath))
        {
            throw new ArgumentException("A query suite path is required.", nameof(request));
        }

        var suite = await LoadSuiteAsync(request.QueriesPath, cancellationToken);
        if (suite.Queries.Count == 0)
        {
            throw new ArgumentException("Evaluation suite must contain at least one query.", nameof(request));
        }

        var queryResults = new List<SearchEvaluationQueryResult>(suite.Queries.Count);
        for (var index = 0; index < suite.Queries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            queryResults.Add(await EvaluateQueryAsync(request, suite, suite.Queries[index], index, cancellationToken));
        }

        return new SearchEvaluationReport(CalculateMetrics(queryResults), queryResults);
    }

    private static async Task<SearchEvaluationSuite> LoadSuiteAsync(string queriesPath, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(queriesPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Evaluation query suite '{fullPath}' does not exist.", fullPath);
        }

        await using var stream = File.OpenRead(fullPath);
        return await JsonSerializer.DeserializeAsync<SearchEvaluationSuite>(stream, JsonOptions, cancellationToken)
            ?? throw new ArgumentException($"Evaluation query suite '{fullPath}' is empty.");
    }

    private async Task<SearchEvaluationQueryResult> EvaluateQueryAsync(
        SearchEvaluationRequest request,
        SearchEvaluationSuite suite,
        SearchEvaluationQuery query,
        int queryIndex,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
        {
            throw new ArgumentException($"Evaluation query at index {queryIndex} must include a non-empty query.");
        }

        var limit = Math.Clamp(query.Limit ?? request.Limit ?? suite.Limit ?? DefaultLimit, 1, 50);
        var results = await indexer.SearchAsync(
            query.FilePath ?? request.FilePath ?? suite.FilePath,
            query.Query,
            limit,
            query.Hybrid ?? request.Hybrid ?? suite.Hybrid ?? true,
            query.Scope ?? request.Scope ?? suite.Scope,
            query.WorkspaceRoot ?? request.WorkspaceRoot ?? suite.WorkspaceRoot,
            query.WorkspaceGroup ?? request.WorkspaceGroup ?? suite.WorkspaceGroup,
            query.ContentType ?? request.ContentType ?? suite.ContentType,
            query.RetrievalMode ?? request.RetrievalMode ?? suite.RetrievalMode,
            query.SearchProfile ?? request.SearchProfile ?? suite.SearchProfile,
            cancellationToken);
        var expectedResults = GetExpectedResults(query);
        var bestRank = FindBestRank(results, expectedResults);

        return new SearchEvaluationQueryResult(
            string.IsNullOrWhiteSpace(query.Name) ? $"query-{queryIndex + 1}" : query.Name,
            query.Query,
            bestRank.HasValue,
            bestRank,
            bestRank.HasValue ? 1d / bestRank.Value : 0d,
            expectedResults,
            results.Select((result, index) => ToEvaluationResult(result, index + 1)).ToArray());
    }

    private static IReadOnlyList<SearchEvaluationExpectedResult> GetExpectedResults(SearchEvaluationQuery query)
    {
        if (query.ExpectedResults.Count > 0)
        {
            return query.ExpectedResults;
        }

        return query.Expected is null ? [] : [query.Expected];
    }

    private static int? FindBestRank(
        IReadOnlyList<SearchResult> results,
        IReadOnlyList<SearchEvaluationExpectedResult> expectedResults)
    {
        if (expectedResults.Count == 0)
        {
            return results.Count > 0 ? 1 : null;
        }

        for (var index = 0; index < results.Count; index++)
        {
            if (expectedResults.Any(expected => Matches(results[index], expected)))
            {
                return index + 1;
            }
        }

        return null;
    }

    private static bool Matches(SearchResult result, SearchEvaluationExpectedResult expected)
        => MatchesPath(result.FilePath, expected.File) &&
            Contains(result.FilePath, expected.FileContains) &&
            EqualsIfSet(result.SymbolName, expected.Symbol) &&
            Contains(result.SymbolName, expected.SymbolContains) &&
            Contains(result.Preview, expected.ContentContains) &&
            EqualsIfSet(result.ContentType, expected.ContentType) &&
            EqualsIfSet(result.IndexProfile, expected.IndexProfile);

    private static bool MatchesPath(string actualPath, string? expectedPath)
    {
        if (string.IsNullOrWhiteSpace(expectedPath))
        {
            return true;
        }

        var normalizedActual = NormalizePathForMatch(actualPath);
        var normalizedExpected = NormalizePathForMatch(expectedPath);
        return string.Equals(normalizedActual, normalizedExpected, StringComparison.OrdinalIgnoreCase) ||
            normalizedActual.EndsWith("/" + normalizedExpected, StringComparison.OrdinalIgnoreCase) ||
            normalizedActual.Contains(normalizedExpected, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePathForMatch(string path)
        => path.Replace('\\', '/').Trim('/');

    private static bool EqualsIfSet(string actual, string? expected)
        => string.IsNullOrWhiteSpace(expected) ||
            string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string actual, string? expected)
        => string.IsNullOrWhiteSpace(expected) ||
            actual.Contains(expected, StringComparison.OrdinalIgnoreCase);

    private static SearchEvaluationSearchResult ToEvaluationResult(SearchResult result, int rank)
        => new(
            rank,
            result.FilePath,
            result.SymbolName,
            result.SymbolKind,
            result.StartLine,
            result.EndLine,
            result.Score,
            result.Preview,
            result.ContentType,
            result.IndexProfile,
            result.Language);

    private static SearchEvaluationMetrics CalculateMetrics(IReadOnlyList<SearchEvaluationQueryResult> results)
    {
        if (results.Count == 0)
        {
            return new SearchEvaluationMetrics(0, 0, 0, 0, 0, 0, 0);
        }

        var passed = results.Count(result => result.Passed);
        var ranked = results.Where(result => result.BestRank.HasValue).ToArray();
        return new SearchEvaluationMetrics(
            results.Count,
            passed,
            passed / (double)results.Count,
            results.Count(result => result.BestRank is <= 1) / (double)results.Count,
            results.Count(result => result.BestRank is <= 5) / (double)results.Count,
            results.Sum(result => result.ReciprocalRank) / results.Count,
            ranked.Length == 0 ? 0 : ranked.Average(result => result.BestRank!.Value));
    }
}
