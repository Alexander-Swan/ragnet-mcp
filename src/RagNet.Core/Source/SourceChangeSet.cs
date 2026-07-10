namespace RagNet.Mcp.Source;

public sealed record SourceChangeSet(
    string Provider,
    bool IsAvailable,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> DeletedFiles,
    string? Message = null)
{
    public bool IsComplete { get; init; }

    public static SourceChangeSet Unavailable(string provider, string? message = null)
        => new(provider, IsAvailable: false, [], [], message);
}
