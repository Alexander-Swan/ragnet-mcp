namespace RagNet.Mcp.Indexing;

public static class IndexProfiles
{
    public const string All = "all";
    public const string Code = "code";
    public const string Documentation = "docs";
    public const string Metadata = "metadata";
    public const string Tests = "tests";
    public const string Frontend = "frontend";

    public static string Normalize(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            return All;
        }

        var normalized = profile.Trim().ToLowerInvariant();
        return normalized switch
        {
            All => All,
            Code => Code,
            Documentation or "documentation" => Documentation,
            Metadata or "project_metadata" => Metadata,
            Tests or "test" => Tests,
            Frontend or "ui" or "web" => Frontend,
            _ => throw new ArgumentException(
                $"Unsupported profile '{profile}'. Use code, docs, metadata, tests, frontend, or all.",
                nameof(profile))
        };
    }

    public static string? NormalizeFilter(string? profile)
    {
        var normalized = Normalize(profile);
        return normalized == All ? null : normalized;
    }
}
