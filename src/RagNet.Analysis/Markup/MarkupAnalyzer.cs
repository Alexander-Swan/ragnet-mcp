using System.Text.RegularExpressions;
using RagNet.Mcp.Analyzers.Interfaces;
using RagNet.Mcp.Indexing;

namespace RagNet.Mcp.Analyzers.Markup;

public sealed partial class MarkupAnalyzer : ICodeAnalyzer
{
    private const int MaxChunkChars = 750;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cshtml",
        ".razor",
        ".vbhtml",
        ".aspx",
        ".ascx",
        ".master",
        ".xaml",
        ".html",
        ".css",
        ".scss",
        ".sass",
        ".less"
    };

    public bool CanAnalyze(string filePath) => Extensions.Contains(Path.GetExtension(filePath));

    public async Task<IReadOnlyList<CodeChunk>> AnalyzeAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default)
    {
        var source = await File.ReadAllTextAsync(filePath, cancellationToken);
        var lines = SplitLines(source);
        var chunks = new List<CodeChunk>();

        if (IsStyleSheet(filePath))
        {
            AddStyleChunks(workspaceRoot, filePath, lines, chunks, cancellationToken);
        }
        else
        {
            AddDirectiveChunk(workspaceRoot, filePath, lines, chunks);
            AddMarkupChunks(workspaceRoot, filePath, lines, chunks, cancellationToken);
        }

        if (chunks.Count == 0)
        {
            chunks.AddRange(CreateChunks(
                workspaceRoot,
                filePath,
                GetLanguage(filePath),
                Path.GetFileName(filePath),
                "File",
                1,
                Math.Max(1, lines.Length),
                source));
        }

        return chunks;
    }

    private static void AddDirectiveChunk(string workspaceRoot, string filePath, IReadOnlyList<string> lines, List<CodeChunk> chunks)
    {
        var directiveLines = lines
            .Select((Line, Index) => new { Line, Number = Index + 1 })
            .Where(item => RazorDirectiveRegex().IsMatch(item.Line) || AspxDirectiveRegex().IsMatch(item.Line) || XamlClassRegex().IsMatch(item.Line))
            .ToArray();

        if (directiveLines.Length == 0)
        {
            return;
        }

        chunks.AddRange(CreateChunks(
            workspaceRoot,
            filePath,
            GetLanguage(filePath),
            "markup directives",
            "Directives",
            directiveLines[0].Number,
            directiveLines[^1].Number,
            string.Join(Environment.NewLine, directiveLines.Select(item => item.Line.TrimEnd()))));
    }

    private static void AddMarkupChunks(
        string workspaceRoot,
        string filePath,
        IReadOnlyList<string> lines,
        List<CodeChunk> chunks,
        CancellationToken cancellationToken)
    {
        var consumed = new List<(int Start, int End)>();

        for (var index = 0; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var codeBlock = RazorCodeRegex().Match(lines[index]);
            if (codeBlock.Success)
            {
                var endLine = FindBraceBlockEnd(lines, index);
                consumed.Add((index + 1, endLine));
                chunks.AddRange(CreateChunks(
                    workspaceRoot,
                    filePath,
                    GetLanguage(filePath),
                    "@code",
                    "CodeBlock",
                    index + 1,
                    endLine,
                    string.Join(Environment.NewLine, lines.Skip(index).Take(endLine - index))));
                continue;
            }

            if (consumed.Any(span => index + 1 >= span.Start && index + 1 <= span.End))
            {
                continue;
            }

            var element = ElementRegex().Match(lines[index]);
            if (!element.Success)
            {
                continue;
            }

            var tag = element.Groups["tag"].Value;
            if (IsLowValueTag(tag) && !HasBindingOrEvent(lines[index]))
            {
                continue;
            }

            var end = FindElementEnd(lines, index, tag);
            var content = string.Join(Environment.NewLine, lines.Skip(index).Take(end - index));
            var symbolName = GetElementSymbolName(tag, content);
            var kind = HasBindingOrEvent(content) ? "BoundElement" : "Element";
            chunks.AddRange(CreateChunks(workspaceRoot, filePath, GetLanguage(filePath), symbolName, kind, index + 1, end, content));
        }
    }

    private static void AddStyleChunks(
        string workspaceRoot,
        string filePath,
        IReadOnlyList<string> lines,
        List<CodeChunk> chunks,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var match = CssSelectorRegex().Match(lines[index]);
            if (!match.Success)
            {
                continue;
            }

            var end = FindBraceBlockEnd(lines, index);
            var selector = match.Groups["selector"].Value.Trim();
            chunks.AddRange(CreateChunks(
                workspaceRoot,
                filePath,
                GetLanguage(filePath),
                selector,
                selector.StartsWith("@", StringComparison.Ordinal) ? "StyleAtRule" : "StyleRule",
                index + 1,
                end,
                string.Join(Environment.NewLine, lines.Skip(index).Take(end - index))));
        }
    }

    private static int FindBraceBlockEnd(IReadOnlyList<string> lines, int startIndex)
    {
        var balance = 0;
        var sawBlock = false;

        for (var index = startIndex; index < lines.Count; index++)
        {
            foreach (var character in lines[index])
            {
                if (character == '{')
                {
                    balance++;
                    sawBlock = true;
                }
                else if (character == '}')
                {
                    balance--;
                }
            }

            if (sawBlock && balance <= 0)
            {
                return index + 1;
            }
        }

        return lines.Count;
    }

    private static int FindElementEnd(IReadOnlyList<string> lines, int startIndex, string tag)
    {
        if (lines[startIndex].Contains("/>", StringComparison.Ordinal) || VoidTags().Contains(tag))
        {
            return startIndex + 1;
        }

        var closing = $"</{tag}";
        for (var index = startIndex; index < lines.Count; index++)
        {
            if (lines[index].Contains(closing, StringComparison.OrdinalIgnoreCase))
            {
                return index + 1;
            }
        }

        return startIndex + 1;
    }

    private static string GetElementSymbolName(string tag, string content)
    {
        var nameMatch = ElementNameRegex().Match(content);
        if (nameMatch.Success)
        {
            return $"{tag}#{nameMatch.Groups["name"].Value}";
        }

        var classMatch = ClassRegex().Match(content);
        return classMatch.Success ? $"{tag}.{classMatch.Groups["class"].Value}" : tag;
    }

    private static bool HasBindingOrEvent(string content)
        => BindingOrEventRegex().IsMatch(content);

    private static bool IsLowValueTag(string tag)
        => tag.Equals("div", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("span", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("p", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("li", StringComparison.OrdinalIgnoreCase);

    private static bool IsStyleSheet(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return extension.Equals(".css", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".scss", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".sass", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".less", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<CodeChunk> CreateChunks(
        string workspaceRoot,
        string filePath,
        string language,
        string symbolName,
        string symbolKind,
        int startLine,
        int endLine,
        string content)
    {
        var id = $"{Path.GetRelativePath(workspaceRoot, filePath)}:{startLine}:{endLine}:{symbolName}";
        if (content.Length <= MaxChunkChars)
        {
            return
            [
                new CodeChunk(id, workspaceRoot, filePath, language, symbolName, symbolKind, startLine, endLine, content)
                {
                    ContentType = IndexedContentTypes.Markup
                }
            ];
        }

        var chunks = new List<CodeChunk>();
        var lines = SplitLines(content);
        var partLines = new List<string>();
        var partStartLine = startLine;
        var currentLength = 0;
        var part = 1;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var lineLength = line.Length + Environment.NewLine.Length;
            if (partLines.Count > 0 && currentLength + lineLength > MaxChunkChars)
            {
                chunks.Add(CreatePart(workspaceRoot, filePath, language, id, symbolName, symbolKind, partStartLine, partStartLine + partLines.Count - 1, part, partLines));
                part++;
                partLines.Clear();
                currentLength = 0;
                partStartLine = startLine + index;
            }

            partLines.Add(line);
            currentLength += lineLength;
        }

        if (partLines.Count > 0)
        {
            chunks.Add(CreatePart(workspaceRoot, filePath, language, id, symbolName, symbolKind, partStartLine, partStartLine + partLines.Count - 1, part, partLines));
        }

        return chunks;
    }

    private static CodeChunk CreatePart(
        string workspaceRoot,
        string filePath,
        string language,
        string id,
        string symbolName,
        string symbolKind,
        int startLine,
        int endLine,
        int part,
        IReadOnlyList<string> lines)
        => new(
            $"{id}:part:{part}",
            workspaceRoot,
            filePath,
            language,
            $"{symbolName} part {part}",
            $"{symbolKind}Part",
            startLine,
            endLine,
            string.Join(Environment.NewLine, lines))
        {
            ContentType = IndexedContentTypes.Markup
        };

    private static string[] SplitLines(string content) => content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string GetLanguage(string filePath)
        => Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".razor" => "razor",
            ".cshtml" or ".vbhtml" => "razor",
            ".aspx" or ".ascx" or ".master" => "aspnet",
            ".xaml" => "xaml",
            ".css" or ".scss" or ".sass" or ".less" => "css",
            _ => "html"
        };

    private static HashSet<string> VoidTags()
        => new(StringComparer.OrdinalIgnoreCase)
        {
            "area",
            "base",
            "br",
            "col",
            "embed",
            "hr",
            "img",
            "input",
            "link",
            "meta",
            "param",
            "source",
            "track",
            "wbr"
        };

    [GeneratedRegex(@"^\s*@(?:page|model|using|inject|inherits|layout|namespace|attribute)\b", RegexOptions.CultureInvariant)]
    private static partial Regex RazorDirectiveRegex();

    [GeneratedRegex(@"^\s*<%@\s*(?:Page|Control|Master|Register|Import)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AspxDirectiveRegex();

    [GeneratedRegex(@"\bx:Class\s*=", RegexOptions.CultureInvariant)]
    private static partial Regex XamlClassRegex();

    [GeneratedRegex(@"^\s*@code\s*\{", RegexOptions.CultureInvariant)]
    private static partial Regex RazorCodeRegex();

    [GeneratedRegex(@"<(?<tag>[A-Za-z_][\w:.-]*)(?:\s|>|/)", RegexOptions.CultureInvariant)]
    private static partial Regex ElementRegex();

    [GeneratedRegex(@"\b(?:id|x:Name|Name|asp:ID)\s*=\s*[""'](?<name>[^""']+)[""']", RegexOptions.CultureInvariant)]
    private static partial Regex ElementNameRegex();

    [GeneratedRegex(@"\bclass\s*=\s*[""'](?<class>[^""'\s]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ClassRegex();

    [GeneratedRegex(@"(@bind|@on\w+|on\w+\s*=|\b(?:Click|Command|ItemsSource|DataContext|Text|Value|SelectedItem)\s*=|\{Binding\b|asp-for\s*=|asp-action\s*=|asp-controller\s*=)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BindingOrEventRegex();

    [GeneratedRegex(@"^\s*(?<selector>(?:@[\w-]+[^{]+|[^{}]+))\s*\{", RegexOptions.CultureInvariant)]
    private static partial Regex CssSelectorRegex();
}
