namespace RagNet.Mcp.Source.Interfaces;

public interface ISourceChangeDetector
{
    Task<SourceChangeSet> DetectChangesAsync(
        string workspaceRoot,
        IReadOnlyList<string> candidateFiles,
        IReadOnlyList<string> previouslyIndexedFiles,
        CancellationToken cancellationToken = default);
}
