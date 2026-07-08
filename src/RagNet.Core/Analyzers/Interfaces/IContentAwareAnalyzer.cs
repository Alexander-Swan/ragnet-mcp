using RagNet.Mcp.Analyzers;

namespace RagNet.Mcp.Analyzers.Interfaces;

public interface IContentAwareAnalyzer : ICodeAnalyzer
{
    Task<AnalyzerMatch> MatchAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default);
}
