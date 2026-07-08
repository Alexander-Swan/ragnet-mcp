using RagNet.Mcp.Workspace.Interfaces;

namespace RagNet.Mcp.Workspace;

public sealed class WorkspaceDetector : IWorkspaceDetector
{
    public Task<WorkspaceInfo> DetectAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A file path or workspace path is required.", nameof(filePath));
        }

        var fullPath = Path.GetFullPath(filePath);
        var directory = Directory.Exists(fullPath)
            ? new DirectoryInfo(fullPath)
            : new FileInfo(fullPath).Directory;

        if (directory is null)
        {
            throw new DirectoryNotFoundException($"Could not resolve a directory for '{filePath}'.");
        }

        var cursor = directory;
        string? nearestSolution = null;
        string? nearestProject = null;
        DirectoryInfo? gitRoot = null;

        while (cursor is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            nearestSolution ??= cursor.EnumerateFiles("*.sln").Select(file => file.FullName).FirstOrDefault();
            nearestProject ??= cursor.EnumerateFiles("*.csproj").Select(file => file.FullName).FirstOrDefault();

            if (Directory.Exists(Path.Combine(cursor.FullName, ".git")))
            {
                gitRoot = cursor;
                break;
            }

            if (nearestSolution is not null)
            {
                break;
            }

            cursor = cursor.Parent;
        }

        var root = gitRoot?.FullName
            ?? Path.GetDirectoryName(nearestSolution)
            ?? Path.GetDirectoryName(nearestProject)
            ?? directory.FullName;

        var rootName = new DirectoryInfo(root).Name;
        return Task.FromResult(new WorkspaceInfo(root, rootName, nearestSolution, nearestProject));
    }
}
