using System.Diagnostics;
using RagNet.Mcp.Source;

namespace RagNet.Mcp.Tests;

public sealed class GitSourceChangeDetectorTests
{
    [Fact]
    public async Task DetectChangesAsync_UsesPreviousCommitForCommittedDeltas()
    {
        var workspace = new TemporaryWorkspace();
        try
        {
            await RunGitAsync(workspace.RootPath, "init");
            await RunGitAsync(workspace.RootPath, "config user.email ragnet@example.test");
            await RunGitAsync(workspace.RootPath, "config user.name RagNet");

            var changed = workspace.WriteFile("src/Changed.cs", "old");
            var removed = workspace.WriteFile("src/Removed.cs", "remove me");
            await RunGitAsync(workspace.RootPath, "add .");
            await RunGitAsync(workspace.RootPath, "commit -m initial");
            var previousCommit = (await RunGitAsync(workspace.RootPath, "rev-parse HEAD")).Trim();

            await File.WriteAllTextAsync(changed, "new");
            File.Delete(removed);
            await RunGitAsync(workspace.RootPath, "add .");
            await RunGitAsync(workspace.RootPath, "commit -m update");

            var detector = new GitSourceChangeDetector();

            var changeSet = await detector.DetectChangesAsync(
                workspace.RootPath,
                candidateFiles: null,
                previouslyIndexedFiles: [changed, removed],
                previousCommit);

            Assert.True(changeSet.IsAvailable);
            Assert.True(changeSet.IsComplete);
            Assert.Contains(Path.GetFullPath(changed), changeSet.ChangedFiles);
            Assert.Contains(Path.GetFullPath(removed), changeSet.DeletedFiles);
        }
        finally
        {
            ClearReadOnlyAttributes(workspace.RootPath);
            workspace.Dispose();
        }
    }

    private static async Task<string> RunGitAsync(string workingDirectory, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        foreach (var argument in SplitArguments(arguments))
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed: {error}");
        }

        return output;
    }

    private static IEnumerable<string> SplitArguments(string arguments)
        => arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static void ClearReadOnlyAttributes(string rootPath)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(rootPath, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }
}
