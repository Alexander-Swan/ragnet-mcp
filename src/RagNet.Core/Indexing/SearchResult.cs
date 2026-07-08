namespace RagNet.Mcp.Indexing;

public sealed record SearchResult(
    string FilePath,
    string SymbolName,
    string SymbolKind,
    int StartLine,
    int EndLine,
    double Score,
    string Preview);
