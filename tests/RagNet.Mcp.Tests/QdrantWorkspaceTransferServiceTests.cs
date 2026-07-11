using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Indexing;
using RagNet.Mcp.Indexing.Interfaces;
using RagNet.Mcp.Source;
using RagNet.Mcp.Source.Interfaces;
using RagNet.Mcp.Storage;
using RagNet.Mcp.Workspace;
using RagNet.Mcp.Workspace.Interfaces;

namespace RagNet.Mcp.Tests;

public sealed class QdrantWorkspaceTransferServiceTests
{
    [Fact]
    public async Task ExportWorkspaceAsync_WritesManifestAndPointDump()
    {
        using var handler = new FakeQdrantHandler();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "ragnet-transfer-export");
        var collectionName = QdrantCollectionNaming.GetCollectionName("test", workspaceRoot);
        var registry = new FakeWorkspaceRegistry(
            new IndexedWorkspaceRecord(
                workspaceRoot,
                QdrantCollectionNaming.GetWorkspaceId(workspaceRoot),
                collectionName,
                ["product"],
                [Path.Combine(workspaceRoot, "Api.sln")],
                DateTimeOffset.Parse("2026-07-09T12:00:00Z"),
                FilesScanned: 1,
                ChunksIndexed: 1,
                FullReindex: false));
        var stateStore = new FakeStateStore
        {
            State = new WorkspaceIndexState(
                workspaceRoot,
                new Dictionary<string, IndexedFileState>(StringComparer.OrdinalIgnoreCase),
                "test-model",
                IndexSchemaVersions.CurrentText,
                DateTimeOffset.Parse("2026-07-09T12:00:00Z"),
                StateExists: true)
        };
        handler.Enqueue(HttpStatusCode.OK, $$"""
            {
              "result": {
                "points": [
                  {
                    "id": "11111111-1111-5111-8111-111111111111",
                    "vector": [0.1, 0.2],
                    "payload": {
                      "workspace_id": "{{QdrantCollectionNaming.GetWorkspaceId(workspaceRoot)}}",
                      "workspace_root": {{JsonSerializer.Serialize(workspaceRoot)}},
                      "file_path": {{JsonSerializer.Serialize(Path.Combine(workspaceRoot, "Program.cs"))}},
                      "relative_path": "Program.cs",
                      "content": "hello"
                    }
                  }
                ]
              }
            }
            """);
        handler.Enqueue(HttpStatusCode.OK, """{"result":{"config":{"params":{"vectors":{"size":2}}}}}""");
        using var output = new TemporaryDirectory();
        var service = CreateService(handler, registry, new FakeGroupRegistry(), stateStore);

        var result = await service.ExportWorkspaceAsync(workspaceRoot, output.Path);

