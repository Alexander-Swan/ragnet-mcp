using System.Text.Json;
using RagNet.Mcp.Composition;
using RagNet.Mcp.Indexing;
using RagNet.Mcp.Indexing.Interfaces;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRagNetIndexingServices(builder.Configuration);

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

app.MapGet("/", () => Results.Json(new
{
    name = "ragnet-mcp",
    description = ".NET-native MCP server for local semantic code search",
    mcpEndpoint = "/ragnet-mcp",
    health = "/health"
}));

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "ragnet-mcp",
    time = DateTimeOffset.UtcNow
}));

var indexApi = app.MapGroup("/api/index");

indexApi.MapPost("/trigger", async (
    IndexTriggerRequest request,
    IIndexingJobQueue queue,
    CancellationToken cancellationToken) =>
{
    // TODO: Require admin authentication before exposing this endpoint outside a trusted dev network.
    var response = await queue.EnqueueAsync(request, cancellationToken);
    return response.Status switch
    {
        IndexTriggerStatus.Rejected => Results.BadRequest(response),
        IndexTriggerStatus.Failed => Results.Problem(
            title: response.Message,
            detail: string.Join(Environment.NewLine, response.Warnings),
            statusCode: StatusCodes.Status500InternalServerError,
            extensions: new Dictionary<string, object?> { ["job"] = response }),
        _ => Results.Accepted($"/api/index/triggers/{response.JobId}", response)
    };
});

indexApi.MapGet("/triggers", (IIndexingJobQueue queue) => Results.Ok(queue.GetRecentEvents()));

app.MapPost("/webhooks/github", async (
    JsonElement payload,
    IIndexingJobQueue queue,
    HttpRequest httpRequest,
    CancellationToken cancellationToken) =>
{
    // TODO: Validate X-Hub-Signature-256 and map installation/repository permissions before production use.
    var request = BuildGitHubIndexTriggerRequest(payload, httpRequest.Headers["X-GitHub-Event"].ToString());
    var response = await queue.EnqueueAsync(request, cancellationToken);
    return Results.Accepted($"/api/index/triggers/{response.JobId}", response);
});

app.MapMcp("/ragnet-mcp");

app.Run();

static IndexTriggerRequest BuildGitHubIndexTriggerRequest(JsonElement payload, string eventType)
{
    var changedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var deletedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    if (payload.TryGetProperty("commits", out var commits) && commits.ValueKind == JsonValueKind.Array)
    {
        foreach (var commit in commits.EnumerateArray())
        {
            AddStringArray(commit, "added", changedFiles);
            AddStringArray(commit, "modified", changedFiles);
            AddStringArray(commit, "removed", deletedFiles);
        }
    }

    if (payload.TryGetProperty("head_commit", out var headCommit))
    {
        AddStringArray(headCommit, "added", changedFiles);
        AddStringArray(headCommit, "modified", changedFiles);
        AddStringArray(headCommit, "removed", deletedFiles);
    }

    return new IndexTriggerRequest
    {
        Provider = "github",
        EventType = string.IsNullOrWhiteSpace(eventType) ? null : eventType,
        RepositoryUrl = GetNestedString(payload, "repository", "clone_url")
            ?? GetNestedString(payload, "repository", "html_url"),
        Branch = NormalizeGitRef(GetString(payload, "ref")),
        CommitSha = GetString(payload, "after") ?? GetNestedString(payload, "head_commit", "id"),
        ChangedFiles = changedFiles.ToArray(),
        DeletedFiles = deletedFiles.ToArray()
    };
}

static void AddStringArray(JsonElement element, string propertyName, ISet<string> values)
{
    if (!element.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
    {
        return;
    }

    foreach (var item in array.EnumerateArray())
    {
        var value = item.GetString();
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(value);
        }
    }
}

static string? GetNestedString(JsonElement element, string first, string second)
    => element.TryGetProperty(first, out var nested) ? GetString(nested, second) : null;

static string? GetString(JsonElement element, string propertyName)
    => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;

static string? NormalizeGitRef(string? gitRef)
    => gitRef?.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase) == true
        ? gitRef["refs/heads/".Length..]
        : gitRef;

public partial class Program;
