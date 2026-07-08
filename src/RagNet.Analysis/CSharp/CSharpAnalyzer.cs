using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RagNet.Mcp.Analyzers.Interfaces;
using RagNet.Mcp.Indexing;

namespace RagNet.Mcp.Analyzers.CSharp;

public sealed class CSharpAnalyzer : ICodeAnalyzer
{
    private const string LanguageName = "csharp";
    private const int MaxChunkChars = 750;

    public bool CanAnalyze(string filePath) => string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<CodeChunk>> AnalyzeAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default)
    {
        var source = await File.ReadAllTextAsync(filePath, cancellationToken);
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: cancellationToken);
        var root = await tree.GetRootAsync(cancellationToken);
        var lineSpan = tree.GetText(cancellationToken).Lines;
        var chunks = new List<CodeChunk>();

        foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            chunks.AddRange(CreateChunks(workspaceRoot, filePath, member));
        }

        if (chunks.Count == 0)
        {
            chunks.AddRange(SplitChunk(
                workspaceRoot,
                filePath,
                $"{Path.GetRelativePath(workspaceRoot, filePath)}:1:{lineSpan.Count}:file",
                Path.GetFileName(filePath),
                "File",
                1,
                Math.Max(1, lineSpan.Count),
                source));
        }

        return chunks;
    }

    private static IReadOnlyList<CodeChunk> CreateChunks(string workspaceRoot, string filePath, MemberDeclarationSyntax member)
    {
        var symbolName = GetSymbolName(member);
        if (string.IsNullOrWhiteSpace(symbolName))
        {
            return [];
        }

        var span = member.GetLocation().GetLineSpan();
        var startLine = span.StartLinePosition.Line + 1;
        var endLine = span.EndLinePosition.Line + 1;
        var content = member is BaseTypeDeclarationSyntax type
            ? CreateTypeSummary(type)
            : member.ToFullString();
        var id = $"{Path.GetRelativePath(workspaceRoot, filePath)}:{startLine}:{endLine}:{symbolName}";

        return SplitChunk(
            workspaceRoot,
            filePath,
            id,
            symbolName,
            member is BaseTypeDeclarationSyntax ? $"{member.Kind()}Summary" : member.Kind().ToString(),
            startLine,
            endLine,
            content);
    }

    private static string GetSymbolName(MemberDeclarationSyntax member)
        => member switch
        {
            BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            EventDeclarationSyntax evt => evt.Identifier.ValueText,
            FieldDeclarationSyntax field => string.Join(", ", field.Declaration.Variables.Select(variable => variable.Identifier.ValueText)),
            _ => string.Empty
        };

    private static string CreateTypeSummary(BaseTypeDeclarationSyntax type)
    {
        var declaration = type switch
        {
            TypeDeclarationSyntax typeDeclaration => typeDeclaration
                .WithMembers([])
                .WithOpenBraceToken(default)
                .WithCloseBraceToken(default)
                .NormalizeWhitespace()
                .ToFullString(),
            EnumDeclarationSyntax enumDeclaration => enumDeclaration
                .WithMembers([])
                .WithOpenBraceToken(default)
                .WithCloseBraceToken(default)
                .NormalizeWhitespace()
                .ToFullString(),
            _ => type.ToString()
        };

        var members = type switch
        {
            TypeDeclarationSyntax typeDeclaration => typeDeclaration.Members
                .Select(CreateMemberSummary),
            EnumDeclarationSyntax enumDeclaration => enumDeclaration.Members
                .Select(member => $"enum member {member.Identifier.ValueText}"),
            _ => []
        };
        var memberSummaries = members
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        if (memberSummaries.Length == 0)
        {
            return declaration;
        }

        return $"{declaration}{Environment.NewLine}{Environment.NewLine}Members:{Environment.NewLine}{string.Join(Environment.NewLine, memberSummaries)}";
    }

    private static string CreateMemberSummary(MemberDeclarationSyntax member)
        => member switch
        {
            BaseTypeDeclarationSyntax type => $"{GetAccessibility(type.Modifiers)} {type.Kind()} {type.Identifier.ValueText}".Trim(),
            MethodDeclarationSyntax method => $"{GetAccessibility(method.Modifiers)} method {method.Identifier.ValueText}{method.ParameterList}".Trim(),
            ConstructorDeclarationSyntax constructor => $"{GetAccessibility(constructor.Modifiers)} constructor {constructor.Identifier.ValueText}{constructor.ParameterList}".Trim(),
            PropertyDeclarationSyntax property => $"{GetAccessibility(property.Modifiers)} property {property.Type} {property.Identifier.ValueText}".Trim(),
            FieldDeclarationSyntax field => $"{GetAccessibility(field.Modifiers)} field {field.Declaration.Type} {string.Join(", ", field.Declaration.Variables.Select(variable => variable.Identifier.ValueText))}".Trim(),
            EventDeclarationSyntax evt => $"{GetAccessibility(evt.Modifiers)} event {evt.Type} {evt.Identifier.ValueText}".Trim(),
            EventFieldDeclarationSyntax evt => $"{GetAccessibility(evt.Modifiers)} event {evt.Declaration.Type} {string.Join(", ", evt.Declaration.Variables.Select(variable => variable.Identifier.ValueText))}".Trim(),
            _ => string.Empty
        };

    private static string GetAccessibility(SyntaxTokenList modifiers)
    {
        var access = modifiers
            .Where(modifier => modifier.IsKind(SyntaxKind.PublicKeyword) ||
                modifier.IsKind(SyntaxKind.PrivateKeyword) ||
                modifier.IsKind(SyntaxKind.ProtectedKeyword) ||
                modifier.IsKind(SyntaxKind.InternalKeyword))
            .Select(modifier => modifier.ValueText)
            .ToArray();

        return access.Length == 0 ? "private" : string.Join(" ", access);
    }

    private static IReadOnlyList<CodeChunk> SplitChunk(
        string workspaceRoot,
        string filePath,
        string id,
        string symbolName,
        string symbolKind,
        int startLine,
        int endLine,
        string content)
    {
        if (content.Length <= MaxChunkChars)
        {
            return
            [
                new CodeChunk(
                    id,
                    workspaceRoot,
                    filePath,
                    LanguageName,
                    symbolName,
                    symbolKind,
                    startLine,
                    endLine,
                    content)
            ];
        }

        var chunks = new List<CodeChunk>();
        var lines = content.Replace("\r\n", "\n").Split('\n');
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
                chunks.Add(CreatePart(workspaceRoot, filePath, id, symbolName, symbolKind, partStartLine, partStartLine + partLines.Count - 1, part, partLines));
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
            chunks.Add(CreatePart(workspaceRoot, filePath, id, symbolName, symbolKind, partStartLine, partStartLine + partLines.Count - 1, part, partLines));
        }

        return chunks;
    }

    private static CodeChunk CreatePart(
        string workspaceRoot,
        string filePath,
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
            LanguageName,
            $"{symbolName} part {part}",
            $"{symbolKind}Part",
            startLine,
            endLine,
            string.Join(Environment.NewLine, lines));
}
