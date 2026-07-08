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

try
{
    return await RunAsync(indexer, args);
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

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
            var workspaces = GetMany(options, "workspace") ?? [];
            var group = GetString(options, "group");
            if (workspaces.Count == 0 && !string.IsNullOrWhiteSpace(group))
            {
                var groupResults = await IndexGroupAsync(indexer, group, options, progress);
                Console.WriteLine(JsonSerializer.Serialize(groupResults, jsonOptions));
                return 0;
            }

            if (workspaces.Count == 0)
            {
                throw new ArgumentException("--workspace or --group is required for index.");
            }

            if (!string.IsNullOrWhiteSpace(group))
            {
                SaveLocalWorkspaceGroup(group, workspaces, GetBool(options, "add"));
            }

            var results = (await indexer.IndexTargetsAsync(
                workspaces,
                GetMany(options, "exclude"),
                GetBool(options, "force"),
                GetString(options, "profile") ?? GetString(options, "index-profile"),
                progress: progress)).ToList();

            if (results.Count == 1)
            {
                Console.WriteLine(JsonSerializer.Serialize(results[0], jsonOptions));
            }
            else
            {
                Console.WriteLine(JsonSerializer.Serialize(results, jsonOptions));
            }

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
            currentKey = NormalizeOptionName(arg[2..].Trim());
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

        if (arg.StartsWith("-", StringComparison.Ordinal) && arg.Length > 1)
        {
            currentKey = NormalizeOptionName(arg[1..].Trim());
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

static string NormalizeOptionName(string name)
    => name switch
    {
        "w" => "workspace",
        "g" => "group",
        "a" => "add",
        "p" => "profile",
        "f" => "force",
        _ => name
    };

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

static async Task<IReadOnlyList<IndexWorkspaceResult>> IndexGroupAsync(
    IWorkspaceIndexer indexer,
    string group,
    Dictionary<string, List<string>> options,
    IProgress<IndexingProgress>? progress)
{
    var localGroup = LoadLocalWorkspaceGroups()
        .FirstOrDefault(candidate => string.Equals(candidate.Name, group, StringComparison.OrdinalIgnoreCase));
    if (localGroup is not null)
    {
        return await indexer.IndexTargetsAsync(
            localGroup.Roots,
            GetMany(options, "exclude"),
            GetBool(options, "force"),
            GetString(options, "profile") ?? GetString(options, "index-profile"),
            progress: progress);
    }

    return await indexer.IndexGroupAsync(
        group,
        GetMany(options, "exclude"),
        GetBool(options, "force"),
        GetString(options, "profile") ?? GetString(options, "index-profile"),
        progress: progress);
}

static IReadOnlyList<LocalWorkspaceGroup> LoadLocalWorkspaceGroups()
{
    var path = GetLocalWorkspaceGroupsPath();
    if (!File.Exists(path))
    {
        return [];
    }

    var json = File.ReadAllText(path);
    var groups = JsonSerializer.Deserialize<LocalWorkspaceGroupStore>(json, JsonOptions())?.Groups ?? [];
    return groups
        .Where(group => !string.IsNullOrWhiteSpace(group.Name) && group.Roots.Count > 0)
        .ToArray();
}

static void SaveLocalWorkspaceGroup(string group, IReadOnlyList<string> workspaces, bool add)
{
    var roots = workspaces
        .Where(workspace => !string.IsNullOrWhiteSpace(workspace))
        .Select(Path.GetFullPath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    if (roots.Length == 0)
    {
        throw new ArgumentException("--workspace must include at least one non-empty path when assigning a group.");
    }

    var path = GetLocalWorkspaceGroupsPath();
    var currentGroups = LoadLocalWorkspaceGroups();
    var existingGroup = currentGroups.FirstOrDefault(candidate => string.Equals(candidate.Name, group, StringComparison.OrdinalIgnoreCase));
    if (add && existingGroup is not null)
    {
        roots = existingGroup.Roots
            .Concat(roots)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    var existing = currentGroups
        .Where(candidate => !string.Equals(candidate.Name, group, StringComparison.OrdinalIgnoreCase))
        .Append(new LocalWorkspaceGroup(group, roots))
        .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, JsonSerializer.Serialize(new LocalWorkspaceGroupStore(existing), JsonOptions()));
    var action = add && existingGroup is not null ? "Updated" : "Saved";
    Console.Error.WriteLine($"{action} workspace group '{group}' with {roots.Length} workspace(s) to {path}.");
}

static string GetLocalWorkspaceGroupsPath()
    => Path.Combine(Directory.GetCurrentDirectory(), ".ragnet", "indexer-workspace-groups.json");

static JsonSerializerOptions JsonOptions()
    => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

static void WriteHelp()
{
    Console.WriteLine("""
    RagNet Indexer

    Commands:
      index       --workspace|-w <target> [--workspace|-w <target> ...] [--group|-g <name>] [--add|-a] [--force] [--profile <profile>] [--exclude <dir-or-relative-path> ...]
      index       --group|-g <name> [--force] [--profile <profile>] [--exclude <dir-or-relative-path> ...]
      status      --workspace <path>

    Options:
      --profile       Index profile: all, code, docs, metadata, frontend, or tests. Default is all.
      --group, -g     With --workspace, saves/replaces a local group. Without --workspace, indexes an existing local or configured group.
      --workspace, -w Index target: workspace root, directory, solution file, or supported file. Repeat to union targets.
      --add, -a       With --workspace and --group, append targets to the existing local group instead of replacing it.
      --no-progress   Suppress progress output. Final JSON is always written to stdout.

    Examples:
      ragnet-indexer index --workspace D:\Work\Product\Api\Api.sln
      ragnet-indexer index --workspace D:\Work\Product\Api\Api.sln --workspace D:\Work\Product\Admin\Admin.sln --group my-product
      ragnet-indexer index -w D:\Work\Product\Api\Api.sln -w D:\Work\Product\docs\api
      ragnet-indexer index -w D:\Work\Product\Worker -g my-product -a
      ragnet-indexer index --group my-product --force
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

sealed record LocalWorkspaceGroupStore(IReadOnlyList<LocalWorkspaceGroup> Groups);

sealed record LocalWorkspaceGroup(string Name, IReadOnlyList<string> Roots);
