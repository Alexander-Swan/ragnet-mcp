using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Embeddings;

namespace RagNet.Mcp.Tests;

public sealed class OllamaEmbeddingProviderTests
{
    [Fact]
    public async Task EmbedBatchAsync_UsesEmbedEndpointWithMultipleInputs()
    {
        using var handler = new FakeOllamaHandler("""{"embeddings":[[1,0],[0,1]]}""");
        var provider = CreateProvider(handler);

        var embeddings = await provider.EmbedBatchAsync(["first", "second"]);

        Assert.Equal(2, embeddings.Count);
        Assert.Equal([1f, 0f], embeddings[0]);
        Assert.Equal([0f, 1f], embeddings[1]);
        Assert.Equal("POST", handler.Method);
        Assert.Equal("/api/embed", handler.Path);

        using var request = JsonDocument.Parse(handler.Body);
        Assert.Equal("test-embed", request.RootElement.GetProperty("model").GetString());
        Assert.Equal(["first", "second"], request.RootElement.GetProperty("input").EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task EmbedBatchAsync_ModelNotFoundThrowsSpecificException()
    {
        using var handler = new FakeOllamaHandler(
            """{"error":"model \"test-embed\" not found, try pulling it first"}""",
            HttpStatusCode.NotFound);
        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<EmbeddingModelNotFoundException>(
            () => provider.EmbedBatchAsync(["first"]));

        Assert.Equal("test-embed", exception.Model);
        Assert.Contains("ollama pull test-embed", exception.Message);
    }

    [Fact]
    public async Task ListInstalledModelsAsync_ReadsOllamaTags()
    {
        using var handler = new FakeOllamaHandler(new Dictionary<string, (string Body, HttpStatusCode StatusCode)>
        {
            ["/api/tags"] = ("""{"models":[{"name":"mxbai-embed-large:latest"},{"name":"nomic-embed-text:latest"}]}""", HttpStatusCode.OK)
        });
        var provider = CreateProvider(handler);

        var models = await provider.ListInstalledModelsAsync();

        Assert.Equal(["mxbai-embed-large:latest", "nomic-embed-text:latest"], models.Select(model => model.Name).ToArray());
        Assert.Equal("GET", handler.Method);
        Assert.Equal("/api/tags", handler.Path);
    }

    [Fact]
    public async Task ResolveEmbeddingModelAsync_MissingConfiguredModelFailsWithInstalledFallbackSuggestion()
    {
        using var handler = new FakeOllamaHandler(new Dictionary<string, (string Body, HttpStatusCode StatusCode)>
        {
            ["/api/tags"] = ("""{"models":[{"name":"nomic-embed-text:latest"}]}""", HttpStatusCode.OK)
        });
        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<EmbeddingModelNotFoundException>(
            () => provider.ResolveEmbeddingModelAsync());

        Assert.Equal("test-embed", exception.Model);
        Assert.True(exception.FallbackAvailable);
        Assert.Equal("nomic-embed-text", exception.FallbackModel);
        Assert.Contains("AllowInstalledEmbeddingModelFallback", exception.Message);
        Assert.Equal(["nomic-embed-text:latest"], exception.InstalledModels.Select(model => model.Name).ToArray());
    }

    [Fact]
    public async Task ResolveEmbeddingModelAsync_UsesFallbackOnlyWhenEnabled()
    {
        using var handler = new FakeOllamaHandler(new Dictionary<string, (string Body, HttpStatusCode StatusCode)>
        {
            ["/api/tags"] = ("""{"models":[{"name":"nomic-embed-text:latest"}]}""", HttpStatusCode.OK),
            ["/api/embed"] = ("""{"embeddings":[[1,0]]}""", HttpStatusCode.OK)
        });
        var provider = CreateProvider(handler, allowFallback: true);

        var resolution = await provider.ResolveEmbeddingModelAsync();
        await provider.EmbedBatchAsync(["first"]);

        Assert.True(resolution.UsedFallback);
        Assert.Equal("test-embed", resolution.ConfiguredModel);
        Assert.Equal("nomic-embed-text", resolution.SelectedModel);
        Assert.Equal("POST", handler.Method);
        Assert.Equal("/api/embed", handler.Path);

        using var request = JsonDocument.Parse(handler.Body);
        Assert.Equal("nomic-embed-text", request.RootElement.GetProperty("model").GetString());
    }

    private static OllamaEmbeddingProvider CreateProvider(FakeOllamaHandler handler, bool allowFallback = false)
        => new(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("http://ollama.test/")
            },
            Options.Create(new RagNetOptions
            {
                Ollama = new OllamaOptions
                {
                    EmbeddingModel = "test-embed",
                    FallbackEmbeddingModel = "nomic-embed-text",
                    AllowInstalledEmbeddingModelFallback = allowFallback
                }
            }));

    private sealed class FakeOllamaHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, (string Body, HttpStatusCode StatusCode)> _responses;

        public FakeOllamaHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
            : this(new Dictionary<string, (string Body, HttpStatusCode StatusCode)>
            {
                ["/api/embed"] = (responseBody, statusCode)
            })
        {
        }

        public FakeOllamaHandler(IReadOnlyDictionary<string, (string Body, HttpStatusCode StatusCode)> responses)
        {
            _responses = responses;
        }

        public string Method { get; private set; } = string.Empty;

        public string Path { get; private set; } = string.Empty;

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method.Method;
            Path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (!_responses.TryGetValue(Path, out var response))
            {
                response = ("""{"error":"not found"}""", HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json")
            };
        }
    }
}
