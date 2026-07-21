using Microsoft.Extensions.Logging;
using RagNet.Mcp.Source.Interfaces;

namespace RagNet.Mcp.Source;

public sealed class TfsSourceIdentityResolver(
    ITfsCommandRunner commandRunner,
    ILogger<TfsSourceIdentityResolver> logger) : ISourceIdentityResolver
{
    public async Task<bool> CanResolveAsync(string workspaceRoot, CancellationToken cancellationToken = default)
        => await TfsWorkspaceDiscovery.DiscoverAsync(workspaceRoot, commandRunner, cancellationToken) is not null;

    public async Task<SourceIdentity> ResolveAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default)
    {
        var normalizedWorkspaceRoot = TfsWorkspaceDiscovery.NormalizePath(workspaceRoot);
        var normalizedFilePath = TfsWorkspaceDiscovery.NormalizePath(filePath);
        var workspace = await TfsWorkspaceDiscovery.DiscoverAsync(normalizedWorkspaceRoot, commandRunner, cancellationToken);
        if (workspace is null)
        {
            return SourceIdentity.Local(normalizedWorkspaceRoot, normalizedFilePath);
        }

        var changeset = await TfsWorkspaceDiscovery.GetLatestChangesetAsync(workspace, commandRunner, cancellationToken);
        logger.LogDebug(
            "Resolved TFVC workspace {WorkspaceName} at {LocalPath} with changeset {Changeset}.",
            workspace.WorkspaceName,
            workspace.LocalPath,
            changeset);

        return new SourceIdentity(
            normalizedWorkspaceRoot,
            workspace.LocalPath,
            NormalizeRelativePath(Path.GetRelativePath(workspace.LocalPath, normalizedFilePath)),
            IsGitRepository: false,
            RemoteUrl: string.IsNullOrWhiteSpace(workspace.CollectionUrl) ? workspace.ServerPath : workspace.CollectionUrl,
            Branch: workspace.ServerPath,
            CommitSha: changeset is null ? null : $"tfvc:{changeset.Value}",
            WorkspaceId: workspace.WorkspaceName);
    }

    private static string NormalizeRelativePath(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
}
