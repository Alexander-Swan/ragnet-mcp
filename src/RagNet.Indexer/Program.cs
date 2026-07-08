using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RagNet.Mcp.Composition;
using RagNet.Mcp.Indexing;
using RagNet.Mcp.Indexing.Interfaces;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

builder.Services.AddRagNetIndexingServices(builder.Configuration);

using var host = builder.Build();
var indexer = host.Services.GetRequiredService<IWorkspaceIndexer>();

return await RunAsync(indexer, args);

static async Task<int> RunAsync(IWorkspaceIndexer indexer, string[] args)
{
    if (args.Length == 0 || IsHelp(args[0]))
    {
        WriteHelp();
        return args.Length == 0 ? 1 : 0;
    }

    var command = args[0].Trim().ToLowerInvariant();
    var options = ParseOptions(args.Skip(1));
    var progress = GetBool(options, "no-progress")
        ? null
        : new Progress<IndexingProgress>(WriteProgress);
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    switch (command)
    {
        case "index":
        {
            var workspace = Required(options, "workspace");
            var result = await indexer.IndexAsync(
                workspace,
                GetMany(options, "exclude"),
                GetBool(options, "force"),
                GetString(options, "profile") ?? GetString(options, "index-profile"),
                progress: progress);
            Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
            return 0;
        }

        case "index-group":
        {
            var group = Required(options, "group");
            var result = await indexer.IndexGroupAsync(
                group,
                GetMany(options, "exclude"),
                GetBool(options, "force"),
                GetString(options, "profile") ?? GetString(options, "index-profile"),
                progress: progress);
            Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
            return 0;
        }

        case "status":
        {
            var workspace = Required(options, "workspace");
            var result = await indexer.GetStatusAsync(workspace);
            Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
            return 0;
        }

        default:
            Console.Error.WriteLine($"Unknown command '{command}'.");
            WriteHelp();
            return 1;
    }
}

static Dictionary<string, List<string>> ParseOptions(IEnumerable<string> args)
{
    var parsed = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    string? currentKey = null;

    foreach (var arg in args)
    {
        if (arg.StartsWith("--", StringComparison.Ordinal))
        {
            currentKey = arg[2..].Trim();
            if (currentKey.Length == 0)
            {
                throw new ArgumentException("Empty option name is not valid.");
            }

            if (!parsed.ContainsKey(currentKey))
            {
                parsed[currentKey] = [];
            }

            continue;
        }

        if (currentKey is null)
        {
            throw new ArgumentException($"Unexpected positional argument '{arg}'.");
        }

        parsed[currentKey].Add(arg);
    }

    return parsed;
}

static string Required(Dictionary<string, List<string>> options, string name)
    => options.TryGetValue(name, out var values) && values.Count > 0 && !string.IsNullOrWhiteSpace(values[^1])
        ? values[^1]
        : throw new ArgumentException($"--{name} is required.");

static IReadOnlyList<string>? GetMany(Dictionary<string, List<string>> options, string name)
    => options.TryGetValue(name, out var values) && values.Count > 0
        ? values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()
        : null;

static string? GetString(Dictionary<string, List<string>> options, string name)
    => options.TryGetValue(name, out var values) && values.Count > 0 && !string.IsNullOrWhiteSpace(values[^1])
        ? values[^1]
        : null;

static bool GetBool(Dictionary<string, List<string>> options, string name)
    => options.ContainsKey(name) &&
        (options[name].Count == 0 || bool.Parse(options[name][^1]));

static bool IsHelp(string value)
    => value is "-h" or "--help" or "help";

static void WriteHelp()
{
    Console.WriteLine("""
    RagNet Indexer

    Commands:
      index       --workspace <path> [--force] [--profile <profile>] [--exclude <dir-or-relative-path> ...]
      index-group --group <name>     [--force] [--profile <profile>] [--exclude <dir-or-relative-path> ...]
      status      --workspace <path>

    Options:
      --profile       Index profile: all, code, docs, metadata, frontend, or tests. Default is all.
      --no-progress   Suppress progress output. Final JSON is always written to stdout.

    Examples:
      ragnet-indexer index --workspace D:\Work\Product\Api
      ragnet-indexer index --workspace D:\Work\Product\Api --force
      ragnet-indexer index-group --group my-product
      ragnet-indexer status --workspace D:\Work\Product\Api
    """);
}

static void WriteProgress(IndexingProgress progress)
{
    var count = progress.Total.HasValue
        ? $"{progress.Current}/{progress.Total.Value}"
        : progress.Current.ToString();
    var workspaceName = Path.GetFileName(progress.WorkspaceRoot);
    if (string.IsNullOrWhiteSpace(workspaceName))
    {
        workspaceName = progress.WorkspaceRoot;
    }

    Console.Error.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} [{workspaceName}] {progress.Stage}: {count} - {progress.Message}");
}
