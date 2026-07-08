using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RagNet.Mcp.Analyzers.CSharp;
using RagNet.Mcp.Analyzers.Interfaces;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Embeddings;
using RagNet.Mcp.Embeddings.Interfaces;
using RagNet.Mcp.Indexing;
using RagNet.Mcp.Indexing.Interfaces;
using RagNet.Mcp.Source;
using RagNet.Mcp.Source.Interfaces;
using RagNet.Mcp.Storage;
using RagNet.Mcp.Storage.Interfaces;
using RagNet.Mcp.Workspace;
using RagNet.Mcp.Workspace.Interfaces;

namespace RagNet.Mcp.Composition;

public static class RagNetServiceCollectionExtensions
{
    public static IServiceCollection AddRagNetIndexingServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RagNetOptions>(configuration.GetSection(RagNetOptions.SectionName));

        services.AddHttpClient<IEmbeddingProvider, OllamaEmbeddingProvider>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<RagNetOptions>>().Value;
            client.BaseAddress = new Uri(options.Ollama.BaseUrl);
        });

        services.AddHttpClient<QdrantVectorStore>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<RagNetOptions>>().Value;
            client.BaseAddress = new Uri(options.Qdrant.BaseUrl);
        });

        services.AddSingleton<IWorkspaceDetector, WorkspaceDetector>();
        services.AddSingleton<IIndexedWorkspaceRegistry, IndexedWorkspaceRegistry>();
        services.AddSingleton<IWorkspaceScopeResolver, WorkspaceScopeResolver>();
        services.AddSingleton<ICodeAnalyzer, CSharpAnalyzer>();
        // TODO: Add FSharpAnalyzer for *.fs and *.fsproj when F# symbol extraction is implemented.
        // TODO: Add VisualBasicAnalyzer for *.vb and *.vbproj when VB.NET symbol extraction is implemented.
        services.AddSingleton<ISourceIdentityResolver, GitSourceIdentityResolver>();
        services.AddSingleton<IWorkspaceIndexStateStore, FileWorkspaceIndexStateStore>();
        services.AddSingleton<InMemoryVectorStore>();
        services.AddSingleton<IVectorStore>(serviceProvider => serviceProvider.GetRequiredService<QdrantVectorStore>());
        services.AddSingleton<IWorkspaceIndexer, WorkspaceIndexer>();
        services.AddSingleton<IIndexingJobQueue, InMemoryIndexingJobQueue>();

        return services;
    }
}
