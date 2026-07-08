namespace RagNet.Mcp.Indexing;

public sealed record SearchResult(
    string FilePath,
    string SymbolName,
    string SymbolKind,
    int StartLine,
    int EndLine,
    double Score,
    string Preview)
{
    public string ContentType { get; init; } = IndexedContentTypes.Code;

    public string IndexProfile { get; init; } = IndexProfiles.Code;

    public string Language { get; init; } = string.Empty;
}
