using Microsoft.Extensions.Options;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Source.Interfaces;

namespace RagNet.Mcp.Source;

public sealed class ConfiguredSourceIdentityResolver(
    GitSourceIdentityResolver gitResolver,
    TfsSourceIdentityResolver tfsResolver,
    IOptions<RagNetOptions> options) : ISourceIdentityResolver
{
    private readonly RagNetOptions _options = options.Value;

    public async Task<SourceIdentity> ResolveAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default)
    {
        var provider = NormalizeProvider(_options.SourceControl.IdentityProvider);
        return provider switch
        {
            SourceControlProviders.Git => await gitResolver.ResolveAsync(workspaceRoot, filePath, cancellationToken),
            SourceControlProviders.Tfs => await tfsResolver.ResolveAsync(workspaceRoot, filePath, cancellationToken),
            SourceControlProviders.None => SourceIdentity.Local(workspaceRoot, filePath),
            _ => await ResolveAutoAsync(workspaceRoot, filePath, cancellationToken)
        };
    }

    private async Task<SourceIdentity> ResolveAutoAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken)
    {
        if (await gitResolver.CanResolveAsync(workspaceRoot, cancellationToken))
        {
            return await gitResolver.ResolveAsync(workspaceRoot, filePath, cancellationToken);
        }

        if (await tfsResolver.CanResolveAsync(workspaceRoot, cancellationToken))
        {
            return await tfsResolver.ResolveAsync(workspaceRoot, filePath, cancellationToken);
        }

        return SourceIdentity.Local(workspaceRoot, filePath);
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
