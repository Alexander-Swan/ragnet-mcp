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

    private static OllamaEmbeddingProvider CreateProvider(FakeOllamaHandler handler)
        => new(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("http://ollama.test/")
            },
            Options.Create(new RagNetOptions
            {
                Ollama = new OllamaOptions
                {
                    EmbeddingModel = "test-embed"
                }
            }));

    private sealed class FakeOllamaHandler(
        string responseBody,
        HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
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

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
