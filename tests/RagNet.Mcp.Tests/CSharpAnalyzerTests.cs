using RagNet.Mcp.Analyzers.CSharp;

namespace RagNet.Mcp.Tests;

public sealed class CSharpAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_CreatesTypeSummaryWithoutFullMethodBody()
    {
        using var workspace = new TemporaryWorkspace();
        var file = workspace.WriteFile(
            "Sample.cs",
            """
            namespace Demo;

            public sealed class Sample
            {
                public void Run()
                {
                    var secretImplementationDetail = 42;
                    Console.WriteLine(secretImplementationDetail);
                }
            }
            """);

        var analyzer = new CSharpAnalyzer();
        var chunks = await analyzer.AnalyzeAsync(workspace.RootPath, file);

        var typeChunk = Assert.Single(chunks, chunk => chunk.SymbolName == "Sample");
        Assert.Contains("Members:", typeChunk.Content);
        Assert.Contains("method Run()", typeChunk.Content);
        Assert.DoesNotContain("secretImplementationDetail", typeChunk.Content);
    }

    [Fact]
    public async Task AnalyzeAsync_SplitsOversizedMemberChunks()
    {
        using var workspace = new TemporaryWorkspace();
        var longBody = string.Join(Environment.NewLine, Enumerable.Range(0, 450)
            .Select(index => $"""        Console.WriteLine("line {index:000} {new string('x', 40)}");"""));
        var file = workspace.WriteFile(
            "LargeMethod.cs",
            $$"""
            public sealed class LargeMethod
            {
                public void Run()
                {
            {{longBody}}
                }
            }
            """);

        var analyzer = new CSharpAnalyzer();
        var chunks = await analyzer.AnalyzeAsync(workspace.RootPath, file);

        var methodParts = chunks
            .Where(chunk => chunk.SymbolName.StartsWith("Run part ", StringComparison.Ordinal))
            .ToArray();

        Assert.True(methodParts.Length > 1);
        Assert.All(methodParts, chunk => Assert.True(chunk.Content.Length <= 750));
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public string RootPath { get; } = Path.Combine(Path.GetTempPath(), $"ragnet-tests-{Guid.NewGuid():N}");

        public TemporaryWorkspace()
        {
            Directory.CreateDirectory(RootPath);
        }

        public string WriteFile(string relativePath, string content)
        {
            var file = Path.Combine(RootPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
            return file;
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
