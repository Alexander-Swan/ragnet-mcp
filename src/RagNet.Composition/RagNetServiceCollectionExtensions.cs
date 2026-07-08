using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RagNet.Mcp.Analyzers.CSharp;
using RagNet.Mcp.Analyzers.Documentation;
using RagNet.Mcp.Analyzers.DotNet;
using RagNet.Mcp.Analyzers.Interfaces;
using RagNet.Mcp.Analyzers.JavaScriptTypeScript;
using RagNet.Mcp.Analyzers.Markup;
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

        services.AddHttpClient<QdrantIndexedWorkspaceRegistry>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<RagNetOptions>>().Value;
            client.BaseAddress = new Uri(options.Qdrant.BaseUrl);
        });

        services.AddHttpClient<QdrantWorkspaceIndexStateStore>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<RagNetOptions>>().Value;
            client.BaseAddress = new Uri(options.Qdrant.BaseUrl);
        });

        services.AddSingleton<IWorkspaceDetector, WorkspaceDetector>();
        services.AddSingleton<IWorkspaceScopeResolver, WorkspaceScopeResolver>();
        services.AddSingleton<ICodeAnalyzer, CSharpAnalyzer>();
        services.AddSingleton<ICodeAnalyzer, DocumentationAnalyzer>();
        services.AddSingleton<ICodeAnalyzer, ProjectMetadataAnalyzer>();
        services.AddSingleton<ICodeAnalyzer, JavaScriptTypeScriptAnalyzer>();
        services.AddSingleton<ICodeAnalyzer, MarkupAnalyzer>();
        services.AddSingleton<ISourceIdentityResolver, GitSourceIdentityResolver>();
        services.AddSingleton<IWorkspaceIndexStateStore>(serviceProvider => serviceProvider.GetRequiredService<QdrantWorkspaceIndexStateStore>());
        services.AddSingleton<InMemoryVectorStore>();
        services.AddSingleton<IVectorStore>(serviceProvider => serviceProvider.GetRequiredService<QdrantVectorStore>());
        services.AddSingleton<IIndexedWorkspaceRegistry>(serviceProvider => serviceProvider.GetRequiredService<QdrantIndexedWorkspaceRegistry>());
        services.AddSingleton<IWorkspaceIndexer, WorkspaceIndexer>();
        services.AddSingleton<IIndexingJobQueue, InMemoryIndexingJobQueue>();

        return services;
    }
}
