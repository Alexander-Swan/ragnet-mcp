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
        IReadOnlyList<string>? candidateFiles,
        IReadOnlyList<string> previouslyIndexedFiles,
        string? previousCommitSha = null,
        CancellationToken cancellationToken = default)
    {
        var repositoryRoot = await TryGetRepositoryRootAsync(workspaceRoot, cancellationToken);
        if (repositoryRoot is null)
        {
            return SourceChangeSet.Unavailable("git", "No Git repository was found.");
        }

        var currentCommitSha = await TryGetCurrentCommitShaAsync(repositoryRoot, cancellationToken);
        if (!string.IsNullOrWhiteSpace(previousCommitSha) &&
            !string.IsNullOrWhiteSpace(currentCommitSha) &&
            !string.Equals(previousCommitSha, currentCommitSha, StringComparison.OrdinalIgnoreCase))
        {
            var diff = await RunGitAsync(repositoryRoot, ["diff", "--name-status", "-z", previousCommitSha, currentCommitSha], cancellationToken);
            if (diff.ExitCode != 0)
            {
                return SourceChangeSet.Unavailable("git", diff.Error.Trim());
            }

            return await DetectFromDiffAndStatusAsync(
                repositoryRoot,
                diff.Output,
                candidateFiles,
                previouslyIndexedFiles,
                cancellationToken);
        }

        var status = await RunGitAsync(repositoryRoot, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken);
        if (status.ExitCode != 0)
        {
            return SourceChangeSet.Unavailable("git", status.Error.Trim());
        }

        var hasCandidateFilter = candidateFiles is { Count: > 0 };
        var candidateSet = hasCandidateFilter
            ? candidateFiles!
                .Select(NormalizePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var previousSet = previouslyIndexedFiles
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in ParseStatus(status.Output, repositoryRoot))
        {
            if (hasCandidateFilter && !candidateSet.Contains(item.Path) && !previousSet.Contains(item.Path))
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
            deleted.Order(StringComparer.OrdinalIgnoreCase).ToArray())
        {
            IsComplete = true
        };
    }

    private static async Task<SourceChangeSet> DetectFromDiffAndStatusAsync(
        string repositoryRoot,
        string diffOutput,
        IReadOnlyList<string>? candidateFiles,
        IReadOnlyList<string> previouslyIndexedFiles,
        CancellationToken cancellationToken)
    {
        var status = await RunGitAsync(repositoryRoot, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken);
        if (status.ExitCode != 0)
        {
            return SourceChangeSet.Unavailable("git", status.Error.Trim());
        }

        var hasCandidateFilter = candidateFiles is { Count: > 0 };
        var candidateSet = hasCandidateFilter
            ? candidateFiles!
                .Select(NormalizePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var previousSet = previouslyIndexedFiles
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in ParseNameStatus(diffOutput, repositoryRoot))
        {
            AddChange(item, hasCandidateFilter, candidateSet, previousSet, changed, deleted);
        }

        foreach (var item in ParseStatus(status.Output, repositoryRoot))
        {
            AddChange(item, hasCandidateFilter, candidateSet, previousSet, changed, deleted);
        }

        return new SourceChangeSet(
            "git",
            IsAvailable: true,
            changed.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            deleted.Order(StringComparer.OrdinalIgnoreCase).ToArray())
        {
            IsComplete = true
        };
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

    private static async Task<string?> TryGetCurrentCommitShaAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(repositoryRoot, ["rev-parse", "HEAD"], cancellationToken);
        if (result.ExitCode != 0)
        {
            return null;
        }

        var commitSha = result.Output.Trim();
        return string.IsNullOrWhiteSpace(commitSha) ? null : commitSha;
    }

    private static void AddChange(
        GitStatusItem item,
        bool hasCandidateFilter,
        HashSet<string> candidateSet,
        HashSet<string> previousSet,
        HashSet<string> changed,
        HashSet<string> deleted)
    {
        if (hasCandidateFilter && !candidateSet.Contains(item.Path) && !previousSet.Contains(item.Path))
        {
            return;
        }

        if (item.Deleted)
        {
            deleted.Add(item.Path);
            changed.Remove(item.Path);
            return;
        }

        if (File.Exists(item.Path))
        {
            changed.Add(item.Path);
            deleted.Remove(item.Path);
        }
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

    private static IEnumerable<GitStatusItem> ParseNameStatus(string output, string repositoryRoot)
    {
        var parts = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++)
        {
            var status = parts[index];
            if (string.IsNullOrWhiteSpace(status) || index + 1 >= parts.Length)
            {
                continue;
            }

            var deleted = status.StartsWith('D');
            var renamed = status.StartsWith('R') || status.StartsWith('C');
            if (renamed)
            {
                var oldPath = parts[++index];
                if (index + 1 >= parts.Length)
                {
                    yield return new GitStatusItem(NormalizePath(Path.Combine(repositoryRoot, oldPath)), Deleted: true);
                    continue;
                }

                var newPath = parts[++index];
                yield return new GitStatusItem(NormalizePath(Path.Combine(repositoryRoot, oldPath)), Deleted: true);
                yield return new GitStatusItem(NormalizePath(Path.Combine(repositoryRoot, newPath)), Deleted: false);
                continue;
            }

            var path = parts[++index];
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
