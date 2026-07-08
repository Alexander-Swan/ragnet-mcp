using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RagNet.Mcp.Source.Interfaces;

namespace RagNet.Mcp.Source;

public sealed class GitSourceIdentityResolver(ILogger<GitSourceIdentityResolver> logger) : ISourceIdentityResolver
{
    private const int GitTimeoutMilliseconds = 5_000;

    public async Task<SourceIdentity> ResolveAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default)
    {
        var normalizedWorkspaceRoot = NormalizePath(workspaceRoot);
        var normalizedFilePath = NormalizePath(filePath);

        var discoveredGitRoot = FindGitMetadataRoot(normalizedWorkspaceRoot);
        if (discoveredGitRoot is null)
        {
            return SourceIdentity.Local(normalizedWorkspaceRoot, normalizedFilePath);
        }

        var repositoryRoot = await RunGitAsync(normalizedWorkspaceRoot, "rev-parse --show-toplevel", cancellationToken);
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return SourceIdentity.Local(normalizedWorkspaceRoot, normalizedFilePath);
        }

        repositoryRoot = NormalizePath(repositoryRoot);
        var remoteUrl = NullIfBlank(await RunGitAsync(repositoryRoot, "config --get remote.origin.url", cancellationToken));
        var branch = NullIfBlank(await RunGitAsync(repositoryRoot, "rev-parse --abbrev-ref HEAD", cancellationToken));
        if (string.Equals(branch, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            branch = null;
        }

        var commitSha = NullIfBlank(await RunGitAsync(repositoryRoot, "rev-parse HEAD", cancellationToken));

        return new SourceIdentity(
            normalizedWorkspaceRoot,
            repositoryRoot,
            NormalizeRelativePath(Path.GetRelativePath(repositoryRoot, normalizedFilePath)),
            IsGitRepository: true,
            RemoteUrl: remoteUrl,
            Branch: branch,
            CommitSha: commitSha);
    }

    private async Task<string?> RunGitAsync(string workingDirectory, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"-C \"{workingDirectory}\" {arguments}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (!process.Start())
            {
                return null;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var exitedTask = process.WaitForExitAsync(cancellationToken);

            var completed = await Task.WhenAny(exitedTask, Task.Delay(GitTimeoutMilliseconds, cancellationToken));
            if (completed != exitedTask)
            {
                TryKill(process);
                logger.LogDebug("Timed out while running git {Arguments} in {WorkingDirectory}.", arguments, workingDirectory);
                return null;
            }

            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                logger.LogDebug("Git {Arguments} failed in {WorkingDirectory}: {Error}", arguments, workingDirectory, error.Trim());
                return null;
            }

            return output.Trim();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            logger.LogDebug(ex, "Git metadata detection is unavailable.");
            return null;
        }
    }

    private static string? FindGitMetadataRoot(string startPath)
    {
        var directory = Directory.Exists(startPath)
            ? new DirectoryInfo(startPath)
            : new FileInfo(startPath).Directory;

        while (directory is not null)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
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

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
