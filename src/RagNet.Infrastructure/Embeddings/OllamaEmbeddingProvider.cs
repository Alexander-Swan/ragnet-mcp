using System.Net.Http.Json;
using System.Net;
using Microsoft.Extensions.Options;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Embeddings;
using RagNet.Mcp.Embeddings.Interfaces;

namespace RagNet.Mcp.Embeddings;

public sealed class OllamaEmbeddingProvider(HttpClient httpClient, IOptions<RagNetOptions> options) : IEmbeddingProvider, IEmbeddingModelCatalog
{
    private readonly RagNetOptions _options = options.Value;
    private string? _selectedEmbeddingModel;

    public async Task<IReadOnlyList<EmbeddingModelInfo>> ListInstalledModelsAsync(CancellationToken cancellationToken = default)
    {
        var payload = await httpClient.GetFromJsonAsync<OllamaTagsResponse>("/api/tags", cancellationToken);
        return (payload?.Models ?? [])
            .Select(model => string.IsNullOrWhiteSpace(model.Name) ? model.Model : model.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => new EmbeddingModelInfo(name!))
            .DistinctBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<EmbeddingModelResolution> ResolveEmbeddingModelAsync(CancellationToken cancellationToken = default)
    {
        var configuredModel = _options.Ollama.EmbeddingModel;
        var installedModels = await ListInstalledModelsAsync(cancellationToken);
        if (ContainsModel(installedModels, configuredModel))
        {
            _selectedEmbeddingModel = configuredModel;
            return new EmbeddingModelResolution(configuredModel, configuredModel, UsedFallback: false, installedModels, Message: null);
        }

        var fallbackModel = _options.Ollama.FallbackEmbeddingModel;
        var fallbackAvailable = ContainsModel(installedModels, fallbackModel);
        if (_options.Ollama.AllowInstalledEmbeddingModelFallback && fallbackAvailable)
        {
            _selectedEmbeddingModel = fallbackModel;
            return new EmbeddingModelResolution(
                configuredModel,
                fallbackModel,
                UsedFallback: true,
                installedModels,
                $"Ollama embedding model '{configuredModel}' is not installed; using configured fallback '{fallbackModel}'.");
        }

        throw CreateModelNotFoundException(configuredModel, installedModels, fallbackModel, fallbackAvailable);
    }

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
            model = GetSelectedEmbeddingModel(),
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
                throw CreateModelNotFoundException(GetSelectedEmbeddingModel(), [], _options.Ollama.FallbackEmbeddingModel, fallbackAvailable: false);
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

    private string GetSelectedEmbeddingModel()
        => _selectedEmbeddingModel ?? _options.Ollama.EmbeddingModel;

    private static bool ContainsModel(IReadOnlyList<EmbeddingModelInfo> installedModels, string model)
        => installedModels.Any(installed =>
            string.Equals(installed.Name, model, StringComparison.OrdinalIgnoreCase) ||
            installed.Name.StartsWith(model + ":", StringComparison.OrdinalIgnoreCase));

    private static EmbeddingModelNotFoundException CreateModelNotFoundException(
        string model,
        IReadOnlyList<EmbeddingModelInfo> installedModels,
        string? fallbackModel,
        bool fallbackAvailable)
    {
        var installed = installedModels.Count == 0
            ? "No Ollama models are installed."
            : "Installed Ollama models: " + string.Join(", ", installedModels.Select(candidate => candidate.Name)) + ".";
        var fallback = fallbackAvailable && !string.IsNullOrWhiteSpace(fallbackModel)
            ? $" Installed fallback '{fallbackModel}' is available; enable RagNet:Ollama:AllowInstalledEmbeddingModelFallback to use it."
            : string.Empty;

        return new EmbeddingModelNotFoundException(
            model,
            $"Ollama embedding model '{model}' is not installed. Pull it with: ollama pull {model}. {installed}{fallback}",
            installedModels,
            fallbackModel,
            fallbackAvailable);
    }

    private sealed record OllamaEmbeddingResponse(IReadOnlyList<float[]> Embeddings);

    private sealed record OllamaTagsResponse(IReadOnlyList<OllamaModel> Models);

    private sealed record OllamaModel(string Name, string? Model);
}
