using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Indexing;
using RagNet.Mcp.Workspace;

namespace RagNet.Mcp.Tests;

public sealed class QdrantWorkspaceGroupRegistryTests
{
    [Fact]
    public async Task SaveGroupAsync_CreatesCollectionAndUpsertsSharedGroup()
    {
        using var handler = new FakeQdrantHandler();
        handler.Enqueue(HttpStatusCode.NotFound, "{}");
        handler.Enqueue(HttpStatusCode.OK, """{"result":true}""");
        handler.Enqueue(HttpStatusCode.OK, """{"result":{"operation_id":1,"status":"completed"}}""");
        var registry = CreateRegistry(handler);
        var root = Path.Combine(Path.GetTempPath(), "ragnet-shared-group");

        var group = await registry.SaveGroupAsync(" Team ", [root], ["bin"]);

        Assert.Equal("Team", group.Name);
        Assert.Equal(WorkspaceGroupSources.Shared, group.Source);
        Assert.False(group.IsReadOnly);
        Assert.Equal("GET", handler.Requests[0].Method);
        Assert.Equal("/collections/test-prefix-workspace-groups", handler.Requests[0].Path);
        Assert.Equal("PUT", handler.Requests[1].Method);
        Assert.Equal("/collections/test-prefix-workspace-groups", handler.Requests[1].Path);
        Assert.Equal("PUT", handler.Requests[2].Method);
        Assert.Equal("/collections/test-prefix-workspace-groups/points", handler.Requests[2].Path);
        Assert.Equal("wait=true", handler.Requests[2].Query.TrimStart('?'));

        using var document = JsonDocument.Parse(handler.Requests[2].Body);
        var payload = document.RootElement.GetProperty("points")[0].GetProperty("payload");
        Assert.Equal("Team", payload.GetProperty("group_name").GetString());
        Assert.Equal(IndexSchemaVersions.Current, payload.GetProperty("schema_version").GetInt32());
        Assert.Equal("shared", payload.GetProperty("source").GetString());
        Assert.Equal("bin", payload.GetProperty("exclude_directories")[0].GetString());
        Assert.Equal(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), payload.GetProperty("roots")[0].GetString());
    }

    [Fact]
    public async Task GetGroupsAsync_MergesSharedConfiguredAndLocalWithLocalOverride()
    {
        using var handler = new FakeQdrantHandler();
        using var currentDirectory = new TemporaryCurrentDirectory();
        Directory.CreateDirectory(Path.Combine(currentDirectory.Path, ".ragnet"));
        await File.WriteAllTextAsync(
            Path.Combine(currentDirectory.Path, ".ragnet", "indexer-workspace-groups.json"),
            """
            {
              "groups": [
                {
                  "name": "Team",
                  "roots": [ "local-root" ]
                }
              ]
            }
            """);
        handler.Enqueue(HttpStatusCode.OK, """{"result":{"status":"green"}}""");
        handler.Enqueue(HttpStatusCode.OK, $$"""
            {
              "result": {
                "points": [
                  {
                    "payload": {
                      "group_name": "Team",
                      "roots": [ {{JsonSerializer.Serialize(Path.Combine(Path.GetTempPath(), "shared-root"))}} ],
                      "exclude_directories": [ "obj" ]
                    }
                  },
                  {
                    "payload": {
                      "group_name": "SharedOnly",
                      "roots": [ {{JsonSerializer.Serialize(Path.Combine(Path.GetTempPath(), "shared-only"))}} ],
                      "exclude_directories": []
                    }
                  }
                ]
              }
            }
            """);
        var registry = CreateRegistry(handler, new RagNetOptions
        {
            Qdrant = new QdrantOptions { CollectionPrefix = "Test Prefix" },
            WorkspaceGroups =
            [
                new WorkspaceGroupOptions
                {
                    Name = "ConfiguredOnly",
                    Roots = [Path.Combine(Path.GetTempPath(), "configured-root")],
                    ExcludeDirectories = ["artifacts"]
                }
            ]
        });

        var groups = await registry.GetGroupsAsync();

        var team = Assert.Single(groups, group => group.Name == "Team");
        Assert.Equal(WorkspaceGroupSources.Local, team.Source);
        Assert.Equal(Path.GetFullPath("local-root"), Assert.Single(team.Roots));

        var configured = Assert.Single(groups, group => group.Name == "ConfiguredOnly");
        Assert.Equal(WorkspaceGroupSources.Configured, configured.Source);
        Assert.True(configured.IsReadOnly);
        Assert.Equal(["artifacts"], configured.ExcludeDirectories);

        var sharedOnly = Assert.Single(groups, group => group.Name == "SharedOnly");
        Assert.Equal(WorkspaceGroupSources.Shared, sharedOnly.Source);
    }

    [Fact]
    public async Task SaveAndDeleteRejectConfiguredReadOnlyGroup()
    {
        using var handler = new FakeQdrantHandler();
        var registry = CreateRegistry(handler, new RagNetOptions
        {
            WorkspaceGroups =
            [
                new WorkspaceGroupOptions
                {
                    Name = "Configured",
                    Roots = [Path.GetTempPath()]
                }
            ]
        });

        var saveException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.SaveGroupAsync("configured", [Path.GetTempPath()]));
        var deleteException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.DeleteGroupAsync("CONFIGURED"));

        Assert.Contains("read-only", saveException.Message);
        Assert.Contains("read-only", deleteException.Message);
        Assert.Empty(handler.Requests);
    }

    private static QdrantWorkspaceGroupRegistry CreateRegistry(
        FakeQdrantHandler handler,
        RagNetOptions? options = null)
        => new(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("http://qdrant.test/")
            },
            Options.Create(options ?? new RagNetOptions
            {
                Qdrant = new QdrantOptions
                {
                    CollectionPrefix = "Test Prefix"
                }
            }),
            NullLogger<QdrantWorkspaceGroupRegistry>.Instance);

    private sealed class TemporaryCurrentDirectory : IDisposable
    {
        private readonly string _previousDirectory = Directory.GetCurrentDirectory();

        public TemporaryCurrentDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ragnet-group-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.SetCurrentDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.SetCurrentDirectory(_previousDirectory);
            Directory.Delete(Path, recursive: true);
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
