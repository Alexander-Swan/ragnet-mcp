using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Indexing;

namespace RagNet.Mcp.Tests;

public sealed class QdrantWorkspaceIndexStateStoreTests
{
    [Fact]
    public async Task LoadAsync_ReturnsMissingStateWhenStateCollectionDoesNotExist()
    {
        using var handler = new FakeQdrantHandler();
        handler.Enqueue(HttpStatusCode.NotFound, "{}");
        var store = CreateStore(handler);

        var state = await store.LoadAsync(Path.Combine(Path.GetTempPath(), "missing-workspace"));

        Assert.False(state.StateExists);
        Assert.Empty(state.Files);
    }

    [Fact]
    public async Task SaveAsync_CreatesCollectionAndUpsertsWorkspaceStatePoint()
    {
        using var handler = new FakeQdrantHandler();
        handler.Enqueue(HttpStatusCode.NotFound, "{}");
        handler.Enqueue(HttpStatusCode.OK, """{"result":true}""");
        handler.Enqueue(HttpStatusCode.OK, """{"result":{"operation_id":1,"status":"completed"}}""");
        var store = CreateStore(handler);
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "ragnet-state-save");
        var filePath = Path.Combine(workspaceRoot, "src", "Program.cs");
        var savedAt = DateTimeOffset.Parse("2026-07-07T12:34:56Z");

        await store.SaveAsync(new WorkspaceIndexState(
            workspaceRoot,
            new Dictionary<string, IndexedFileState>(StringComparer.OrdinalIgnoreCase)
            {
                [filePath] = new IndexedFileState(filePath, "abc123", 42, savedAt, ChunkCount: 7)
            },
            "test-model",
            IndexSchemaVersions.CurrentText,
            savedAt,
            StateExists: true));

        Assert.Equal("GET", handler.Requests[0].Method);
        Assert.Equal("/collections/test-prefix-index-state", handler.Requests[0].Path);
        Assert.Equal("PUT", handler.Requests[1].Method);
        Assert.Equal("/collections/test-prefix-index-state", handler.Requests[1].Path);
        Assert.Equal("PUT", handler.Requests[2].Method);
        Assert.Equal("/collections/test-prefix-index-state/points", handler.Requests[2].Path);
        Assert.Equal("wait=true", handler.Requests[2].Query.TrimStart('?'));

        using var document = JsonDocument.Parse(handler.Requests[2].Body);
        var payload = document.RootElement.GetProperty("points")[0].GetProperty("payload");
        Assert.Equal(Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), payload.GetProperty("workspace_root").GetString());
        Assert.Equal("test-model", payload.GetProperty("embedding_model").GetString());
        Assert.Equal(IndexSchemaVersions.Current, payload.GetProperty("schema_version").GetInt32());
        Assert.Equal("abc123", payload.GetProperty("files")[0].GetProperty("fingerprint").GetString());
        Assert.Equal(7, payload.GetProperty("files")[0].GetProperty("chunk_count").GetInt32());
    }

    [Fact]
    public async Task LoadAsync_ReadsWorkspaceStatePoint()
    {
        using var handler = new FakeQdrantHandler();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "ragnet-state-load");
        var filePath = Path.Combine(workspaceRoot, "src", "Program.cs");
        handler.Enqueue(HttpStatusCode.OK, """{"result":{"status":"green"}}""");
        handler.Enqueue(HttpStatusCode.OK, $$"""
            {
              "result": [
                {
                  "id": "11111111-1111-5111-8111-111111111111",
                  "payload": {
                    "workspace_root": {{JsonSerializer.Serialize(workspaceRoot)}},
                    "workspace_id": "workspace-id",
                    "embedding_model": "test-model",
                    "schema_version": 2,
                    "saved_at_utc": "2026-07-07T12:34:56+00:00",
                    "files": [
                      {
                        "file_path": {{JsonSerializer.Serialize(filePath)}},
                        "fingerprint": "abc123",
                        "size": 42,
                        "last_write_time_utc": "2026-07-07T12:34:56+00:00",
                        "chunk_count": 5
                      }
                    ]
                  }
                }
              ]
            }
            """);
        var store = CreateStore(handler);

        var state = await store.LoadAsync(workspaceRoot);

        Assert.True(state.StateExists);
        Assert.Equal("test-model", state.EmbeddingModel);
        Assert.Equal(IndexSchemaVersions.CurrentText, state.SchemaVersion);
        var fileState = Assert.Single(state.Files.Values);
        Assert.Equal(Path.GetFullPath(filePath), fileState.FilePath);
        Assert.Equal("abc123", fileState.Fingerprint);
        Assert.Equal(42, fileState.Size);
        Assert.Equal(5, fileState.ChunkCount);
    }

    [Fact]
    public async Task DeleteAsync_RemovesWorkspaceStatePoint()
    {
        using var handler = new FakeQdrantHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"result":{"status":"green"}}""");
        handler.Enqueue(HttpStatusCode.OK, """{"result":{"operation_id":1,"status":"completed"}}""");
        var store = CreateStore(handler);

        await store.DeleteAsync(Path.Combine(Path.GetTempPath(), "ragnet-state-delete"));

        Assert.Equal("GET", handler.Requests[0].Method);
        Assert.Equal("POST", handler.Requests[1].Method);
        Assert.Equal("/collections/test-prefix-index-state/points/delete", handler.Requests[1].Path);
        Assert.Equal("wait=true", handler.Requests[1].Query.TrimStart('?'));
        Assert.Contains("\"points\"", handler.Requests[1].Body, StringComparison.Ordinal);
    }

    private static QdrantWorkspaceIndexStateStore CreateStore(FakeQdrantHandler handler)
    {
        var options = Options.Create(new RagNetOptions
        {
            Qdrant = new QdrantOptions
            {
                CollectionPrefix = "Test Prefix"
            }
        });

        return new QdrantWorkspaceIndexStateStore(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("http://qdrant.test/")
            },
            options,
            NullLogger<QdrantWorkspaceIndexStateStore>.Instance);
    }

    private sealed class FakeQdrantHandler : HttpMessageHandler, IDisposable
    {
        private readonly Queue<(HttpStatusCode StatusCode, string Body)> _responses = new();

        public List<CapturedRequest> Requests { get; } = [];

        public void Enqueue(HttpStatusCode statusCode, string body)
            => _responses.Enqueue((statusCode, body));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method.Method,
                request.RequestUri?.AbsolutePath ?? string.Empty,
                request.RequestUri?.Query ?? string.Empty,
                body));

            if (!_responses.TryDequeue(out var response))
            {
                throw new InvalidOperationException($"No fake response queued for {request.Method} {request.RequestUri}.");
            }

            return new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed record CapturedRequest(string Method, string Path, string Query, string Body);
}
