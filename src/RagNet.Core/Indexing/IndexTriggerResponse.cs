namespace RagNet.Mcp.Indexing;

public sealed record IndexTriggerResponse(
    Guid JobId,
    IndexTriggerStatus Status,
    string Message,
    DateTimeOffset AcceptedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    IndexTriggerRequest Request,
    IReadOnlyList<IndexWorkspaceResult> Results,
    IReadOnlyList<string> Warnings);
