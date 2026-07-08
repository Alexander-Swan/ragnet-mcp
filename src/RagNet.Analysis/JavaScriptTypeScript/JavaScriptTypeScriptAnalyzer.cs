using System.Text.RegularExpressions;
using RagNet.Mcp.Analyzers.Interfaces;
using RagNet.Mcp.Indexing;

namespace RagNet.Mcp.Analyzers.JavaScriptTypeScript;

public sealed partial class JavaScriptTypeScriptAnalyzer : ICodeAnalyzer
{
    private const int MaxChunkChars = 750;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js",
        ".jsx",
        ".mjs",
        ".cjs",
        ".ts",
        ".tsx",
        ".mts",
        ".cts"
    };

    public bool CanAnalyze(string filePath) => Extensions.Contains(Path.GetExtension(filePath));

    public async Task<IReadOnlyList<CodeChunk>> AnalyzeAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default)
    {
        var source = await File.ReadAllTextAsync(filePath, cancellationToken);
        var lines = SplitLines(source);
        var chunks = new List<CodeChunk>();
        var consumed = new List<(int Start, int End)>();

        AddDependencyChunk(workspaceRoot, filePath, lines, chunks);

        for (var index = 0; index < lines.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = lines[index];
            var declaration = MatchDeclaration(line);
            if (declaration is null)
            {
                AddRouteChunk(workspaceRoot, filePath, lines, index, chunks);
                continue;
            }

            var startLine = FindDeclarationStart(lines, index);
            var endLine = FindDeclarationEnd(lines, index);
            if (consumed.Any(span => startLine >= span.Start && startLine <= span.End))
            {
                continue;
            }

            consumed.Add((startLine, endLine));
            var content = string.Join(Environment.NewLine, lines[(startLine - 1)..endLine]);
            chunks.AddRange(CreateChunks(
                workspaceRoot,
                filePath,
                GetLanguage(filePath),
                declaration.Value.Name,
                declaration.Value.Kind,
                startLine,
                endLine,
                content));
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

    private static void AddDependencyChunk(string workspaceRoot, string filePath, IReadOnlyList<string> lines, List<CodeChunk> chunks)
    {
        var dependencyLines = lines
            .Select((Line, Index) => new { Line, Number = Index + 1 })
            .Where(item => ImportExportRegex().IsMatch(item.Line) || RequireRegex().IsMatch(item.Line))
            .ToArray();

        if (dependencyLines.Length == 0)
        {
            return;
        }

        chunks.AddRange(CreateChunks(
            workspaceRoot,
            filePath,
            GetLanguage(filePath),
            "module dependencies",
            "ImportsExports",
            dependencyLines[0].Number,
            dependencyLines[^1].Number,
            string.Join(Environment.NewLine, dependencyLines.Select(item => item.Line.TrimEnd()))));
    }

    private static void AddRouteChunk(string workspaceRoot, string filePath, IReadOnlyList<string> lines, int index, List<CodeChunk> chunks)
    {
        var match = JsxRouteRegex().Match(lines[index]);
        if (!match.Success)
        {
            return;
        }

        var routeName = $"route {match.Groups["path"].Value}";
        chunks.AddRange(CreateChunks(
            workspaceRoot,
            filePath,
            GetLanguage(filePath),
            routeName,
            "Route",
            index + 1,
            index + 1,
            lines[index].Trim()));
    }

    private static (string Name, string Kind)? MatchDeclaration(string line)
    {
        var classMatch = ClassRegex().Match(line);
        if (classMatch.Success)
        {
            return (classMatch.Groups["name"].Value, "Class");
        }

        var functionMatch = FunctionRegex().Match(line);
        if (functionMatch.Success)
        {
            return (functionMatch.Groups["name"].Value, "Function");
        }

        var assignedFunctionMatch = AssignedFunctionRegex().Match(line);
        if (assignedFunctionMatch.Success)
        {
            var name = assignedFunctionMatch.Groups["name"].Value;
            var kind = char.IsUpper(name[0]) ? "Component" : "Function";
            return (name, kind);
        }

        var exportMatch = ExportValueRegex().Match(line);
        if (exportMatch.Success)
        {
            return (exportMatch.Groups["name"].Value, "Export");
        }

        return null;
    }

    private static int FindDeclarationStart(IReadOnlyList<string> lines, int declarationIndex)
    {
        var start = declarationIndex;
        while (start > 0)
        {
            var previous = lines[start - 1].TrimStart();
            if (!previous.StartsWith("@", StringComparison.Ordinal) &&
                !previous.StartsWith("export ", StringComparison.Ordinal) &&
                !previous.StartsWith("declare ", StringComparison.Ordinal))
            {
                break;
            }

            start--;
        }

        return start + 1;
    }

    private static int FindDeclarationEnd(IReadOnlyList<string> lines, int declarationIndex)
    {
        var balance = 0;
        var sawBlock = false;

        for (var index = declarationIndex; index < lines.Count; index++)
        {
            var sanitized = StripStringsAndComments(lines[index]);
            foreach (var character in sanitized)
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

            if (!sawBlock && sanitized.Contains(';', StringComparison.Ordinal))
            {
                return index + 1;
            }
        }

        return lines.Count;
    }

    private static string StripStringsAndComments(string line)
    {
        var withoutLineComment = LineCommentRegex().Replace(line, string.Empty);
        return StringRegex().Replace(withoutLineComment, "\"\"");
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
        var contentType = GetContentType(filePath);
        if (content.Length <= MaxChunkChars)
        {
            return
            [
                new CodeChunk(id, workspaceRoot, filePath, language, symbolName, symbolKind, startLine, endLine, content)
                {
                    ContentType = contentType
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
                chunks.Add(CreatePart(workspaceRoot, filePath, language, contentType, id, symbolName, symbolKind, partStartLine, partStartLine + partLines.Count - 1, part, partLines));
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
            chunks.Add(CreatePart(workspaceRoot, filePath, language, contentType, id, symbolName, symbolKind, partStartLine, partStartLine + partLines.Count - 1, part, partLines));
        }

        return chunks;
    }

    private static CodeChunk CreatePart(
        string workspaceRoot,
        string filePath,
        string language,
        string contentType,
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
            ContentType = contentType
        };

    private static string[] SplitLines(string content) => content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string GetLanguage(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mts", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cts", StringComparison.OrdinalIgnoreCase)
            ? "typescript"
            : "javascript";
    }

    private static string GetContentType(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return extension.Equals(".jsx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase)
            ? IndexedContentTypes.Markup
            : IndexedContentTypes.Code;
    }

    [GeneratedRegex(@"^\s*import\s|^\s*export\s+(?:\{|\*|type\s|interface\s)|^\s*export\s+default\s+", RegexOptions.CultureInvariant)]
    private static partial Regex ImportExportRegex();

    [GeneratedRegex(@"\b(?:require\(|module\.exports|exports\.)", RegexOptions.CultureInvariant)]
    private static partial Regex RequireRegex();

    [GeneratedRegex(@"^\s*(?:export\s+default\s+|export\s+)?class\s+(?<name>[A-Za-z_$][\w$]*)", RegexOptions.CultureInvariant)]
    private static partial Regex ClassRegex();

    [GeneratedRegex(@"^\s*(?:export\s+default\s+|export\s+)?(?:async\s+)?function\s+(?<name>[A-Za-z_$][\w$]*)", RegexOptions.CultureInvariant)]
    private static partial Regex FunctionRegex();

    [GeneratedRegex(@"^\s*(?:export\s+)?(?:const|let|var)\s+(?<name>[A-Za-z_$][\w$]*)\s*(?::[^=]+)?=\s*(?:async\s*)?(?:\([^)]*\)|[A-Za-z_$][\w$]*)\s*=>|^\s*(?:export\s+)?(?:const|let|var)\s+(?<name>[A-Za-z_$][\w$]*)\s*=\s*(?:memo|forwardRef|lazy|observer)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex AssignedFunctionRegex();

    [GeneratedRegex(@"^\s*export\s+(?:const|let|var)\s+(?<name>[A-Za-z_$][\w$]*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ExportValueRegex();

    [GeneratedRegex(@"<Route\b[^>]*\bpath\s*=\s*[""'](?<path>[^""']+)[""']", RegexOptions.CultureInvariant)]
    private static partial Regex JsxRouteRegex();

    [GeneratedRegex(@"(?<!:)//.*$", RegexOptions.CultureInvariant)]
    private static partial Regex LineCommentRegex();

    [GeneratedRegex("""(?:"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|`(?:\\.|[^`\\])*`)""", RegexOptions.CultureInvariant)]
    private static partial Regex StringRegex();
}
