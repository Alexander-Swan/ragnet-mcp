using System.Text.RegularExpressions;

namespace RagNet.Mcp.Source;

public static partial class TfsWorkspaceDiscovery
{
    public static async Task<TfsWorkspaceInfo?> DiscoverAsync(
        string workspaceRoot,
        ITfsCommandRunner runner,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = NormalizePath(workspaceRoot);
        var result = await runner.RunAsync(
            normalizedRoot,
            ["workfold", normalizedRoot, "/noprompt"],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            return null;
        }

        return ParseWorkfold(result.Output, normalizedRoot);
    }

    public static TfsWorkspaceInfo? ParseWorkfold(string output, string workspaceRoot)
    {
        var workspaceName = string.Empty;
        var collectionUrl = string.Empty;
        var mappings = new List<TfsWorkspaceMapping>();
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Workspace:", StringComparison.OrdinalIgnoreCase))
            {
                workspaceName = line["Workspace:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("Collection:", StringComparison.OrdinalIgnoreCase))
            {
                collectionUrl = line["Collection:".Length..].Trim();
                continue;
            }

            var serverIndex = line.IndexOf("$/", StringComparison.Ordinal);
            if (serverIndex < 0)
            {
                continue;
            }

            var separator = line.IndexOf(':', serverIndex);
            if (separator < 0)
            {
                continue;
            }

            var serverPath = line[serverIndex..separator].Trim();
            var localPath = line[(separator + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(serverPath) || string.IsNullOrWhiteSpace(localPath))
            {
                continue;
            }

            mappings.Add(new TfsWorkspaceMapping(serverPath, NormalizePath(localPath)));
        }

        var normalizedRoot = NormalizePath(workspaceRoot);
        var mapping = mappings
            .Where(mapping => IsPathUnderRoot(normalizedRoot, mapping.LocalPath))
            .OrderByDescending(mapping => mapping.LocalPath.Length)
            .FirstOrDefault();
        return mapping is null
            ? null
            : new TfsWorkspaceInfo(workspaceName, collectionUrl, mapping.ServerPath, mapping.LocalPath);
    }

    public static async Task<int?> GetLatestChangesetAsync(
        TfsWorkspaceInfo workspace,
        ITfsCommandRunner runner,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            workspace.LocalPath,
            ["history", workspace.ServerPath, "/recursive", "/stopafter:1", "/format:brief", "/noprompt"],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            return null;
        }

        return ParseLatestChangeset(result.Output);
    }

    public static int? ParseLatestChangeset(string output)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = ChangesetLineRegex().Match(line.Trim());
            if (match.Success && int.TryParse(match.Groups["changeset"].Value, out var changeset))
            {
                return changeset;
            }
        }

        return null;
    }

    public static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return fullPath.Length == root?.Length
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool IsPathUnderRoot(string path, string root)
    {
        var normalizedPath = NormalizePath(path);
        var normalizedRoot = NormalizePath(root);
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(
                normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^(?:Changeset:\s*)?(?<changeset>\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChangesetLineRegex();
}

public sealed record TfsWorkspaceInfo(string WorkspaceName, string CollectionUrl, string ServerPath, string LocalPath);

internal sealed record TfsWorkspaceMapping(string ServerPath, string LocalPath);
