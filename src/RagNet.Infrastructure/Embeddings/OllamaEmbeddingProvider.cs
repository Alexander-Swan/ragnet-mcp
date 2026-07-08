using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Embeddings.Interfaces;

namespace RagNet.Mcp.Embeddings;

public sealed class OllamaEmbeddingProvider(HttpClient httpClient, IOptions<RagNetOptions> options) : IEmbeddingProvider
{
    private readonly RagNetOptions _options = options.Value;

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var request = new
        {
            model = _options.Ollama.EmbeddingModel,
            prompt = text
        };

        using var response = await httpClient.PostAsJsonAsync("/api/embeddings", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(cancellationToken: cancellationToken);
        return payload?.Embedding ?? [];
    }

    private sealed record OllamaEmbeddingResponse(float[] Embedding);
}
