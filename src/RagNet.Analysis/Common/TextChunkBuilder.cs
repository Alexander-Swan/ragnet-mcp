using RagNet.Mcp.Indexing;

namespace RagNet.Mcp.Analyzers.Common;

internal static class TextChunkBuilder
{
    public const int DefaultMaxChunkChars = 1500;

    public static IReadOnlyList<CodeChunk> Split(
        string workspaceRoot,
        string filePath,
        string language,
        string symbolName,
        string symbolKind,
        int startLine,
        int endLine,
        string content,
        string contentType,
        int maxChunkChars = DefaultMaxChunkChars)
    {
        if (content.Length <= maxChunkChars)
        {
            return
            [
                Create(
                    workspaceRoot,
                    filePath,
                    language,
                    BuildId(workspaceRoot, filePath, startLine, endLine, symbolName),
                    symbolName,
                    symbolKind,
                    startLine,
                    endLine,
                    content,
                    contentType)
            ];
        }

        var chunks = new List<CodeChunk>();
        var lines = NormalizeNewlines(content).Split('\n');
        var partLines = new List<string>();
        var partStartLine = startLine;
        var currentLength = 0;
        var part = 1;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var lineLength = line.Length + Environment.NewLine.Length;
            if (partLines.Count > 0 && currentLength + lineLength > maxChunkChars)
            {
                chunks.Add(CreatePart(
                    workspaceRoot,
                    filePath,
                    language,
                    symbolName,
                    symbolKind,
                    partStartLine,
                    partStartLine + partLines.Count - 1,
                    part,
                    partLines,
                    contentType));

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
            chunks.Add(CreatePart(
                workspaceRoot,
                filePath,
                language,
                symbolName,
                symbolKind,
                partStartLine,
                partStartLine + partLines.Count - 1,
                part,
                partLines,
                contentType));
        }

        return chunks;
    }

    public static string NormalizeNewlines(string value)
        => value.Replace("\r\n", "\n").Replace('\r', '\n');

    private static CodeChunk CreatePart(
        string workspaceRoot,
        string filePath,
        string language,
        string symbolName,
        string symbolKind,
        int startLine,
        int endLine,
        int part,
        IReadOnlyList<string> lines,
        string contentType)
        => Create(
            workspaceRoot,
            filePath,
            language,
            $"{BuildId(workspaceRoot, filePath, startLine, endLine, symbolName)}:part:{part}",
            $"{symbolName} part {part}",
            $"{symbolKind}Part",
            startLine,
            endLine,
            string.Join(Environment.NewLine, lines),
            contentType);

    private static CodeChunk Create(
        string workspaceRoot,
        string filePath,
        string language,
        string id,
        string symbolName,
        string symbolKind,
        int startLine,
        int endLine,
        string content,
        string contentType)
        => new(
            id,
            workspaceRoot,
            filePath,
            language,
            symbolName,
            symbolKind,
            startLine,
            endLine,
            content)
        {
            ContentType = contentType
        };

    private static string BuildId(string workspaceRoot, string filePath, int startLine, int endLine, string symbolName)
        => $"{Path.GetRelativePath(workspaceRoot, filePath)}:{startLine}:{endLine}:{SanitizeIdPart(symbolName)}";

    private static string SanitizeIdPart(string value)
    {
        var sanitized = new string(value.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray())
            .Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "chunk" : sanitized;
    }
}
