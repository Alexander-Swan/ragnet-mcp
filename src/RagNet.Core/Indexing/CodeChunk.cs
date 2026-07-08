namespace RagNet.Mcp.Indexing;

using RagNet.Mcp.Source;

public sealed record CodeChunk(
    string Id,
    string WorkspaceRoot,
    string FilePath,
    string Language,
    string SymbolName,
    string SymbolKind,
    int StartLine,
    int EndLine,
    string Content)
{
    public SourceIdentity? Source { get; init; }

    public string ContentType { get; init; } = IndexedContentTypes.Code;

    public string IndexProfile { get; init; } = IndexProfiles.Code;

    public string? FullyQualifiedSymbolName { get; init; }

    public string? Namespace { get; init; }

    public string? TypeContext { get; init; }

    public string? BaseTypes { get; init; }
}
