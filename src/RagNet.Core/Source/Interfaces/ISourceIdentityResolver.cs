namespace RagNet.Mcp.Source.Interfaces;

public interface ISourceIdentityResolver
{
    Task<SourceIdentity> ResolveAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default);
}
