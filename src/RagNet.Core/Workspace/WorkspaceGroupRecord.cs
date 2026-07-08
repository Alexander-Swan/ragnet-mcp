namespace RagNet.Mcp.Workspace;

public sealed record WorkspaceGroupRecord(
    string Name,
    string Source,
    IReadOnlyList<string> Roots,
    IReadOnlyList<string> ExcludeDirectories,
    bool IsReadOnly);

public static class WorkspaceGroupSources
{
    public const string Configured = "configured";

    public const string Local = "local";

    public const string Shared = "shared";
}
