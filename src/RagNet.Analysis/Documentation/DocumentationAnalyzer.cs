using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RagNet.Mcp.Analyzers;
using RagNet.Mcp.Analyzers.Common;
using RagNet.Mcp.Analyzers.Interfaces;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Indexing;

namespace RagNet.Mcp.Analyzers.Documentation;

public sealed partial class DocumentationAnalyzer : IContentAwareAnalyzer
{
    private const double StrongOverrideConfidence = 0.95;
    private const double BaselineDocumentationConfidence = 0.55;

    private readonly RagNetOptions _options;

    private static readonly Dictionary<string, string> ExtensionLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        [".md"] = "markdown",
        [".mdx"] = "mdx",
        [".html"] = "html",
        [".htm"] = "html",
        [".txt"] = "text",
        [".rst"] = "rst",
        [".adoc"] = "asciidoc",
        [".asciidoc"] = "asciidoc"
    };

    public DocumentationAnalyzer()
        : this(null)
    {
    }

    public DocumentationAnalyzer(IOptions<RagNetOptions>? options)
    {
        _options = options?.Value ?? new RagNetOptions();
    }

    public bool CanAnalyze(string filePath) => ExtensionLanguages.ContainsKey(Path.GetExtension(filePath));

    public async Task<AnalyzerMatch> MatchAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(filePath);
        if (!ExtensionLanguages.ContainsKey(extension))
        {
            return AnalyzerMatch.No;
        }

        if (ClassificationPathMatcher.MatchesAny(workspaceRoot, filePath, _options.Classification.DocumentationPathPatterns))
        {
            return AnalyzerMatch.Supported(StrongOverrideConfidence, "documentation_path_override");
        }

        var isAmbiguous = IsAmbiguousDocumentationExtension(extension);
        if (!isAmbiguous)
        {
            return AnalyzerMatch.Supported(0.8, "documentation_extension");
        }

        var source = await ReadProbeAsync(filePath, cancellationToken);
        var confidence = BaselineDocumentationConfidence;
        var reasons = new List<string> { "ambiguous_extension" };

        if (ClassificationPathMatcher.MatchesAny(workspaceRoot, filePath, _options.Classification.ApplicationMarkupPathPatterns))
        {
            confidence -= 0.25;
            reasons.Add("application_markup_path");
        }

        if (HtmlHeadingRegex().IsMatch(source) || MarkdownHeadingRegex().IsMatch(source))
        {
            confidence += 0.18;
            reasons.Add("headings");
        }

        if (DocumentationStructureRegex().IsMatch(source))
        {
            confidence += 0.14;
            reasons.Add("documentation_structure");
        }

        if (DocumentationGeneratorRegex().IsMatch(source))
        {
            confidence += 0.2;
            reasons.Add("documentation_generator");
        }

        if (ParagraphRegex().Matches(source).Count >= 2)
        {
            confidence += 0.08;
            reasons.Add("paragraph_density");
        }

        if (AppMarkupSignalRegex().IsMatch(source))
        {
            confidence -= 0.28;
            reasons.Add("application_markup_signals");
        }

        return AnalyzerMatch.Supported(confidence, string.Join(",", reasons));
    }

    public async Task<IReadOnlyList<CodeChunk>> AnalyzeAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default)
    {
        var source = await File.ReadAllTextAsync(filePath, cancellationToken);
        var language = ExtensionLanguages[Path.GetExtension(filePath)];
        var lines = TextChunkBuilder.NormalizeNewlines(source).Split('\n');
        var sections = FindSections(lines, language).ToArray();

        if (sections.Length == 0)
        {
            return TextChunkBuilder.Split(
                workspaceRoot,
                filePath,
                language,
                Path.GetFileName(filePath),
                "Document",
                1,
                Math.Max(1, lines.Length),
                source,
                IndexedContentTypes.Documentation);
        }

        var chunks = new List<CodeChunk>();
        for (var index = 0; index < sections.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var section = sections[index];
            var nextStartLine = index + 1 < sections.Length ? sections[index + 1].StartLine : lines.Length + 1;
            var endLine = Math.Max(section.StartLine, nextStartLine - 1);
            var content = string.Join(Environment.NewLine, lines.Skip(section.StartLine - 1).Take(endLine - section.StartLine + 1));

            chunks.AddRange(TextChunkBuilder.Split(
                workspaceRoot,
                filePath,
                language,
                section.Title,
                "DocumentSection",
                section.StartLine,
                endLine,
                content,
                IndexedContentTypes.Documentation));
        }

        return chunks;
    }

    private static IEnumerable<DocumentationSection> FindSections(IReadOnlyList<string> lines, string language)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var title = language switch
            {
                "markdown" or "mdx" => TryGetMarkdownHeading(line),
                "html" => TryGetHtmlHeading(line),
                "rst" => TryGetRstHeading(lines, index),
                "asciidoc" => TryGetAsciiDocHeading(line),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(title))
            {
                yield return new DocumentationSection(index + 1, title);
            }
        }
    }

    private static string? TryGetMarkdownHeading(string line)
    {
        var match = MarkdownHeadingRegex().Match(line);
        return match.Success ? match.Groups["title"].Value.Trim() : null;
    }

    private static string? TryGetHtmlHeading(string line)
    {
        var match = HtmlHeadingRegex().Match(line);
        return match.Success ? StripHtmlTags(match.Groups["title"].Value).Trim() : null;
    }

    private static string? TryGetRstHeading(IReadOnlyList<string> lines, int index)
    {
        if (index + 1 >= lines.Count || string.IsNullOrWhiteSpace(lines[index]))
        {
            return null;
        }

        var underline = lines[index + 1].Trim();
        return underline.Length >= Math.Min(3, lines[index].Trim().Length) && underline.All(character => character is '=' or '-' or '~' or '^' or '"')
            ? lines[index].Trim()
            : null;
    }

    private static string? TryGetAsciiDocHeading(string line)
    {
        var match = AsciiDocHeadingRegex().Match(line);
        return match.Success ? match.Groups["title"].Value.Trim() : null;
    }

    private static string StripHtmlTags(string value)
        => HtmlTagRegex().Replace(value, string.Empty);

    private static bool IsAmbiguousDocumentationExtension(string extension)
        => extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".htm", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mdx", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ReadProbeAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new StreamReader(stream);
            var buffer = new char[Math.Min(12_000, (int)Math.Min(stream.Length, int.MaxValue))];
            var read = await reader.ReadBlockAsync(buffer, cancellationToken);
            return new string(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private sealed record DocumentationSection(int StartLine, string Title);

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+(?<title>.+?)\s*#*\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownHeadingRegex();

    [GeneratedRegex(@"<h[1-6][^>]*>(?<title>.*?)</h[1-6]>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlHeadingRegex();

    [GeneratedRegex(@"<(?:article|main|section|nav|pre|code)\b|\b(?:table-of-contents|toc|breadcrumbs?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DocumentationStructureRegex();

    [GeneratedRegex(@"\b(?:docfx|docusaurus|sphinx|mkdocs|swagger-ui|redoc|asciidoctor|typedoc|storybook-docs)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DocumentationGeneratorRegex();

    [GeneratedRegex(@"<(?:p|li)\b[^>]*>[^<]{40,}</(?:p|li)>|^[^\r\n<>{}]{80,}$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ParagraphRegex();

    [GeneratedRegex(@"(\*ng(?:If|For)\b|\[(?:[A-Za-z][\w.-]*)\]\s*=|\((?:[A-Za-z][\w.-]*)\)\s*=|\[\(ngModel\)\]|routerLink\b|repeat\.for\b|if\.bind\b|value\.bind\b|click\.delegate\b|\bv-(?:if|for|model)\b|@[A-Za-z][\w.-]*\s*=|:[A-Za-z][\w.-]*\s*=|@(?:page|model|code)\b|\{Binding\b|\bx:Class\s*=)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AppMarkupSignalRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"^={1,6}\s+(?<title>.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex AsciiDocHeadingRegex();
}
