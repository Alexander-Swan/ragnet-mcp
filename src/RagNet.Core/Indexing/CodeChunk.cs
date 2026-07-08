namespace RagNet.Mcp.Indexing;

public sealed record CodeChunk(
    string Id,
    string WorkspaceRoot,
    string FilePath,
    string Language,
    string SymbolName,
    string SymbolKind,
    int StartLine,
    int EndLine,
    string Content);
