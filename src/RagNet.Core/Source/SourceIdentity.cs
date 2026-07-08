namespace RagNet.Mcp.Source;

public sealed record SourceIdentity(
    string WorkspaceRoot,
    string RepositoryRoot,
    string RelativePath,
    bool IsGitRepository,
    string? RemoteUrl = null,
    string? Branch = null,
    string? CommitSha = null,
    string? WorkspaceId = null,
    string? ProjectId = null)
{
    public static SourceIdentity Local(string workspaceRoot, string filePath, string? workspaceId = null, string? projectId = null)
    {
        var normalizedWorkspaceRoot = NormalizePath(workspaceRoot);
        var normalizedFilePath = NormalizePath(filePath);

        return new SourceIdentity(
            normalizedWorkspaceRoot,
            normalizedWorkspaceRoot,
            NormalizeRelativePath(Path.GetRelativePath(normalizedWorkspaceRoot, normalizedFilePath)),
            IsGitRepository: false,
            WorkspaceId: workspaceId,
            ProjectId: projectId);
    }

    public SourceIdentity ForFile(string filePath)
    {
        var normalizedFilePath = NormalizePath(filePath);
        return this with
        {
            RelativePath = NormalizeRelativePath(Path.GetRelativePath(RepositoryRoot, normalizedFilePath))
        };
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return fullPath.Length == root?.Length
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string NormalizeRelativePath(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
}
