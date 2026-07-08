using System.Text.RegularExpressions;

namespace RagNet.Mcp.Analyzers.Common;

internal static class ClassificationPathMatcher
{
    public static bool MatchesAny(string workspaceRoot, string filePath, IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0)
        {
            return false;
        }

        var relativePath = Normalize(Path.GetRelativePath(workspaceRoot, filePath));
        var fullPath = Normalize(filePath);

        return patterns
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Any(pattern => IsMatch(relativePath, pattern) || IsMatch(fullPath, pattern));
    }

    private static bool IsMatch(string path, string pattern)
    {
        var normalizedPattern = Normalize(pattern.Trim());
        var regex = "^" + Regex.Escape(normalizedPattern)
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*")
            .Replace("\\?", "[^/]") + "$";

        return Regex.IsMatch(path, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string Normalize(string path)
        => path.Replace('\\', '/').Trim('/');
}
