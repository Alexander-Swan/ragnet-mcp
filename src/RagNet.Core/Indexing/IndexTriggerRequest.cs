namespace RagNet.Mcp.Indexing;

/// <summary>
/// Describes a hosted or administrative indexing trigger.
/// </summary>
public sealed record IndexTriggerRequest
{
    public string? Provider { get; init; }

    public string? EventType { get; init; }

    public string? RepositoryUrl { get; init; }

    public string? Branch { get; init; }

    public string? CommitSha { get; init; }

    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    public IReadOnlyList<string> DeletedFiles { get; init; } = [];

    public string? WorkspacePath { get; init; }

    public string? WorkspaceGroup { get; init; }

    public bool Force { get; init; }

    public string? IndexProfile { get; init; }
}
