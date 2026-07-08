using RagNet.Mcp.Indexing;

namespace RagNet.Mcp.Analyzers.Interfaces;

public interface ICodeAnalyzer
{
    bool CanAnalyze(string filePath);

    Task<IReadOnlyList<CodeChunk>> AnalyzeAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default);
}

// Planned .NET language analyzers:
// - FSharpAnalyzer: *.fs and project metadata from *.fsproj
// - VisualBasicAnalyzer: *.vb and project metadata from *.vbproj
