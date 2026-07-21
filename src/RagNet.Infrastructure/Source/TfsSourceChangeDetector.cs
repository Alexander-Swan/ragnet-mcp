using System.Text.RegularExpressions;
using RagNet.Mcp.Source.Interfaces;

namespace RagNet.Mcp.Source;

public sealed partial class TfsSourceChangeDetector(ITfsCommandRunner commandRunner) : ISourceChangeDetector
{
    public async Task<bool> CanDetectAsync(string workspaceRoot, CancellationToken cancellationToken = default)
        => await TfsWorkspaceDiscovery.DiscoverAsync(workspaceRoot, commandRunner, cancellationToken) is not null;

    public async Task<SourceChangeSet> DetectChangesAsync(
        string workspaceRoot,
        IReadOnlyList<string>? candidateFiles,
        IReadOnlyList<string> previouslyIndexedFiles,
        string? previousCommitSha = null,
        CancellationToken cancellationToken = default)
    {
        var workspace = await TfsWorkspaceDiscovery.DiscoverAsync(workspaceRoot, commandRunner, cancellationToken);
        if (workspace is null)
        {
            return SourceChangeSet.Unavailable("tfs", "No TFVC workspace was found.");
        }

        var previousChangeset = ParseTfvcChangeset(previousCommitSha);
        var currentChangeset = await TfsWorkspaceDiscovery.GetLatestChangesetAsync(workspace, commandRunner, cancellationToken);
        if (previousChangeset is null || currentChangeset is null)
        {
            return SourceChangeSet.Unavailable("tfs", "TFVC changeset metadata is not available for complete delta detection.");
        }

        var hasCandidateFilter = candidateFiles is { Count: > 0 };
        var candidateSet = hasCandidateFilter
            ? candidateFiles!.Select(TfsWorkspaceDiscovery.NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var previousSet = previouslyIndexedFiles
            .Select(TfsWorkspaceDiscovery.NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (currentChangeset.Value < previousChangeset.Value)
        {
            return SourceChangeSet.Unavailable("tfs", "TFVC workspace changeset moved backward; falling back to file scan.");
        }

        if (previousChangeset.Value != currentChangeset.Value)
        {
            var from = previousChangeset.Value + 1;
            var to = currentChangeset.Value;
            var history = await commandRunner.RunAsync(
                workspace.LocalPath,
                ["history", workspace.ServerPath, "/recursive", $"/version:C{from}~C{to}", "/format:detailed", "/noprompt"],
                cancellationToken);
            if (history.ExitCode != 0)
            {
                return SourceChangeSet.Unavailable("tfs", history.Error.Trim());
            }

            foreach (var item in ParseDetailedHistory(history.Output, workspace))
            {
                AddChange(item, hasCandidateFilter, candidateSet, previousSet, changed, deleted);
            }
        }

        var status = await commandRunner.RunAsync(
            workspace.LocalPath,
            ["status", workspace.LocalPath, "/recursive", "/format:detailed", "/noprompt"],
            cancellationToken);
        if (status.ExitCode != 0)
        {
            return SourceChangeSet.Unavailable("tfs", status.Error.Trim());
        }

        foreach (var item in ParseStatus(status.Output))
        {
            AddChange(item, hasCandidateFilter, candidateSet, previousSet, changed, deleted);
        }

        return new SourceChangeSet(
            "tfs",
            IsAvailable: true,
            changed.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            deleted.Order(StringComparer.OrdinalIgnoreCase).ToArray())
        {
            IsComplete = true
        };
    }

    public static int? ParseTfvcChangeset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("tfvc:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["tfvc:".Length..];
        }

        return int.TryParse(trimmed, out var changeset) ? changeset : null;
    }

    public static IReadOnlyList<TfsChangedItem> ParseDetailedHistory(string output, TfsWorkspaceInfo workspace)
    {
        var items = new List<TfsChangedItem>();
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var serverIndex = line.IndexOf("$/", StringComparison.Ordinal);
            if (serverIndex < 0)
            {
                continue;
            }

            var changeText = line[..serverIndex].Trim();
            var serverPath = StripTfvcVersion(line[serverIndex..].Trim());
            var localPath = MapServerPathToLocal(workspace, serverPath);
            if (localPath is null)
            {
                continue;
            }

            var deleted = changeText.Contains("delete", StringComparison.OrdinalIgnoreCase);
            items.Add(new TfsChangedItem(localPath, deleted));
        }

        return items;
    }

    public static IReadOnlyList<TfsChangedItem> ParseStatus(string output)
    {
        var items = new List<TfsChangedItem>();
        string? pendingChange = null;
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Change:", StringComparison.OrdinalIgnoreCase))
            {
                pendingChange = line["Change:".Length..].Trim();
                continue;
            }

            string? localPath = null;
            if (line.StartsWith("Local item:", StringComparison.OrdinalIgnoreCase))
            {
                localPath = line["Local item:".Length..].Trim();
            }
            else
            {
                var match = LocalPathRegex().Match(line);
                if (match.Success)
                {
                    localPath = match.Value.Trim();
                    pendingChange ??= line[..match.Index].Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(localPath))
            {
                continue;
            }

            var deleted = pendingChange?.Contains("delete", StringComparison.OrdinalIgnoreCase) == true;
            items.Add(new TfsChangedItem(TfsWorkspaceDiscovery.NormalizePath(localPath), deleted));
            pendingChange = null;
        }

        return items;
    }

    private static void AddChange(
        TfsChangedItem item,
        bool hasCandidateFilter,
        HashSet<string> candidateSet,
        HashSet<string> previousSet,
        HashSet<string> changed,
        HashSet<string> deleted)
    {
        if (hasCandidateFilter && !candidateSet.Contains(item.LocalPath) && !previousSet.Contains(item.LocalPath))
        {
            return;
        }

        if (item.Deleted)
        {
            deleted.Add(item.LocalPath);
            changed.Remove(item.LocalPath);
            return;
        }

        changed.Add(item.LocalPath);
        deleted.Remove(item.LocalPath);
    }

    private static string? MapServerPathToLocal(TfsWorkspaceInfo workspace, string serverPath)
    {
        if (!serverPath.StartsWith(workspace.ServerPath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = serverPath[workspace.ServerPath.Length..]
            .TrimStart('/', '\\')
            .Replace('/', Path.DirectorySeparatorChar);
        return TfsWorkspaceDiscovery.NormalizePath(Path.Combine(workspace.LocalPath, relative));
    }

    private static string StripTfvcVersion(string serverPath)
    {
        var separator = serverPath.IndexOf(';', StringComparison.Ordinal);
        return separator < 0 ? serverPath : serverPath[..separator];
    }

    [GeneratedRegex(@"(?:[A-Za-z]:\\|\\\\)[^\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex LocalPathRegex();
}

public sealed record TfsChangedItem(string LocalPath, bool Deleted);
