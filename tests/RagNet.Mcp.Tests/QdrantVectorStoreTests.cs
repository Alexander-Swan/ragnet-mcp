using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Indexing;
using RagNet.Mcp.Storage;

namespace RagNet.Mcp.Tests;

public sealed class QdrantVectorStoreTests
{
    [Fact]
    public async Task UpsertAsync_SplitsPointsIntoConfiguredBatches()
    {
        using var handler = new FakeQdrantHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"result":{"config":{"params":{"vectors":{"size":2}}}}}""");
        handler.Enqueue(HttpStatusCode.OK, """{"result":{"operation_id":1,"status":"completed"}}""");
        handler.Enqueue(HttpStatusCode.OK, """{"result":{"operation_id":2,"status":"completed"}}""");
        var store = CreateStore(handler, upsertBatchSize: 2);
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"ragnet-qdrant-vector-tests-{Guid.NewGuid():N}");

        await store.UpsertAsync(
            workspaceRoot,
            [
                CreateChunk(workspaceRoot, "src/First.cs", "first"),
                CreateChunk(workspaceRoot, "src/Second.cs", "second"),
                CreateChunk(workspaceRoot, "src/Third.cs", "third")
            ],
            [
                [1f, 0f],
                [0f, 1f],
                [1f, 1f]
            ]);

        Assert.Equal("GET", handler.Requests[0].Method);
        var upsertRequests = handler.Requests
            .Where(request => request.Method == "PUT" && request.Path.EndsWith("/points", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(2, upsertRequests.Length);
        Assert.Equal([2, 1], upsertRequests.Select(CountPoints).ToArray());

        using var document = JsonDocument.Parse(upsertRequests[0].Body);
        var payload = document.RootElement.GetProperty("points")[0].GetProperty("payload");
        Assert.Equal(IndexSchemaVersions.Current, payload.GetProperty("schema_version").GetInt32());
    }

    [Fact]
    public async Task SearchAsync_RejectsUnsupportedStoredSchemaVersion()
    {
        using var handler = new FakeQdrantHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"result":{"status":"green"}}""");
        handler.Enqueue(HttpStatusCode.OK, """
            {
              "result": [
                {
                  "score": 0.8,
                  "payload": {
                    "schema_version": "999",
                    "file_path": "/repo/src/Program.cs",
                    "symbol_name": "Program",
                    "symbol_kind": "file",
                    "start_line": 1,
                    "end_line": 2,
                    "preview": "preview",
                    "content": "content",
                    "content_type": "code",
                    "index_profile": "code",
                    "language": "csharp"
                  }
                }
              ]
            }
            """);
        var store = CreateStore(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SearchAsync(Path.GetTempPath(), [1f, 0f], "program", 10, hybrid: false));

        Assert.Contains("schema version '999'", exception.Message);
        Assert.Contains(IndexSchemaVersions.Current.ToString(), exception.Message);
    }

    [Fact]
    public async Task UpsertAsync_VectorSizeMismatchExplainsFullWorkspaceForceReindex()
    {
        using var handler = new FakeQdrantHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"result":{"config":{"params":{"vectors":{"size":3}}}}}""");
        var store = CreateStore(handler);
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"ragnet-qdrant-vector-tests-{Guid.NewGuid():N}");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.UpsertAsync(
                workspaceRoot,
                [CreateChunk(workspaceRoot, "src/Program.cs", "code")],
                [[1f, 0f]]));

        Assert.Contains("full workspace reindex", exception.Message);
        Assert.Contains("index -c --force", exception.Message);
        Assert.Contains("solution, subfolder, file, group member, or profile-scoped", exception.Message);
    }

    [Fact]
    public async Task DeleteByFilesAsync_DeletesDistinctFilesWithSingleFilterRequest()
    {
        using var handler = new FakeQdrantHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"result":{"status":"green"}}""");
        handler.Enqueue(HttpStatusCode.OK, """{"result":{"operation_id":1,"status":"completed"}}""");
        var store = CreateStore(handler);
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"ragnet-qdrant-vector-tests-{Guid.NewGuid():N}");
        var firstFile = Path.Combine(workspaceRoot, "src", "Program.cs");
        var secondFile = Path.Combine(workspaceRoot, "docs", "readme.md");

        await store.DeleteByFilesAsync(
            workspaceRoot,
            [
                firstFile,
                secondFile,
                firstFile
            ]);

        Assert.Equal("GET", handler.Requests[0].Method);
        Assert.Equal("POST", handler.Requests[1].Method);
        Assert.EndsWith("/points/delete", handler.Requests[1].Path);
        Assert.Equal("wait=true", handler.Requests[1].Query.TrimStart('?'));

        using var document = JsonDocument.Parse(handler.Requests[1].Body);
        var filter = document.RootElement.GetProperty("filter");
        var must = filter.GetProperty("must").EnumerateArray().ToArray();
        var should = filter.GetProperty("should").EnumerateArray().ToArray();

        var workspaceMatch = Assert.Single(must);
        Assert.Equal("workspace_id", workspaceMatch.GetProperty("key").GetString());
        Assert.Equal(QdrantCollectionNaming.GetWorkspaceId(workspaceRoot), workspaceMatch.GetProperty("match").GetProperty("value").GetString());

        Assert.Equal(2, should.Length);
        Assert.All(should, condition => Assert.Equal("file_path", condition.GetProperty("key").GetString()));
        Assert.Equal(
            [
                Path.GetFullPath(firstFile),
                Path.GetFullPath(secondFile)
            ],
            should.Select(condition => condition.GetProperty("match").GetProperty("value").GetString() ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task DeleteCollectionAsync_DeletesExplicitCollectionName()
    {
        using var handler = new FakeQdrantHandler();
        handler.Enqueue(HttpStatusCode.OK, "{}");
        var store = CreateStore(handler);

        await store.DeleteCollectionAsync("ragnet-stage-test");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("DELETE", request.Method);
        Assert.Equal("/collections/ragnet-stage-test", request.Path);
    }

    private static QdrantVectorStore CreateStore(FakeQdrantHandler handler, int upsertBatchSize = 256)
    {
        var options = Options.Create(new RagNetOptions
        {
            Qdrant = new QdrantOptions
            {
                CollectionPrefix = "Test Prefix",
                UpsertBatchSize = upsertBatchSize
            }
        });

        return new QdrantVectorStore(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("http://qdrant.test/")
            },
            options,
            NullLogger<QdrantVectorStore>.Instance);
    }

    private static CodeChunk CreateChunk(string workspaceRoot, string relativePath, string content)
        => new(
            Id: relativePath,
            WorkspaceRoot: workspaceRoot,
            FilePath: Path.Combine(workspaceRoot, relativePath),
            Language: "csharp",
            SymbolName: Path.GetFileNameWithoutExtension(relativePath),
            SymbolKind: "file",
            StartLine: 1,
            EndLine: 1,
            Content: content);

    private static int CountPoints(CapturedRequest request)
    {
        using var document = JsonDocument.Parse(request.Body);
        return document.RootElement.GetProperty("points").GetArrayLength();
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
