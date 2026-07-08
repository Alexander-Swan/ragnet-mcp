using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagNet.Mcp.Composition;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Embeddings;
using RagNet.Mcp.Indexing;
using RagNet.Mcp.Indexing.Interfaces;
using RagNet.Mcp.Storage.Interfaces;
using RagNet.Mcp.Workspace;
using RagNet.Mcp.Workspace.Interfaces;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

builder.Services.AddRagNetIndexingServices(builder.Configuration);

using var host = builder.Build();
var indexer = host.Services.GetRequiredService<IWorkspaceIndexer>();
var workspaceRegistry = host.Services.GetRequiredService<IIndexedWorkspaceRegistry>();
var vectorStore = host.Services.GetRequiredService<IVectorStore>();
var stateStore = host.Services.GetRequiredService<IWorkspaceIndexStateStore>();
var ragNetOptions = host.Services.GetRequiredService<IOptions<RagNetOptions>>().Value;

try
{
    return await RunAsync(indexer, workspaceRegistry, vectorStore, stateStore, ragNetOptions, args);
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
catch (EmbeddingModelNotFoundException ex)
{
    Console.Error.WriteLine($"Error: Ollama embedding model '{ex.Model}' is not installed.");
    Console.Error.WriteLine($"Ollama: {ragNetOptions.Ollama.BaseUrl}");
    Console.Error.WriteLine($"Run: ollama pull {ex.Model}");
    Console.Error.WriteLine(@"Or rerun setup: .\scripts\setup.ps1 -Mode Hybrid");
    return 1;
}
catch (HttpRequestException ex)
{
    var offlineService = GetOfflineService(ex, ragNetOptions);
    Console.Error.WriteLine($"Error: {offlineService.Name} is offline at {offlineService.BaseUrl}.");
    Console.Error.WriteLine($"Details: {GetInnermostMessage(ex)}");
    Console.Error.WriteLine(offlineService.SetupHint);
    return 1;
}

static OfflineService GetOfflineService(HttpRequestException exception, RagNetOptions options)
{
    var message = exception.ToString();
    if (MessageContainsEndpoint(message, options.Qdrant.BaseUrl))
    {
        return new OfflineService(
            "Qdrant",
            options.Qdrant.BaseUrl,
            @"Start Qdrant with: .\scripts\setup.ps1 -Mode Hybrid");
    }

    if (MessageContainsEndpoint(message, options.Ollama.BaseUrl))
    {
        return new OfflineService(
            "Ollama",
            options.Ollama.BaseUrl,
            @"Start Ollama with: .\scripts\setup.ps1 -Mode Hybrid");
    }

    return new OfflineService(
        "A required RagNet service",
        $"{options.Qdrant.BaseUrl} or {options.Ollama.BaseUrl}",
        @"Start services with: .\scripts\setup.ps1 -Mode Hybrid");
}

static bool MessageContainsEndpoint(string message, string baseUrl)
{
    if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
    {
        return message.Contains(baseUrl, StringComparison.OrdinalIgnoreCase);
    }

    var hostAndPort = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
    return message.Contains(baseUrl, StringComparison.OrdinalIgnoreCase) ||
        message.Contains(hostAndPort, StringComparison.OrdinalIgnoreCase);
}

static string GetInnermostMessage(Exception exception)
{
    while (exception.InnerException is not null)
    {
        exception = exception.InnerException;
    }

    return exception.Message;
}

static async Task<int> RunAsync(
    IWorkspaceIndexer indexer,
    IIndexedWorkspaceRegistry workspaceRegistry,
    IVectorStore vectorStore,
    IWorkspaceIndexStateStore stateStore,
    RagNetOptions ragNetOptions,
    string[] args)
{
    if (args.Length == 0 || IsHelp(args[0]))
    {
        WriteHelp();
        return args.Length == 0 ? 1 : 0;
    }

    var command = args[0].Trim().ToLowerInvariant();
    var optionStart = GetOptionStart(command, args);
    var options = ParseOptions(args.Skip(optionStart));
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
            var workspaces = await ResolveIndexWorkspaceTargetsAsync(options, workspaceRegistry);
            var group = GetString(options, "group");
            var dryRun = GetBool(options, "dry-run");
            if (workspaces.Count == 0 && !string.IsNullOrWhiteSpace(group))
            {
                if (dryRun)
                {
                    Console.WriteLine(JsonSerializer.Serialize(await DryRunIndexGroupAsync(indexer, group, options, progress), jsonOptions));
                }
                else
                {
                    Console.WriteLine(JsonSerializer.Serialize(await IndexGroupAsync(indexer, group, options, progress), jsonOptions));
                }

                return 0;
            }

            if (workspaces.Count == 0)
            {
                throw new ArgumentException("--workspace, --group, or --current is required for first indexing. If the current workspace has already been indexed, running index without a target will reindex it incrementally.");
            }

            if (!dryRun && !string.IsNullOrWhiteSpace(group))
            {
                await SaveLocalWorkspaceGroupAsync(group, workspaces, GetBool(options, "add"), workspaceRegistry);
            }

            var indexProfile = GetString(options, "profile") ?? GetString(options, "index-profile");
            var excludeDirectories = GetMany(options, "exclude");
            var force = GetBool(options, "force");
            if (dryRun)
            {
                var results = (await indexer.DryRunIndexTargetsAsync(
                    workspaces,
                    excludeDirectories,
                    force,
                    indexProfile,
                    progress: progress)).ToList();

                object output = results.Count == 1 ? results[0] : results;
                Console.WriteLine(JsonSerializer.Serialize(output, jsonOptions));
            }
            else
            {
                var results = (await indexer.IndexTargetsAsync(
                    workspaces,
                    excludeDirectories,
                    force,
                    indexProfile,
                    progress: progress)).ToList();

                object output = results.Count == 1 ? results[0] : results;
                Console.WriteLine(JsonSerializer.Serialize(output, jsonOptions));
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

        case "list":
        {
            var target = RequiredListTarget(args);
            switch (target)
            {
                case "groups":
                    WriteGroupsTable(ListWorkspaceGroups(ragNetOptions));
                    return 0;

                case "workspaces":
                    WriteWorkspacesTable(await workspaceRegistry.GetIndexedWorkspacesAsync());
                    return 0;

                default:
                    throw new ArgumentException("List target must be one of: groups, workspaces.");
            }
        }

        case "delete":
        {
            var target = RequiredListTarget(args);
            switch (target)
            {
                case "group":
                case "groups":
                    Console.WriteLine(JsonSerializer.Serialize(DeleteLocalWorkspaceGroup(GetDeleteSubject(args, options, "group"), ragNetOptions), jsonOptions));
                    return 0;

                case "workspace":
                case "workspaces":
                    Console.WriteLine(JsonSerializer.Serialize(await DeleteIndexedWorkspaceAsync(
                        GetDeleteSubject(args, options, "workspace"),
                        workspaceRegistry,
                        vectorStore,
                        stateStore), jsonOptions));
                    return 0;

                default:
                    throw new ArgumentException("Delete target must be one of: group, workspace.");
            }
        }

        case "create":
        {
            var target = RequiredListTarget(args);
            switch (target)
            {
                case "group":
                case "groups":
                    var groupName = GetCommandSubject(args, options, "group");
                    var targets = await ResolveGroupWorkspaceTargetsAsync(options, workspaceRegistry);
                    Console.WriteLine(JsonSerializer.Serialize(await SaveLocalWorkspaceGroupAsync(
                        groupName,
                        targets,
                        GetBool(options, "add"),
                        workspaceRegistry), jsonOptions));
                    return 0;

                default:
                    throw new ArgumentException("Create target must be: group.");
            }
        }

        default:
            Console.Error.WriteLine($"Unknown command '{command}'.");
            WriteHelp();
            return 1;
    }
}

static int GetOptionStart(string command, string[] args)
{
    if (command == "list")
    {
        return 2;
    }

    if (command is "delete" or "create")
    {
        return args.Length > 2 && !args[2].StartsWith("-", StringComparison.Ordinal)
            ? 3
            : 2;
    }

    return 1;
}

static string RequiredListTarget(string[] args)
{
    if (args.Length < 2 || args[1].StartsWith("-", StringComparison.Ordinal) || IsHelp(args[1]))
    {
        throw new ArgumentException("Command target is required.");
    }

    return args[1].Trim().ToLowerInvariant();
}

static string GetDeleteSubject(string[] args, Dictionary<string, List<string>> options, string optionName)
    => GetCommandSubject(args, options, optionName);

static string GetCommandSubject(string[] args, Dictionary<string, List<string>> options, string optionName)
{
    if (args.Length > 2 && !args[2].StartsWith("-", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(args[2]))
    {
        return args[2];
    }

    return Required(options, optionName);
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
        "c" => "current",
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

static async Task<IReadOnlyList<string>> ResolveIndexWorkspaceTargetsAsync(
    Dictionary<string, List<string>> options,
    IIndexedWorkspaceRegistry workspaceRegistry)
{
    var targets = (GetMany(options, "workspace") ?? []).ToList();
    var currentDirectory = NormalizePath(Directory.GetCurrentDirectory());
    if (GetBool(options, "current"))
    {
        targets.Add(currentDirectory);
    }

    if (targets.Count > 0)
    {
        return targets
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    var currentWorkspace = await TryGetIndexedWorkspaceForCurrentDirectoryAsync(workspaceRegistry);
    return currentWorkspace is null
        ? []
        : [currentWorkspace.WorkspaceRoot];
}

static async Task<IndexedWorkspaceRecord?> TryGetIndexedWorkspaceForCurrentDirectoryAsync(IIndexedWorkspaceRegistry workspaceRegistry)
{
    var currentDirectory = NormalizePath(Directory.GetCurrentDirectory());
    return (await workspaceRegistry.GetIndexedWorkspacesAsync())
        .Where(workspace => IsPathWithinWorkspace(currentDirectory, NormalizePath(workspace.WorkspaceRoot)))
        .OrderByDescending(workspace => NormalizePath(workspace.WorkspaceRoot).Length)
        .FirstOrDefault();
}

static bool IsPathWithinWorkspace(string path, string workspaceRoot)
    => string.Equals(path, workspaceRoot, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(workspaceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(workspaceRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

static async Task<IReadOnlyList<string>> ResolveGroupWorkspaceTargetsAsync(
    Dictionary<string, List<string>> options,
    IIndexedWorkspaceRegistry workspaceRegistry)
{
    var requestedTargets = (GetMany(options, "workspace") ?? []).ToList();
    if (GetBool(options, "current"))
    {
        var currentWorkspace = await TryGetIndexedWorkspaceForCurrentDirectoryAsync(workspaceRegistry)
            ?? throw new InvalidOperationException("The current directory is not inside an indexed workspace. Index it first or pass an indexed workspace name/root.");
        requestedTargets.Add(currentWorkspace.WorkspaceRoot);
    }

    if (requestedTargets.Count == 0)
    {
        throw new ArgumentException("--workspace or --current is required when creating a group from indexed workspaces.");
    }

    var indexedWorkspaces = await workspaceRegistry.GetIndexedWorkspacesAsync();
    return requestedTargets
        .Select(target => ResolveIndexedWorkspaceTarget(target, indexedWorkspaces))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static string ResolveIndexedWorkspaceTarget(string target, IReadOnlyList<IndexedWorkspaceRecord> indexedWorkspaces)
{
    var trimmed = target.Trim();
    if (string.IsNullOrWhiteSpace(trimmed))
    {
        throw new ArgumentException("Workspace target cannot be empty.");
    }

    IndexedWorkspaceRecord[] matches;
    if (!Path.IsPathFullyQualified(trimmed) &&
        trimmed.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0 &&
        !Directory.Exists(trimmed) &&
        !File.Exists(trimmed))
    {
        matches = indexedWorkspaces
            .Where(workspace => string.Equals(Path.GetFileName(NormalizePath(workspace.WorkspaceRoot)), trimmed, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
    else
    {
        var fullPath = NormalizePath(trimmed);
        matches = indexedWorkspaces
            .Where(workspace => string.Equals(NormalizePath(workspace.WorkspaceRoot), fullPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    return matches.Length switch
    {
        1 => matches[0].WorkspaceRoot,
        0 => throw new InvalidOperationException($"Indexed workspace '{trimmed}' was not found. Use 'list workspaces' to see available workspaces."),
        _ => throw new InvalidOperationException($"Workspace name '{trimmed}' matches {matches.Length} indexed workspaces. Use a full workspace root.")
    };
}

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

static async Task<IReadOnlyList<DryRunIndexWorkspaceResult>> DryRunIndexGroupAsync(
    IWorkspaceIndexer indexer,
    string group,
    Dictionary<string, List<string>> options,
    IProgress<IndexingProgress>? progress)
{
    var localGroup = LoadLocalWorkspaceGroups()
        .FirstOrDefault(candidate => string.Equals(candidate.Name, group, StringComparison.OrdinalIgnoreCase));
    if (localGroup is not null)
    {
        return await indexer.DryRunIndexTargetsAsync(
            localGroup.Roots,
            GetMany(options, "exclude"),
            GetBool(options, "force"),
            GetString(options, "profile") ?? GetString(options, "index-profile"),
            progress: progress);
    }

    return await indexer.DryRunIndexGroupAsync(
        group,
        GetMany(options, "exclude"),
        GetBool(options, "force"),
        GetString(options, "profile") ?? GetString(options, "index-profile"),
        progress: progress);
}

static IReadOnlyList<WorkspaceGroupListItem> ListWorkspaceGroups(RagNetOptions options)
{
    var configuredGroups = options.WorkspaceGroups
        .Where(group => !string.IsNullOrWhiteSpace(group.Name))
        .Select(group => new WorkspaceGroupListItem(group.Name, "configured", group.Roots, group.ExcludeDirectories));

    var localGroups = LoadLocalWorkspaceGroups()
        .Select(group => new WorkspaceGroupListItem(group.Name, "local", group.Roots, []));

    return configuredGroups
        .Concat(localGroups)
        .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(group => group.Source, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static void WriteGroupsTable(IReadOnlyList<WorkspaceGroupListItem> groups)
{
    var rows = groups
        .Select(group => new[]
        {
            group.Name,
            group.Source,
            group.Roots.Count.ToString(),
            string.Join(", ", group.Roots),
            group.ExcludeDirectories.Count == 0 ? "-" : string.Join(", ", group.ExcludeDirectories)
        })
        .ToArray();

    WriteTable(["Name", "Source", "Roots", "Targets", "Excludes"], rows);
}

static void WriteWorkspacesTable(IReadOnlyList<IndexedWorkspaceRecord> workspaces)
{
    var rows = workspaces
        .Select(workspace => new[]
        {
            Path.GetFileName(workspace.WorkspaceRoot),
            workspace.WorkspaceRoot,
            workspace.Groups.Count == 0 ? "-" : string.Join(", ", workspace.Groups),
            workspace.IndexedTargets.Count.ToString(),
            workspace.LastIndexedUtc == DateTimeOffset.MinValue ? "-" : workspace.LastIndexedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"),
            workspace.FilesScanned.ToString(),
            workspace.ChunksIndexed.ToString(),
            workspace.CollectionName
        })
        .ToArray();

    WriteTable(["Name", "Root", "Groups", "Targets", "Last Indexed", "Files", "Chunks", "Collection"], rows);
}

static void WriteTable(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
{
    if (rows.Count == 0)
    {
        Console.WriteLine("(none)");
        return;
    }

    var widths = headers
        .Select((header, index) => Math.Max(header.Length, rows.Max(row => DisplayLength(row[index]))))
        .ToArray();

    Console.WriteLine(FormatTableRow(headers, widths));
    Console.WriteLine(string.Join("  ", widths.Select(width => new string('-', width))));
    foreach (var row in rows)
    {
        Console.WriteLine(FormatTableRow(row, widths));
    }
}

static string FormatTableRow(IReadOnlyList<string> columns, IReadOnlyList<int> widths)
    => string.Join("  ", columns.Select((column, index) => (column ?? string.Empty).PadRight(widths[index])));

static int DisplayLength(string? value)
    => value?.Length ?? 0;

static DeleteWorkspaceGroupResult DeleteLocalWorkspaceGroup(string group, RagNetOptions options)
{
    var path = GetLocalWorkspaceGroupsPath();
    var currentGroups = LoadLocalWorkspaceGroups();
    var remainingGroups = currentGroups
        .Where(candidate => !string.Equals(candidate.Name, group, StringComparison.OrdinalIgnoreCase))
        .ToArray();

    if (remainingGroups.Length == currentGroups.Count)
    {
        var configuredGroupExists = options.WorkspaceGroups.Any(candidate =>
            string.Equals(candidate.Name, group, StringComparison.OrdinalIgnoreCase));
        if (configuredGroupExists)
        {
            throw new InvalidOperationException($"Workspace group '{group}' is configured in appsettings and cannot be deleted by the indexer. Edit configuration to remove it.");
        }

        throw new InvalidOperationException($"Local workspace group '{group}' was not found.");
    }

    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, JsonSerializer.Serialize(new LocalWorkspaceGroupStore(remainingGroups), JsonOptions()));
    Console.Error.WriteLine($"Deleted local workspace group '{group}' from {path}.");
    return new DeleteWorkspaceGroupResult(group, "local", path);
}

static async Task<DeleteIndexedWorkspaceResult> DeleteIndexedWorkspaceAsync(
    string workspace,
    IIndexedWorkspaceRegistry workspaceRegistry,
    IVectorStore vectorStore,
    IWorkspaceIndexStateStore stateStore)
{
    var workspaceRoot = NormalizePath(workspace);
    var record = (await workspaceRegistry.GetIndexedWorkspacesAsync())
        .FirstOrDefault(candidate => string.Equals(candidate.WorkspaceRoot, workspaceRoot, StringComparison.OrdinalIgnoreCase));
    if (record is null)
    {
        throw new InvalidOperationException($"Indexed workspace '{workspaceRoot}' was not found in the workspace registry.");
    }

    await vectorStore.DeleteWorkspaceAsync(record.WorkspaceRoot);
    await workspaceRegistry.DeleteWorkspaceAsync(record.WorkspaceRoot);
    await stateStore.DeleteAsync(record.WorkspaceRoot);
    Console.Error.WriteLine($"Deleted indexed workspace '{record.WorkspaceRoot}'.");
    return new DeleteIndexedWorkspaceResult(record.WorkspaceRoot, record.WorkspaceId, record.CollectionName);
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

static async Task<CreateWorkspaceGroupResult> SaveLocalWorkspaceGroupAsync(
    string group,
    IReadOnlyList<string> workspaces,
    bool add,
    IIndexedWorkspaceRegistry workspaceRegistry)
{
    var roots = new List<string>();
    foreach (var workspace in workspaces.Where(workspace => !string.IsNullOrWhiteSpace(workspace)))
    {
        roots.Add(await ResolveLocalWorkspaceTargetPathAsync(workspace, workspaceRegistry));
    }

    var normalizedRoots = roots
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    if (normalizedRoots.Length == 0)
    {
        throw new ArgumentException("--workspace must include at least one non-empty path when assigning a group.");
    }

    var path = GetLocalWorkspaceGroupsPath();
    var currentGroups = LoadLocalWorkspaceGroups();
    var existingGroup = currentGroups.FirstOrDefault(candidate => string.Equals(candidate.Name, group, StringComparison.OrdinalIgnoreCase));
    if (add && existingGroup is not null)
    {
        normalizedRoots = existingGroup.Roots
            .Concat(normalizedRoots)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    var existing = currentGroups
        .Where(candidate => !string.Equals(candidate.Name, group, StringComparison.OrdinalIgnoreCase))
        .Append(new LocalWorkspaceGroup(group, normalizedRoots))
        .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, JsonSerializer.Serialize(new LocalWorkspaceGroupStore(existing), JsonOptions()));
    var action = add && existingGroup is not null ? "Updated" : "Saved";
    Console.Error.WriteLine($"{action} workspace group '{group}' with {normalizedRoots.Length} workspace(s) to {path}.");
    return new CreateWorkspaceGroupResult(group, "local", path, normalizedRoots, add && existingGroup is not null);
}

static string GetLocalWorkspaceGroupsPath()
    => Path.Combine(Directory.GetCurrentDirectory(), ".ragnet", "indexer-workspace-groups.json");

static string NormalizePath(string path)
{
    var fullPath = Path.GetFullPath(path);
    var root = Path.GetPathRoot(fullPath);
    return fullPath.Length == root?.Length
        ? fullPath
        : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

static async Task<string> ResolveLocalWorkspaceTargetPathAsync(
    string workspacePath,
    IIndexedWorkspaceRegistry workspaceRegistry)
{
    var trimmed = workspacePath.Trim();
    if (!Path.IsPathFullyQualified(trimmed) &&
        trimmed.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0 &&
        !Directory.Exists(trimmed) &&
        !File.Exists(trimmed))
    {
        var matches = (await workspaceRegistry.GetIndexedWorkspacesAsync())
            .Where(workspace => string.Equals(Path.GetFileName(NormalizePath(workspace.WorkspaceRoot)), trimmed, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0].WorkspaceRoot,
            0 => throw new InvalidOperationException($"Workspace '{trimmed}' has not been indexed yet. Use a full path for the first index, then the workspace name can be used for incremental indexing."),
            _ => throw new InvalidOperationException($"Workspace name '{trimmed}' matches {matches.Length} indexed workspaces. Use a full path.")
        };
    }

    return Path.GetFullPath(trimmed);
}

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
      index       --workspace|-w <target> [--workspace|-w <target> ...] [--current|-c] [--group|-g <name>] [--add|-a] [--force] [--dry-run] [--profile <profile>] [--exclude <dir-or-relative-path> ...]
      index       --group|-g <name> [--force] [--dry-run] [--profile <profile>] [--exclude <dir-or-relative-path> ...]
      status      --workspace <path>
      create      group <name> --workspace|-w <indexed-name-or-root> [--workspace|-w <indexed-name-or-root> ...] [--current|-c] [--add|-a]
      list        groups
      list        workspaces
      delete      group <name>
      delete      workspace <workspace-root>

    Options:
      --profile       Index profile: all, code, docs, metadata, frontend, or tests. Default is all.
      --group, -g     With --workspace, saves/replaces a local group. Without --workspace, indexes an existing local or configured group.
      --workspace, -w Index target: workspace root, directory, solution file, or supported file. Repeat to union targets.
      --current, -c   Add the current directory as an index target.
      --add, -a       With --workspace and --group, append targets to the existing local group instead of replacing it.
      --dry-run       Preview files and chunks that would be indexed without writing vectors, state, registry, or local groups.
      --no-progress   Suppress progress output. Index/status/delete results are written to stdout.

    Examples:
      ragnet-indexer index --workspace D:\Work\Product\Api\Api.sln
      ragnet-indexer index --current
      ragnet-indexer index --workspace D:\Work\Product\Api\Api.sln --dry-run
      ragnet-indexer index --workspace D:\Work\Product\Api\Api.sln --workspace D:\Work\Product\Admin\Admin.sln --group my-product
      ragnet-indexer index -w D:\Work\Product\Api\Api.sln -w D:\Work\Product\docs\api
      ragnet-indexer index -w D:\Work\Product\Worker -g my-product -a
      ragnet-indexer create group my-product -w Api -w Admin
      ragnet-indexer create group my-product --current
      ragnet-indexer index --group my-product --force
      ragnet-indexer status --workspace D:\Work\Product\Api
      ragnet-indexer list groups
      ragnet-indexer list workspaces
      ragnet-indexer delete group my-product
      ragnet-indexer delete workspace D:\Work\Product\Api
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

sealed record WorkspaceGroupListItem(
    string Name,
    string Source,
    IReadOnlyList<string> Roots,
    IReadOnlyList<string> ExcludeDirectories);

sealed record DeleteWorkspaceGroupResult(string Name, string Source, string StorePath);

sealed record DeleteIndexedWorkspaceResult(string WorkspaceRoot, string WorkspaceId, string CollectionName);

sealed record CreateWorkspaceGroupResult(
    string Name,
    string Source,
    string StorePath,
    IReadOnlyList<string> Roots,
    bool Appended);

sealed record OfflineService(string Name, string BaseUrl, string SetupHint);