        Assert.Equal("workspace", result.Kind);
        Assert.True(File.Exists(result.ManifestPath));
        Assert.True(File.Exists(Path.Combine(output.Path, "collections", $"{collectionName}.jsonl")));

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(result.ManifestPath));
        Assert.Equal("ragnet-qdrant-workspace-export-v1", manifest.RootElement.GetProperty("format").GetString());
        Assert.Equal("workspace", manifest.RootElement.GetProperty("kind").GetString());
        Assert.Equal("Program.cs", (await File.ReadAllTextAsync(Path.Combine(output.Path, "collections", $"{collectionName}.jsonl"))).Contains("Program.cs", StringComparison.Ordinal) ? "Program.cs" : string.Empty);
    }

    [Fact]
    public async Task ImportAsync_RenamesCollectionAndRemapsWorkspacePayloads()
    {
        using var handler = new FakeQdrantHandler();
        using var input = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(input.Path, "collections"));
        var oldRoot = Path.Combine(Path.GetTempPath(), "old-product");
        var newRoot = Path.Combine(Path.GetTempPath(), "new-product");
        var oldCollectionName = QdrantCollectionNaming.GetCollectionName("test", oldRoot);
        await File.WriteAllTextAsync(
            Path.Combine(input.Path, "ragnet-export-manifest.json"),
            $$"""
            {
              "format": "ragnet-qdrant-workspace-export-v1",
              "schemaVersion": "2",
              "exportedAtUtc": "2026-07-09T12:00:00+00:00",
              "collectionPrefix": "test",
              "kind": "workspace",
              "workspaces": [
                {
                  "record": {
                    "workspaceRoot": {{JsonSerializer.Serialize(oldRoot)}},
                    "workspaceId": "{{QdrantCollectionNaming.GetWorkspaceId(oldRoot)}}",
                    "collectionName": "{{oldCollectionName}}",
                    "groups": [],
                    "indexedTargets": [ {{JsonSerializer.Serialize(Path.Combine(oldRoot, "Api.sln"))}} ],
                    "lastIndexedUtc": "2026-07-09T12:00:00+00:00",
                    "filesScanned": 1,
                    "chunksIndexed": 1,
                    "fullReindex": false
                  },
                  "state": null,
                  "workspaceRoot": {{JsonSerializer.Serialize(oldRoot)}},
                  "repositoryRelativeWorkspaceRoot": ".",
                  "repositoryRoot": {{JsonSerializer.Serialize(oldRoot)}},
                  "indexedTargets": [],
                  "vectorSize": 2,
                  "pointsPath": "collections/{{oldCollectionName}}.jsonl",
                  "pointsExported": 1
                }
              ],
              "groups": []
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(input.Path, "collections", $"{oldCollectionName}.jsonl"),
            $$$"""
            {"id":"11111111-1111-5111-8111-111111111111","vector":[0.1,0.2],"payload":{"workspace_id":"{{{QdrantCollectionNaming.GetWorkspaceId(oldRoot)}}}","workspace_root":{{{JsonSerializer.Serialize(oldRoot)}}},"file_path":{{{JsonSerializer.Serialize(Path.Combine(oldRoot, "Program.cs"))}}},"relative_path":"Program.cs","content":"hello"}}
            """);
        handler.Enqueue(HttpStatusCode.NotFound, "{}");
        handler.Enqueue(HttpStatusCode.OK, """{"result":true}""");
        handler.Enqueue(HttpStatusCode.OK, """{"result":{"operation_id":1,"status":"completed"}}""");
        var registry = new FakeWorkspaceRegistry();
        var service = CreateService(handler, registry, new FakeGroupRegistry(), new FakeStateStore());

        var result = await service.ImportAsync(input.Path, new Dictionary<string, string>(), newRoot, expectedKind: "workspace");

        var imported = Assert.Single(result.Workspaces);
        Assert.Equal(newRoot, imported.WorkspaceRoot);
        Assert.Equal(QdrantCollectionNaming.GetCollectionName("test", newRoot), imported.CollectionName);
        var upsert = Assert.Single(handler.Requests, request => request.Method == "PUT" && request.Path.EndsWith("/points", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(upsert.Body);
        var payload = body.RootElement.GetProperty("points")[0].GetProperty("payload");
        Assert.Equal(newRoot, payload.GetProperty("workspace_root").GetString());
        Assert.Equal(Path.Combine(newRoot, "Program.cs"), payload.GetProperty("file_path").GetString());
        Assert.Equal(QdrantCollectionNaming.GetWorkspaceId(newRoot), payload.GetProperty("workspace_id").GetString());
        Assert.Equal(newRoot, Assert.Single(registry.Marked).WorkspaceRoot);
    }

    [Fact]
    public async Task RecoverWorkspaceAsync_RebuildsRegistryAndStateFromExistingCollection()
    {
        using var handler = new FakeQdrantHandler();
        using var workspace = new TemporaryDirectory();
        var filePath = Path.Combine(workspace.Path, "Program.cs");
        await File.WriteAllTextAsync(filePath, "hello");
        var collectionName = QdrantCollectionNaming.GetCollectionName("test", workspace.Path);
        handler.Enqueue(HttpStatusCode.OK, """{"result":{"status":"green"}}""");
        handler.Enqueue(HttpStatusCode.OK, $$"""
            {
              "result": {
                "points": [
                  {
                    "id": "11111111-1111-5111-8111-111111111111",
                    "payload": {
                      "workspace_root": {{JsonSerializer.Serialize(workspace.Path)}},
                      "file_path": {{JsonSerializer.Serialize(filePath)}},
                      "schema_version": 2
                    }
                  },
                  {
                    "id": "22222222-2222-5222-8222-222222222222",
                    "payload": {
                      "workspace_root": {{JsonSerializer.Serialize(workspace.Path)}},
                      "file_path": {{JsonSerializer.Serialize(filePath)}},
                      "schema_version": 2
                    }
                  }
                ]
              }
            }
            """);
        var registry = new FakeWorkspaceRegistry();
        var stateStore = new FakeStateStore();
        var service = CreateService(handler, registry, new FakeGroupRegistry(), stateStore);

        var result = await service.RecoverWorkspaceAsync(workspace.Path, [filePath], "test-model");

        Assert.Equal(collectionName, result.CollectionName);
        Assert.Equal(2, result.PointsScanned);
        Assert.Equal(1, result.FilesRecovered);
        var savedState = Assert.Single(stateStore.Saved);
        Assert.Equal("test-model", savedState.EmbeddingModel);
        Assert.Equal(IndexSchemaVersions.CurrentText, savedState.SchemaVersion);
        Assert.Equal(2, Assert.Single(savedState.Files.Values).ChunkCount);
        var record = Assert.Single(registry.Marked);
        Assert.Equal(workspace.Path, record.WorkspaceRoot);
        Assert.Equal(collectionName, record.CollectionName);
        Assert.Equal(2, record.ChunksIndexed);
        Assert.Equal(filePath, Assert.Single(record.IndexedTargets));
    }

    private static QdrantWorkspaceTransferService CreateService(
        FakeQdrantHandler handler,
        FakeWorkspaceRegistry workspaceRegistry,
        FakeGroupRegistry groupRegistry,
        FakeStateStore stateStore)
        => new(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("http://qdrant.test/")
            },
            Options.Create(new RagNetOptions
            {
                Qdrant = new QdrantOptions
                {
                    CollectionPrefix = "test"
                }
            }),
            workspaceRegistry,
            groupRegistry,
            stateStore,
            new FakeSourceIdentityResolver(),
            NullLogger<QdrantWorkspaceTransferService>.Instance);

    private sealed class FakeWorkspaceRegistry(params IndexedWorkspaceRecord[] records) : IIndexedWorkspaceRegistry
    {
        private readonly List<IndexedWorkspaceRecord> _records = [.. records];

        public List<IndexedWorkspaceRecord> Marked { get; } = [];

        public Task MarkIndexedAsync(IndexedWorkspaceRecord record, CancellationToken cancellationToken = default)
        {
            Marked.Add(record);
            _records.RemoveAll(candidate => string.Equals(candidate.WorkspaceRoot, record.WorkspaceRoot, StringComparison.OrdinalIgnoreCase));
            _records.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetIndexedWorkspaceRootsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(_records.Select(record => record.WorkspaceRoot).ToArray());

        public Task<IReadOnlyList<IndexedWorkspaceRecord>> GetIndexedWorkspacesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IndexedWorkspaceRecord>>(_records.ToArray());

        public Task DeleteWorkspaceAsync(string workspaceRoot, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeGroupRegistry(params WorkspaceGroupRecord[] groups) : IWorkspaceGroupRegistry
    {
        public List<WorkspaceGroupRecord> Saved { get; } = [];

        public Task<IReadOnlyList<WorkspaceGroupRecord>> GetGroupsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceGroupRecord>>(groups);

        public Task<WorkspaceGroupRecord?> GetGroupAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(groups.FirstOrDefault(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase)));

        public Task<WorkspaceGroupRecord> SaveGroupAsync(string name, IReadOnlyList<string> roots, IReadOnlyList<string>? excludeDirectories = null, CancellationToken cancellationToken = default)
        {
            var group = new WorkspaceGroupRecord(name, WorkspaceGroupSources.Shared, roots, excludeDirectories ?? [], IsReadOnly: false);
            Saved.Add(group);
            return Task.FromResult(group);
        }

        public Task DeleteGroupAsync(string name, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeStateStore : IWorkspaceIndexStateStore
    {
        public WorkspaceIndexState? State { get; init; }

        public List<WorkspaceIndexState> Saved { get; } = [];

        public Task<WorkspaceIndexState> LoadAsync(string workspaceRoot, CancellationToken cancellationToken = default)
            => Task.FromResult(State ?? new WorkspaceIndexState(workspaceRoot, new Dictionary<string, IndexedFileState>(StringComparer.OrdinalIgnoreCase), null, null, null, StateExists: false));

        public Task SaveAsync(WorkspaceIndexState state, CancellationToken cancellationToken = default)
        {
            Saved.Add(state);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string workspaceRoot, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeSourceIdentityResolver : ISourceIdentityResolver
    {
        public Task<SourceIdentity> ResolveAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default)
            => Task.FromResult(SourceIdentity.Local(workspaceRoot, filePath));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ragnet-transfer-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
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
