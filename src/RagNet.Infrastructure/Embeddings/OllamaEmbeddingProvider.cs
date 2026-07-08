using System.Net.Http.Json;
using System.Net;
using Microsoft.Extensions.Options;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Embeddings;
using RagNet.Mcp.Embeddings.Interfaces;

namespace RagNet.Mcp.Embeddings;

public sealed class OllamaEmbeddingProvider(HttpClient httpClient, IOptions<RagNetOptions> options) : IEmbeddingProvider
{
    private readonly RagNetOptions _options = options.Value;

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        => (await EmbedBatchAsync([text], cancellationToken))[0];

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0)
        {
            return [];
        }

        var request = new
        {
            model = _options.Ollama.EmbeddingModel,
            input = texts
        };

        using var response = await httpClient.PostAsJsonAsync("/api/embed", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound &&
                body.Contains("model", StringComparison.OrdinalIgnoreCase) &&
                body.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                throw new EmbeddingModelNotFoundException(
                    _options.Ollama.EmbeddingModel,
                    $"Ollama embedding model '{_options.Ollama.EmbeddingModel}' is not installed. Pull it with: ollama pull {_options.Ollama.EmbeddingModel}");
            }

            throw new HttpRequestException(
                $"Ollama embedding request failed with {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
                inner: null,
                response.StatusCode);
        }

        var payload = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(cancellationToken: cancellationToken);
        var embeddings = payload?.Embeddings ?? [];
        if (embeddings.Count != texts.Count)
        {
            throw new InvalidOperationException($"Ollama returned {embeddings.Count} embedding(s) for {texts.Count} input(s).");
        }

        return embeddings;
    }

    private sealed record OllamaEmbeddingResponse(IReadOnlyList<float[]> Embeddings);
}
