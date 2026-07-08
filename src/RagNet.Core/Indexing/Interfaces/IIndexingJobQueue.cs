namespace RagNet.Mcp.Indexing.Interfaces;

public interface IIndexingJobQueue
{
    Task<IndexTriggerResponse> EnqueueAsync(
        IndexTriggerRequest request,
        CancellationToken cancellationToken = default);

    IReadOnlyList<IndexTriggerEvent> GetRecentEvents();
}
