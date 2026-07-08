namespace RagNet.Mcp.Workspace;

public sealed record WorkspaceInfo(
    string RootPath,
    string Name,
    string? SolutionPath,
    string? ProjectPath);
