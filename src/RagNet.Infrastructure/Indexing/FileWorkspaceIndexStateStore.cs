using System.Text.Json;
using RagNet.Mcp.Indexing.Interfaces;

namespace RagNet.Mcp.Indexing;

public sealed class FileWorkspaceIndexStateStore : IWorkspaceIndexStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<WorkspaceIndexState> LoadAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        var path = GetStatePath(workspaceRoot);
        if (!File.Exists(path))
        {
            return Empty(workspaceRoot);
        }

        await using var stream = File.OpenRead(path);
        var state = await JsonSerializer.DeserializeAsync<PersistedWorkspaceIndexState>(stream, JsonOptions, cancellationToken);
        var files = state?.Files?
            .Where(file => !string.IsNullOrWhiteSpace(file.FilePath))
            .ToDictionary(file => file.FilePath, file => new IndexedFileState(
                Path.GetFullPath(file.FilePath),
                file.Fingerprint,
                file.Size,
                file.LastWriteTimeUtc,
                file.ChunkCount), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, IndexedFileState>(StringComparer.OrdinalIgnoreCase);

        return new WorkspaceIndexState(
            workspaceRoot,
            files,
            state?.EmbeddingModel,
            state?.SchemaVersion is { } schemaVersion
                ? IndexSchemaVersions.ReadVersion(schemaVersion)
                : null,
            state?.SavedAtUtc,
            true);
    }

    public async Task SaveAsync(WorkspaceIndexState state, CancellationToken cancellationToken = default)
    {
        var path = GetStatePath(state.WorkspaceRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var payload = new
        {
            workspaceRoot = state.WorkspaceRoot,
            embeddingModel = state.EmbeddingModel,
            schemaVersion = IndexSchemaVersions.Current,
            savedAtUtc = state.SavedAtUtc,
            files = state.Files.Values.OrderBy(file => file.FilePath, StringComparer.OrdinalIgnoreCase).ToArray()
        };

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken);
    }

    public Task DeleteAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        var path = GetStatePath(workspaceRoot);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private static string GetStatePath(string workspaceRoot)
        => Path.Combine(workspaceRoot, ".ragnet", "state.json");

    private static WorkspaceIndexState Empty(string workspaceRoot)
        => new(
            workspaceRoot,
            new Dictionary<string, IndexedFileState>(StringComparer.OrdinalIgnoreCase),
            EmbeddingModel: null,
            SchemaVersion: null,
            SavedAtUtc: null,
            StateExists: false);

    private sealed record PersistedWorkspaceIndexState(
        string WorkspaceRoot,
        string? EmbeddingModel,
        JsonElement? SchemaVersion,
        DateTimeOffset? SavedAtUtc,
        IReadOnlyList<IndexedFileState> Files);
}
