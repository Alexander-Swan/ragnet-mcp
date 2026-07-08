using RagNet.Mcp.Indexing;
using RagNet.Mcp.Indexing.Evaluation;
using RagNet.Mcp.Indexing.Interfaces;

namespace RagNet.Mcp.Tests;

public sealed class SearchEvaluationServiceTests
{
    [Fact]
    public async Task RunAsync_CalculatesAggregateMetricsAndPerQueryRanks()
    {
        using var workspace = new TemporaryWorkspace();
        var suitePath = workspace.WriteFile(
            "eval.json",
            """
            {
              "limit": 5,
              "hybrid": true,
              "workspaceRoot": "D:\\Work\\Product",
              "queries": [
                {
                  "name": "find order service",
                  "query": "where are orders saved?",
                  "expected": {
                    "file": "src/Orders/OrderService.cs",
                    "symbol": "OrderService",
                    "contentContains": "SaveAsync"
                  }
                },
                {
                  "name": "missing",
                  "query": "where are invoices archived?",
                  "expected": {
                    "fileContains": "InvoiceArchive.cs"
                  }
                }
              ]
            }
            """);
        var indexer = new FakeWorkspaceIndexer
        {
            Results =
            {
                ["where are orders saved?"] =
                [
                    Result(@"D:\Work\Product\src\Other.cs", "Other", "nope", 0.7),
                    Result(@"D:\Work\Product\src\Orders\OrderService.cs", "OrderService", "public Task SaveAsync()", 0.6)
                ],
                ["where are invoices archived?"] =
                [
                    Result(@"D:\Work\Product\src\Orders\OrderService.cs", "OrderService", "public Task SaveAsync()", 0.6)
                ]
            }
        };
        var service = new SearchEvaluationService(indexer);

        var report = await service.RunAsync(new SearchEvaluationRequest(suitePath));

        Assert.Equal(2, report.Metrics.TotalQueries);
        Assert.Equal(1, report.Metrics.PassedQueries);
        Assert.Equal(0.5, report.Metrics.PassRate);
        Assert.Equal(0, report.Metrics.HitRateAt1);
        Assert.Equal(0.5, report.Metrics.HitRateAt5);
        Assert.Equal(0.25, report.Metrics.MeanReciprocalRank);
        Assert.Equal(2, report.Metrics.AverageBestRank);
        Assert.True(report.Queries[0].Passed);
        Assert.Equal(2, report.Queries[0].BestRank);
        Assert.False(report.Queries[1].Passed);
        Assert.Null(report.Queries[1].BestRank);
    }

    [Fact]
    public async Task RunAsync_CommandOptionsOverrideSuiteDefaultsAndQueryOptionsOverrideCommandOptions()
    {
        using var workspace = new TemporaryWorkspace();
        var suitePath = workspace.WriteFile(
            "eval.json",
            """
            {
              "limit": 3,
              "hybrid": false,
              "workspaceRoot": "suite-root",
              "searchProfile": "docs",
              "queries": [
                {
                  "query": "query one",
                  "limit": 7,
                  "hybrid": true,
                  "workspaceRoot": "query-root",
                  "searchProfile": "tests",
                  "expected": { "symbol": "Hit" }
                }
              ]
            }
            """);
        var indexer = new FakeWorkspaceIndexer
        {
            Results =
            {
                ["query one"] = [Result("query-root/File.cs", "Hit", "preview", 0.9)]
            }
        };
        var service = new SearchEvaluationService(indexer);

        await service.RunAsync(new SearchEvaluationRequest(
            suitePath,
            Limit: 5,
            Hybrid: false,
            WorkspaceRoot: "command-root",
            SearchProfile: "code"));

        var call = Assert.Single(indexer.Calls);
        Assert.Equal(7, call.Limit);
        Assert.True(call.Hybrid);
        Assert.Equal("query-root", call.WorkspaceRoot);
        Assert.Equal("tests", call.SearchProfile);
    }

    private static SearchResult Result(string filePath, string symbolName, string preview, double score)
        => new(filePath, symbolName, "Method", 1, 10, score, preview)
        {
            ContentType = IndexedContentTypes.Code,
            IndexProfile = IndexProfiles.Code,
            Language = "csharp"
        };

    private sealed class FakeWorkspaceIndexer : IWorkspaceIndexer
    {
        public Dictionary<string, IReadOnlyList<SearchResult>> Results { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<SearchCall> Calls { get; } = [];

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            string? filePath,
            string query,
            int limit,
            bool hybrid,
            string? scope,
            string? workspaceRoot,
            string? workspaceGroup,
            string? contentType = null,
            string? retrievalMode = null,
            string? searchProfile = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new SearchCall(limit, hybrid, workspaceRoot, searchProfile));
            return Task.FromResult(Results.GetValueOrDefault(query, []));
        }

        public Task<IndexWorkspaceResult> IndexAsync(string workspacePath, IReadOnlyList<string>? excludeDirectories = null, bool force = false, string? indexProfile = null, CancellationToken cancellationToken = default, IProgress<IndexingProgress>? progress = null)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<IndexWorkspaceResult>> IndexTargetsAsync(IReadOnlyList<string> workspacePaths, IReadOnlyList<string>? excludeDirectories = null, bool force = false, string? indexProfile = null, CancellationToken cancellationToken = default, IProgress<IndexingProgress>? progress = null)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<DryRunIndexWorkspaceResult>> DryRunIndexTargetsAsync(IReadOnlyList<string> workspacePaths, IReadOnlyList<string>? excludeDirectories = null, bool force = false, string? indexProfile = null, CancellationToken cancellationToken = default, IProgress<IndexingProgress>? progress = null)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<IndexWorkspaceResult>> IndexGroupAsync(string workspaceGroup, IReadOnlyList<string>? excludeDirectories = null, bool force = false, string? indexProfile = null, CancellationToken cancellationToken = default, IProgress<IndexingProgress>? progress = null)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<DryRunIndexWorkspaceResult>> DryRunIndexGroupAsync(string workspaceGroup, IReadOnlyList<string>? excludeDirectories = null, bool force = false, string? indexProfile = null, CancellationToken cancellationToken = default, IProgress<IndexingProgress>? progress = null)
            => throw new NotSupportedException();

        public Task<IndexStatusResult> GetStatusAsync(string workspacePath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string> GetCodeContextAsync(string filePath, int line, int before, int after, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string?> GetSymbolDetailsAsync(string filePath, string symbolName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed record SearchCall(int Limit, bool Hybrid, string? WorkspaceRoot, string? SearchProfile);
}
