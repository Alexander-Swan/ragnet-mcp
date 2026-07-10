namespace RagNet.Mcp.Workspace;

public sealed record IndexedWorkspaceRecord(
    string WorkspaceRoot,
    string WorkspaceId,
    string CollectionName,
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> IndexedTargets,
    DateTimeOffset LastIndexedUtc,
    int FilesScanned,
    int ChunksIndexed,
    bool FullReindex,
    string? RepositoryRoot = null,
    string? RepositoryRelativeWorkspaceRoot = null,
    string? RemoteUrl = null,
    string? Branch = null,
    string? CommitSha = null,
    IReadOnlyList<string>? IndexedTargetRelativePaths = null,
    string? DisplayName = null,
    IReadOnlyList<string>? Aliases = null)
{
    public string EffectiveDisplayName
        => string.IsNullOrWhiteSpace(DisplayName)
            ? IndexedWorkspaceRecordNames.GetDisplayName(WorkspaceRoot)
            : DisplayName;

    public IReadOnlyList<string> EffectiveAliases
        => IndexedWorkspaceRecordNames.GetAliases(WorkspaceRoot, IndexedTargets, Aliases);

    public IndexedWorkspaceRecord WithCalculatedNames()
        => this with
        {
            DisplayName = IndexedWorkspaceRecordNames.GetDisplayName(WorkspaceRoot),
            Aliases = IndexedWorkspaceRecordNames.GetAliases(WorkspaceRoot, IndexedTargets, Aliases)
        };
}

public static class IndexedWorkspaceRecordNames
{
    private static readonly HashSet<string> SolutionExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sln",
        ".slnx"
    };

    public static string GetDisplayName(string workspaceRoot)
    {
        var normalizedRoot = NormalizePath(workspaceRoot);
        var name = GetFileName(normalizedRoot);
        return string.IsNullOrWhiteSpace(name) ? normalizedRoot : name;
    }

    public static IReadOnlyList<string> GetAliases(
        string workspaceRoot,
        IReadOnlyList<string> indexedTargets,
        IReadOnlyList<string>? existingAliases = null)
    {
        var aliases = new List<string>();
        AddAlias(aliases, GetDisplayName(workspaceRoot));

        foreach (var target in indexedTargets.Where(target => !string.IsNullOrWhiteSpace(target)))
        {
            var normalizedTarget = NormalizePath(target);
            if (IsSolutionPath(normalizedTarget))
            {
                var fileName = GetFileName(normalizedTarget);
                AddAlias(aliases, fileName);
                AddAlias(aliases, Path.GetFileNameWithoutExtension(fileName));
                continue;
            }

            if (Directory.Exists(normalizedTarget))
            {
                AddAlias(aliases, GetFileName(normalizedTarget));
            }
        }

        foreach (var alias in existingAliases ?? [])
        {
            AddAlias(aliases, alias);
        }

        return aliases.ToArray();
    }

    public static bool MatchesAlias(IndexedWorkspaceRecord record, string alias)
        => record.EffectiveAliases.Any(candidate => string.Equals(candidate, alias, StringComparison.OrdinalIgnoreCase));

    public static string GetIndexTargetForAlias(IndexedWorkspaceRecord record, string alias)
    {
        foreach (var target in record.IndexedTargets.Where(target => !string.IsNullOrWhiteSpace(target)))
        {
            var normalizedTarget = NormalizePath(target);
            if (IsSolutionPath(normalizedTarget))
            {
                var fileName = Path.GetFileName(normalizedTarget);
                if (string.Equals(fileName, alias, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileNameWithoutExtension(fileName), alias, StringComparison.OrdinalIgnoreCase))
                {
                    return normalizedTarget;
                }
            }
        }

        return record.WorkspaceRoot;
    }

    private static void AddAlias(List<string> aliases, string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias) ||
            aliases.Contains(alias, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        aliases.Add(alias);
    }

    private static bool IsSolutionPath(string path)
        => SolutionExtensions.Contains(Path.GetExtension(path));

    private static string GetFileName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '\\', '/');
        var separatorIndex = trimmed.LastIndexOfAny(['\\', '/']);
        return separatorIndex < 0 ? trimmed : trimmed[(separatorIndex + 1)..];
    }

    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim();
        if (IsWindowsFullyQualifiedPath(trimmed))
        {
            return char.ToUpperInvariant(trimmed[0]) + trimmed[1..].Replace('/', '\\').TrimEnd('\\');
        }

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return fullPath.Length == root?.Length
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsWindowsFullyQualifiedPath(string path)
        => path.Length >= 3 &&
            char.IsAsciiLetter(path[0]) &&
            path[1] == ':' &&
            (path[2] == '\\' || path[2] == '/');
}
