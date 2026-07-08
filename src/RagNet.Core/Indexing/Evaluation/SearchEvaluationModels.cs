namespace RagNet.Mcp.Indexing.Evaluation;

public sealed record SearchEvaluationRequest(
    string QueriesPath,
    int? Limit = null,
    bool? Hybrid = null,
    string? FilePath = null,
    string? Scope = null,
    string? WorkspaceRoot = null,
    string? WorkspaceGroup = null,
    string? ContentType = null,
    string? RetrievalMode = null,
    string? SearchProfile = null);

public sealed record SearchEvaluationSuite
{
    public int? Limit { get; init; }

    public bool? Hybrid { get; init; }

    public string? FilePath { get; init; }

    public string? Scope { get; init; }

    public string? WorkspaceRoot { get; init; }

    public string? WorkspaceGroup { get; init; }

    public string? ContentType { get; init; }

    public string? RetrievalMode { get; init; }

    public string? SearchProfile { get; init; }

    public IReadOnlyList<SearchEvaluationQuery> Queries { get; init; } = [];
}

public sealed record SearchEvaluationQuery
{
    public string? Name { get; init; }

    public required string Query { get; init; }

    public int? Limit { get; init; }

    public bool? Hybrid { get; init; }

    public string? FilePath { get; init; }

    public string? Scope { get; init; }

    public string? WorkspaceRoot { get; init; }

    public string? WorkspaceGroup { get; init; }

    public string? ContentType { get; init; }

    public string? RetrievalMode { get; init; }

    public string? SearchProfile { get; init; }

    public SearchEvaluationExpectedResult? Expected { get; init; }

    public IReadOnlyList<SearchEvaluationExpectedResult> ExpectedResults { get; init; } = [];
}

public sealed record SearchEvaluationExpectedResult
{
    public string? File { get; init; }

    public string? FileContains { get; init; }

    public string? Symbol { get; init; }

    public string? SymbolContains { get; init; }

    public string? ContentContains { get; init; }

    public string? ContentType { get; init; }

    public string? IndexProfile { get; init; }
}

public sealed record SearchEvaluationReport(
    SearchEvaluationMetrics Metrics,
    IReadOnlyList<SearchEvaluationQueryResult> Queries);

public sealed record SearchEvaluationMetrics(
    int TotalQueries,
    int PassedQueries,
    double PassRate,
    double HitRateAt1,
    double HitRateAt5,
    double MeanReciprocalRank,
    double AverageBestRank);

public sealed record SearchEvaluationQueryResult(
    string Name,
    string Query,
    bool Passed,
    int? BestRank,
    double ReciprocalRank,
    IReadOnlyList<SearchEvaluationExpectedResult> ExpectedResults,
    IReadOnlyList<SearchEvaluationSearchResult> Results);

public sealed record SearchEvaluationSearchResult(
    int Rank,
    string FilePath,
    string SymbolName,
    string SymbolKind,
    int StartLine,
    int EndLine,
    double Score,
    string Preview,
    string ContentType,
    string IndexProfile,
    string Language);
