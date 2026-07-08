using RagNet.Mcp.Analyzers;
using RagNet.Mcp.Analyzers.CSharp;
using RagNet.Mcp.Analyzers.Interfaces;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Embeddings;
using RagNet.Mcp.Embeddings.Interfaces;
using RagNet.Mcp.Indexing;
using RagNet.Mcp.Indexing.Interfaces;
using RagNet.Mcp.Storage;
using RagNet.Mcp.Storage.Interfaces;
using RagNet.Mcp.Workspace;
using RagNet.Mcp.Workspace.Interfaces;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RagNetOptions>(builder.Configuration.GetSection(RagNetOptions.SectionName));

builder.Services.AddHttpClient<IEmbeddingProvider, OllamaEmbeddingProvider>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<RagNetOptions>>().Value;
    client.BaseAddress = new Uri(options.Ollama.BaseUrl);
});

builder.Services.AddHttpClient<QdrantVectorStore>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<RagNetOptions>>().Value;
    client.BaseAddress = new Uri(options.Qdrant.BaseUrl);
});

builder.Services.AddSingleton<IWorkspaceDetector, WorkspaceDetector>();
builder.Services.AddSingleton<IIndexedWorkspaceRegistry, IndexedWorkspaceRegistry>();
builder.Services.AddSingleton<IWorkspaceScopeResolver, WorkspaceScopeResolver>();
builder.Services.AddSingleton<ICodeAnalyzer, CSharpAnalyzer>();
// TODO: Add FSharpAnalyzer for *.fs and *.fsproj when F# symbol extraction is implemented.
// TODO: Add VisualBasicAnalyzer for *.vb and *.vbproj when VB.NET symbol extraction is implemented.
builder.Services.AddSingleton<IWorkspaceIndexStateStore, FileWorkspaceIndexStateStore>();
builder.Services.AddSingleton<InMemoryVectorStore>();
builder.Services.AddSingleton<IVectorStore>(serviceProvider => serviceProvider.GetRequiredService<QdrantVectorStore>());
builder.Services.AddSingleton<IWorkspaceIndexer, WorkspaceIndexer>();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

app.MapGet("/", () => Results.Json(new
{
    name = "ragnet-mcp",
    description = ".NET-native MCP server for local semantic code search",
    mcpEndpoint = "/ragnet-mcp",
    health = "/health"
}));

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "ragnet-mcp",
    time = DateTimeOffset.UtcNow
}));

app.MapMcp("/ragnet-mcp");

app.Run();

public partial class Program;
