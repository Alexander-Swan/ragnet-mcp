using System.Text.RegularExpressions;
using RagNet.Mcp.Analyzers.Common;
using RagNet.Mcp.Analyzers.Interfaces;
using RagNet.Mcp.Indexing;

namespace RagNet.Mcp.Analyzers.Documentation;

public sealed partial class DocumentationAnalyzer : ICodeAnalyzer
{
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

    public bool CanAnalyze(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (!ExtensionLanguages.ContainsKey(extension))
        {
            return false;
        }

        return !string.Equals(extension, ".html", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".htm", StringComparison.OrdinalIgnoreCase) ||
            IsDocumentationHtmlPath(filePath);
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

    private static bool IsDocumentationHtmlPath(string filePath)
    {
        var segments = filePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        return segments.Any(segment =>
            segment.Equals("doc", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("docs", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("documentation", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("api-docs", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("generated-docs", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record DocumentationSection(int StartLine, string Title);

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+(?<title>.+?)\s*#*\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownHeadingRegex();

    [GeneratedRegex(@"<h[1-6][^>]*>(?<title>.*?)</h[1-6]>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlHeadingRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"^={1,6}\s+(?<title>.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex AsciiDocHeadingRegex();
}
