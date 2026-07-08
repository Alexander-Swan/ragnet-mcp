using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RagNet.Mcp.Analyzers.Interfaces;
using RagNet.Mcp.Indexing;

namespace RagNet.Mcp.Analyzers.CSharp;

public sealed class CSharpAnalyzer : ICodeAnalyzer
{
    private const string LanguageName = "csharp";

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

            var symbolName = member switch
            {
                BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
                MethodDeclarationSyntax method => method.Identifier.ValueText,
                ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
                PropertyDeclarationSyntax property => property.Identifier.ValueText,
                EventDeclarationSyntax evt => evt.Identifier.ValueText,
                FieldDeclarationSyntax field => string.Join(", ", field.Declaration.Variables.Select(variable => variable.Identifier.ValueText)),
                _ => string.Empty
            };

            if (string.IsNullOrWhiteSpace(symbolName))
            {
                continue;
            }

            var span = member.GetLocation().GetLineSpan();
            var startLine = span.StartLinePosition.Line + 1;
            var endLine = span.EndLinePosition.Line + 1;
            var content = member.ToFullString();
            var id = $"{Path.GetRelativePath(workspaceRoot, filePath)}:{startLine}:{endLine}:{symbolName}";

            chunks.Add(new CodeChunk(
                id,
                workspaceRoot,
                filePath,
                LanguageName,
                symbolName,
                member.Kind().ToString(),
                startLine,
                endLine,
                content));
        }

        if (chunks.Count == 0)
        {
            chunks.Add(new CodeChunk(
                $"{Path.GetRelativePath(workspaceRoot, filePath)}:1:{lineSpan.Count}:file",
                workspaceRoot,
                filePath,
                LanguageName,
                Path.GetFileName(filePath),
                "File",
                1,
                Math.Max(1, lineSpan.Count),
                source));
        }

        return chunks;
    }
}
