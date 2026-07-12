using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Indexing;
using RagNet.Mcp.Storage;
using RagNet.Mcp.Workspace;

namespace RagNet.Mcp.Tests;

public sealed class QdrantIndexedWorkspaceRegistryTests
{
    [Fact]
    public async Task MarkIndexedAsync_PersistsCalculatedDisplayNameAndAliases()
    {
        using var handler = new FakeQdrantHandler();
        handler.Enqueue(HttpStatusCode.NotFound, "{}");
        handler.Enqueue(HttpStatusCode.OK, """{"result":true}""");
        handler.Enqueue(HttpStatusCode.OK, """{"result":{"operation_id":1,"status":"completed"}}""");
        var registry = CreateRegistry(handler);
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "ragnet-alias-product");
        var solution = Path.Combine(workspaceRoot, "Api.sln");

        await registry.MarkIndexedAsync(new IndexedWorkspaceRecord(
            workspaceRoot,
            QdrantCollectionNaming.GetWorkspaceId(workspaceRoot),
            QdrantCollectionNaming.GetCollectionName("test-prefix", workspaceRoot),
            [],
            [solution],
            DateTimeOffset.Parse("2026-07-09T12:00:00Z"),
            FilesScanned: 1,
            ChunksIndexed: 1,
            FullReindex: false));

        using var document = JsonDocument.Parse(handler.Requests[2].Body);
        var payload = document.RootElement.GetProperty("points")[0].GetProperty("payload");
        Assert.Equal("ragnet-alias-product", payload.GetProperty("display_name").GetString());
        var aliases = payload.GetProperty("aliases")
            .EnumerateArray()
            .Select(alias => alias.GetString())
            .ToArray();
        Assert.Contains("ragnet-alias-product", aliases);
        Assert.Contains("Api.sln", aliases);
        Assert.Contains("Api", aliases);
        Assert.Equal(IndexSchemaVersions.Current, payload.GetProperty("schema_version").GetInt32());
        Assert.Equal(IndexedWorkspaceStatuses.Completed, payload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetIndexedWorkspacesAsync_LoadsOlderRegistrySchemaRecords()
    {
        using var handler = new FakeQdrantHandler();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "ragnet-v1-registry");
        var collectionName = QdrantCollectionNaming.GetCollectionName("test-prefix", workspaceRoot);
        handler.Enqueue(HttpStatusCode.OK, """{"result":{"status":"green"}}""");
        handler.Enqueue(HttpStatusCode.OK, $$"""
            {
              "result": {
                "points": [
                  {
                    "payload": {
                      "workspace_root": {{JsonSerializer.Serialize(workspaceRoot)}},
                      "workspace_id": "{{QdrantCollectionNaming.GetWorkspaceId(workspaceRoot)}}",
                      "schema_version": 1,
                      "collection_name": "{{collectionName}}",
                      "groups": [],
                      "indexed_targets": [ {{JsonSerializer.Serialize(Path.Combine(workspaceRoot, "Api.sln"))}} ],
                      "last_indexed_utc": "2026-07-09T12:00:00+00:00",
                      "files_scanned": 1,
                      "chunks_indexed": 2,
                      "full_reindex": true,
                      "status": "indexing"
                    }
                  }
                ]
              }
            }
            """);
        var registry = CreateRegistry(handler);

        var record = Assert.Single(await registry.GetIndexedWorkspacesAsync());

        Assert.Equal(Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), record.WorkspaceRoot);
        Assert.Equal(collectionName, record.CollectionName);
        Assert.Equal(2, record.ChunksIndexed);
        Assert.Equal(IndexedWorkspaceStatuses.Indexing, record.Status);
    }

    private static QdrantIndexedWorkspaceRegistry CreateRegistry(FakeQdrantHandler handler)
        => new(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("http://qdrant.test/")
            },
            Options.Create(new RagNetOptions
            {
                Qdrant = new QdrantOptions
                {
                    CollectionPrefix = "Test Prefix"
                }
            }),
            NullLogger<QdrantIndexedWorkspaceRegistry>.Instance);

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
