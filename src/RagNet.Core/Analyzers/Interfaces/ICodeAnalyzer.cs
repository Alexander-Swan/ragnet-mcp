using RagNet.Mcp.Indexing;

namespace RagNet.Mcp.Analyzers.Interfaces;

public interface ICodeAnalyzer
{
    bool CanAnalyze(string filePath);

    Task<IReadOnlyList<CodeChunk>> AnalyzeAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default);
}

// Planned syntax-aware analyzers:
// - JavaScript/TypeScript analyzers for imports, exports, functions, classes, components, routes, and tests.
