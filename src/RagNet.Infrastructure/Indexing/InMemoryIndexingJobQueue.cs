using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RagNet.Mcp.Indexing.Interfaces;

namespace RagNet.Mcp.Indexing;

public sealed class InMemoryIndexingJobQueue(
    IWorkspaceIndexer indexer,
    ILogger<InMemoryIndexingJobQueue> logger) : IIndexingJobQueue
{
    private const int MaxRecentEvents = 100;
    private readonly ConcurrentQueue<IndexTriggerEvent> _recentEvents = new();

    public async Task<IndexTriggerResponse> EnqueueAsync(
        IndexTriggerRequest request,
        CancellationToken cancellationToken = default)
    {
        var jobId = Guid.NewGuid();
        var acceptedAtUtc = DateTimeOffset.UtcNow;
        var warnings = BuildWarnings(request).ToList();

        if (string.IsNullOrWhiteSpace(request.WorkspacePath) &&
            string.IsNullOrWhiteSpace(request.WorkspaceGroup))
        {
            if (!string.IsNullOrWhiteSpace(request.RepositoryUrl))
            {
                warnings.Add("Repository checkout is not implemented yet; this trigger was recorded for future hosted processing.");
                return Record(new IndexTriggerResponse(
                    jobId,
                    IndexTriggerStatus.Queued,
                    "Repository-only hosted indexing is queued as a placeholder until checkout support exists.",
                    acceptedAtUtc,
                    CompletedAtUtc: null,
                    request,
                    Results: [],
                    warnings));
            }

            return Record(new IndexTriggerResponse(
                jobId,
                IndexTriggerStatus.Rejected,
                "Provide workspacePath, workspaceGroup, or repositoryUrl.",
                acceptedAtUtc,
                DateTimeOffset.UtcNow,
                request,
                Results: [],
                warnings));
        }

        try
        {
            IReadOnlyList<IndexWorkspaceResult> results;
            if (!string.IsNullOrWhiteSpace(request.WorkspaceGroup))
            {
                results = await indexer.IndexGroupAsync(
                    request.WorkspaceGroup,
                    excludeDirectories: null,
                    request.Force,
                    cancellationToken);
            }
            else
            {
                var result = await indexer.IndexAsync(
                    request.WorkspacePath!,
                    excludeDirectories: null,
                    request.Force,
                    cancellationToken);
                results = [result];
            }

            return Record(new IndexTriggerResponse(
                jobId,
                IndexTriggerStatus.Completed,
                "Indexing completed for the local workspace target.",
                acceptedAtUtc,
                DateTimeOffset.UtcNow,
                request,
                results,
                warnings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Index trigger {JobId} failed.", jobId);
            warnings.Add(ex.Message);
            return Record(new IndexTriggerResponse(
                jobId,
                IndexTriggerStatus.Failed,
                "Indexing failed for the local workspace target.",
                acceptedAtUtc,
                DateTimeOffset.UtcNow,
                request,
                Results: [],
                warnings));
        }
    }

    public IReadOnlyList<IndexTriggerEvent> GetRecentEvents()
        => _recentEvents.ToArray();

    private static IEnumerable<string> BuildWarnings(IndexTriggerRequest request)
    {
        if (request.ChangedFiles.Count > 0 || request.DeletedFiles.Count > 0)
        {
            yield return "Changed and deleted file lists are recorded but not used for file-scoped indexing yet.";
        }
    }

    private IndexTriggerResponse Record(IndexTriggerResponse response)
    {
        _recentEvents.Enqueue(new IndexTriggerEvent(
            response.JobId,
            response.Status,
            response.Message,
            response.AcceptedAtUtc,
            response.CompletedAtUtc,
            response.Request,
            response.Results,
            response.Warnings));

        while (_recentEvents.Count > MaxRecentEvents && _recentEvents.TryDequeue(out _))
        {
        }

        return response;
    }
}
