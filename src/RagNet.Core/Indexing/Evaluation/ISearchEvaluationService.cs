namespace RagNet.Mcp.Indexing.Evaluation;

public interface ISearchEvaluationService
{
    Task<SearchEvaluationReport> RunAsync(
        SearchEvaluationRequest request,
        CancellationToken cancellationToken = default);
}
