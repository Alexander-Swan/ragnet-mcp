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
    public async Task AnalyzeAsync_EnrichesChunksWithSyntaxContext()
    {
        using var workspace = new TemporaryWorkspace();
        var file = workspace.WriteFile(
            "Services/SampleService.cs",
            """
            namespace Demo.Services;

            public abstract class BaseService
            {
            }

            public sealed class SampleService : BaseService, IDisposable
            {
                public void Dispose()
                {
                }
            }
            """);

        var analyzer = new CSharpAnalyzer();
        var chunks = await analyzer.AnalyzeAsync(workspace.RootPath, file);

        var typeChunk = Assert.Single(chunks, chunk => chunk.SymbolName == "SampleService");
        Assert.Equal("Demo.Services.SampleService", typeChunk.FullyQualifiedSymbolName);
        Assert.Equal("Demo.Services", typeChunk.Namespace);
        Assert.Null(typeChunk.TypeContext);
        Assert.Equal("BaseService, IDisposable", typeChunk.BaseTypes);
        Assert.Contains("Symbol: Demo.Services.SampleService", typeChunk.Content);
        Assert.Contains("Namespace: Demo.Services", typeChunk.Content);
        Assert.Contains("Base types: BaseService, IDisposable", typeChunk.Content);

        var methodChunk = Assert.Single(chunks, chunk => chunk.SymbolName == "Dispose");
        Assert.Equal("Demo.Services.SampleService.Dispose", methodChunk.FullyQualifiedSymbolName);
        Assert.Equal("Demo.Services", methodChunk.Namespace);
        Assert.Equal("SampleService", methodChunk.TypeContext);
        Assert.Null(methodChunk.BaseTypes);
        Assert.Contains("Type context: SampleService", methodChunk.Content);
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
        Assert.All(methodParts, chunk => Assert.Equal("LargeMethod.Run", chunk.FullyQualifiedSymbolName));
        Assert.All(methodParts, chunk => Assert.Equal("LargeMethod", chunk.TypeContext));
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
