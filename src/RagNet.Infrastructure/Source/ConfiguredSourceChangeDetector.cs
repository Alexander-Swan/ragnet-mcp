using Microsoft.Extensions.Options;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Source.Interfaces;

namespace RagNet.Mcp.Source;

public sealed class ConfiguredSourceChangeDetector(
    GitSourceChangeDetector gitDetector,
    TfsSourceChangeDetector tfsDetector,
    IOptions<RagNetOptions> options) : ISourceChangeDetector
{
    private readonly RagNetOptions _options = options.Value;

    public async Task<SourceChangeSet> DetectChangesAsync(
        string workspaceRoot,
        IReadOnlyList<string>? candidateFiles,
        IReadOnlyList<string> previouslyIndexedFiles,
        string? previousCommitSha = null,
        CancellationToken cancellationToken = default)
    {
        var provider = NormalizeProvider(_options.SourceControl.ChangeDetector);
        return provider switch
        {
            SourceControlProviders.Git => await gitDetector.DetectChangesAsync(workspaceRoot, candidateFiles, previouslyIndexedFiles, previousCommitSha, cancellationToken),
            SourceControlProviders.Tfs => await tfsDetector.DetectChangesAsync(workspaceRoot, candidateFiles, previouslyIndexedFiles, previousCommitSha, cancellationToken),
            SourceControlProviders.None => SourceChangeSet.Unavailable("none", "Source-control change detection is disabled."),
            _ => await DetectAutoAsync(workspaceRoot, candidateFiles, previouslyIndexedFiles, previousCommitSha, cancellationToken)
        };
    }

    private async Task<SourceChangeSet> DetectAutoAsync(
        string workspaceRoot,
        IReadOnlyList<string>? candidateFiles,
        IReadOnlyList<string> previouslyIndexedFiles,
        string? previousCommitSha,
        CancellationToken cancellationToken)
    {
        if (await gitDetector.CanDetectAsync(workspaceRoot, cancellationToken))
        {
            return await gitDetector.DetectChangesAsync(workspaceRoot, candidateFiles, previouslyIndexedFiles, previousCommitSha, cancellationToken);
        }

        if (await tfsDetector.CanDetectAsync(workspaceRoot, cancellationToken))
        {
            return await tfsDetector.DetectChangesAsync(workspaceRoot, candidateFiles, previouslyIndexedFiles, previousCommitSha, cancellationToken);
        }

        return SourceChangeSet.Unavailable("auto", "No supported source-control workspace was found.");
    }

    private static string NormalizeProvider(string? provider)
        => provider?.Trim().ToLowerInvariant() switch
        {
            SourceControlProviders.Git => SourceControlProviders.Git,
            "tfvc" => SourceControlProviders.Tfs,
            SourceControlProviders.Tfs => SourceControlProviders.Tfs,
            SourceControlProviders.None => SourceControlProviders.None,
            _ => SourceControlProviders.Auto
        };
}
