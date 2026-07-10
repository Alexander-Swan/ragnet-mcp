using System.Diagnostics;
using System.Text;
using RagNet.Mcp.Source;
using RagNet.Mcp.Source.Interfaces;

namespace RagNet.Mcp.Source;

public sealed class GitSourceChangeDetector : ISourceChangeDetector
{
    private const int GitTimeoutMilliseconds = 10_000;

    public async Task<SourceChangeSet> DetectChangesAsync(
        string workspaceRoot,
        IReadOnlyList<string> candidateFiles,
        IReadOnlyList<string> previouslyIndexedFiles,
        CancellationToken cancellationToken = default)
    {
        var repositoryRoot = await TryGetRepositoryRootAsync(workspaceRoot, cancellationToken);
        if (repositoryRoot is null)
        {
            return SourceChangeSet.Unavailable("git", "No Git repository was found.");
        }

        var status = await RunGitAsync(repositoryRoot, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken);
        if (status.ExitCode != 0)
        {
            return SourceChangeSet.Unavailable("git", status.Error.Trim());
        }

        var candidateSet = candidateFiles
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previousSet = previouslyIndexedFiles
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in ParseStatus(status.Output, repositoryRoot))
        {
            if (!candidateSet.Contains(item.Path) && !previousSet.Contains(item.Path))
            {
                continue;
            }

            if (item.Deleted)
            {
                deleted.Add(item.Path);
            }
            else if (File.Exists(item.Path))
            {
                changed.Add(item.Path);
            }
        }

        return new SourceChangeSet(
            "git",
            IsAvailable: true,
            changed.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            deleted.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static async Task<string?> TryGetRepositoryRootAsync(string workspaceRoot, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(workspaceRoot, ["rev-parse", "--show-toplevel"], cancellationToken);
        if (result.ExitCode != 0)
        {
            return null;
        }

        var root = result.Output.Trim();
        return string.IsNullOrWhiteSpace(root) ? null : NormalizePath(root);
    }

    private static IEnumerable<GitStatusItem> ParseStatus(string output, string repositoryRoot)
    {
        var parts = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++)
        {
            var entry = parts[index];
            if (entry.Length < 4)
            {
                continue;
            }

            var status = entry[..2];
            var path = entry[3..];
            var deleted = status.Contains('D', StringComparison.Ordinal);
            var renamed = status.Contains('R', StringComparison.Ordinal) || status.Contains('C', StringComparison.Ordinal);
            if (renamed && index + 1 < parts.Length)
            {
                yield return new GitStatusItem(NormalizePath(Path.Combine(repositoryRoot, parts[++index])), Deleted: true);
            }

            yield return new GitStatusItem(NormalizePath(Path.Combine(repositoryRoot, path)), deleted);
        }
    }

    private static async Task<GitCommandResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(GitTimeoutMilliseconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new GitCommandResult(-1, string.Empty, ex.Message);
        }

        var output = process.StandardOutput.ReadToEndAsync(linked.Token);
        var error = process.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return new GitCommandResult(-1, string.Empty, "Git command timed out.");
        }

        return new GitCommandResult(process.ExitCode, await output, await error);
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return fullPath.Length == root?.Length
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private sealed record GitCommandResult(int ExitCode, string Output, string Error);

    private sealed record GitStatusItem(string Path, bool Deleted);
}
