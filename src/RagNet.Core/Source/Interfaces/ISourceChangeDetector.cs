namespace RagNet.Mcp.Source.Interfaces;

public interface ISourceChangeDetector
{
    Task<SourceChangeSet> DetectChangesAsync(
        string workspaceRoot,
        IReadOnlyList<string>? candidateFiles,
        IReadOnlyList<string> previouslyIndexedFiles,
        string? previousCommitSha = null,
        CancellationToken cancellationToken = default);
}
