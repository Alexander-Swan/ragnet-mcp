using System.Net;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagNet.Mcp.Composition;
using RagNet.Mcp.Configuration;
using RagNet.Mcp.Embeddings;
using RagNet.Mcp.Indexing.Evaluation;
using RagNet.Mcp.Indexing;
using RagNet.Mcp.Indexing.Interfaces;
using RagNet.Mcp.Storage;
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
var workspaceGroupRegistry = host.Services.GetRequiredService<IWorkspaceGroupRegistry>();
var vectorStore = host.Services.GetRequiredService<IVectorStore>();
var stateStore = host.Services.GetRequiredService<IWorkspaceIndexStateStore>();
var searchEvaluationService = host.Services.GetRequiredService<ISearchEvaluationService>();
var transferService = host.Services.GetRequiredService<IWorkspaceTransferService>();
var ragNetOptions = host.Services.GetRequiredService<IOptions<RagNetOptions>>().Value;

try
{
    return await RunAsync(indexer, workspaceRegistry, workspaceGroupRegistry, vectorStore, stateStore, searchEvaluationService, transferService, ragNetOptions, args);
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
    IWorkspaceGroupRegistry workspaceGroupRegistry,
    IVectorStore vectorStore,
    IWorkspaceIndexStateStore stateStore,
    ISearchEvaluationService searchEvaluationService,
    IWorkspaceTransferService transferService,
    RagNetOptions ragNetOptions,
    string[] args)
{
    if (args.Length == 0 || IsHelp(args[0]))
    {
        WriteHelp();
        return args.Length == 0 ? 1 : 0;
    }

    if (TryWriteScopedHelp(args))
    {
        return 0;
    }

    var command = args[0].Trim().ToLowerInvariant();
    var optionStart = GetOptionStart(command, args);
    var options = ParseOptions(args.Skip(optionStart));
    var progress = GetBool(options, "no-progress")
        ? null
        : new Progress<IndexingProgress>(ProgressConsole.Write);
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
                await SaveWorkspaceGroupAsync(group, workspaces, GetBool(options, "add"), workspaceRegistry, workspaceGroupRegistry);
            }

            var indexProfile = GetString(options, "profile") ?? GetString(options, "index-profile");
            var excludeDirectories = GetManyDelimited(options, "exclude");
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

        case "eval":
        {
            var evalWorkspaceRoot = GetString(options, "workspace-root") ?? GetString(options, "workspace");
            if (!string.IsNullOrWhiteSpace(evalWorkspaceRoot))
            {
                evalWorkspaceRoot = await ResolveLocalWorkspaceTargetPathAsync(evalWorkspaceRoot, workspaceRegistry);
            }

            var result = await searchEvaluationService.RunAsync(new SearchEvaluationRequest(
                Required(options, "queries"),
                GetInt(options, "limit"),
                GetNullableBool(options, "hybrid"),
                GetString(options, "file-path"),
                GetString(options, "scope"),
                evalWorkspaceRoot,
                GetString(options, "workspace-group") ?? GetString(options, "group"),
                GetString(options, "content-type"),
                GetString(options, "retrieval-mode"),
                GetString(options, "search-profile") ?? GetString(options, "profile")));
            Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
            return result.Metrics.PassedQueries == result.Metrics.TotalQueries ? 0 : 2;
        }

        case "workspace":
        {
            var action = RequiredSubcommand(args, "workspace");
            switch (action)
            {
                case "list":
                    WriteWorkspacesTable(await workspaceRegistry.GetIndexedWorkspacesAsync());
                    return 0;

                case "status":
                {
                    var workspace = await ResolveLocalWorkspaceTargetPathAsync(GetWorkspaceCommandSubject(args, options, "workspace"), workspaceRegistry);
                    var workspaceStatus = await indexer.GetStatusAsync(workspace);
                    Console.WriteLine(JsonSerializer.Serialize(workspaceStatus, jsonOptions));
                    return 0;
                }

                case "delete":
                    Console.WriteLine(JsonSerializer.Serialize(await DeleteIndexedWorkspaceAsync(
                        GetWorkspaceCommandSubject(args, options, "workspace"),
                        workspaceRegistry,
                        vectorStore,
                        stateStore,
                        ragNetOptions), jsonOptions));
                    return 0;

                case "export":
                {
                    var output = Required(options, "output");
                    Console.WriteLine(JsonSerializer.Serialize(await transferService.ExportWorkspaceAsync(
                        GetWorkspaceCommandSubject(args, options, "workspace"),
                        output), jsonOptions));
                    return 0;
                }

                case "import":
                    Console.WriteLine(JsonSerializer.Serialize(await transferService.ImportAsync(
                        Required(options, "input"),
                        ParsePathMap(GetMany(options, "path-map")),
                        GetString(options, "workspace-root") ?? GetString(options, "root"),
                        expectedKind: "workspace"), jsonOptions));
                    return 0;

                case "migrate":
                {
                    Console.WriteLine(JsonSerializer.Serialize(await transferService.MigrateAsync(), jsonOptions));
                    return 0;
                }

                case "adopt":
                {
                    var workspaceRoot = GetString(options, "workspace-root") ??
                        GetString(options, "root") ??
                        GetString(options, "workspace") ??
                        (args.Length > 2 && !args[2].StartsWith("-", StringComparison.Ordinal) && !IsHelp(args[2])
                            ? args[2]
                            : null) ??
                        throw new ArgumentException("workspace adopt requires a workspace root.");
                    Console.WriteLine(JsonSerializer.Serialize(await transferService.RecoverWorkspaceAsync(
                        workspaceRoot,
                        GetMany(options, "target"),
                        GetString(options, "embedding-model")), jsonOptions));
                    return 0;
                }

                case "collection":
                {
                    var positionalTarget =
                        (args.Length > 2 && !args[2].StartsWith("-", StringComparison.Ordinal) && !IsHelp(args[2])
                            ? args[2]
                            : null);
                    var workspaceTarget = GetString(options, "workspace");
                    var pathTarget = GetString(options, "path") ?? GetString(options, "file");
                    if (!string.IsNullOrWhiteSpace(positionalTarget) &&
                        string.IsNullOrWhiteSpace(workspaceTarget) &&
                        string.IsNullOrWhiteSpace(pathTarget) &&
                        string.IsNullOrWhiteSpace(GetString(options, "group")))
                    {
                        if (Path.Exists(positionalTarget))
                        {
                            pathTarget = positionalTarget;
                        }
                        else
                        {
                            workspaceTarget = positionalTarget;
                        }
                    }

                    var collectionStatus = await transferService.ResolveCollectionsAsync(
                        workspaceTarget,
                        GetString(options, "group"),
                        pathTarget,
                        cancellationToken: default);
                    Console.WriteLine(JsonSerializer.Serialize(collectionStatus, jsonOptions));
                    return 0;
                }

                default:
                    throw new ArgumentException("Workspace command must be one of: adopt, collection, delete, export, import, list, migrate, status.");
            }
        }

        case "group":
        {
            var action = RequiredSubcommand(args, "group");
            switch (action)
            {
                case "list":
                    WriteGroupsTable(await workspaceGroupRegistry.GetGroupsAsync());
                    return 0;

                case "create":
                case "add":
                {
                    var groupName = GetWorkspaceCommandSubject(args, options, "group");
                    var targets = await ResolveGroupWorkspaceTargetsAsync(options, workspaceRegistry);
                    Console.WriteLine(JsonSerializer.Serialize(await SaveWorkspaceGroupAsync(
                        groupName,
                        targets,
                        add: action == "add" || GetBool(options, "add"),
                        workspaceRegistry,
                        workspaceGroupRegistry), jsonOptions));
                    return 0;
                }

                case "delete":
                    Console.WriteLine(JsonSerializer.Serialize(await DeleteWorkspaceGroupAsync(
                        GetWorkspaceCommandSubject(args, options, "group"),
                        workspaceGroupRegistry), jsonOptions));
                    return 0;

                case "export":
                    Console.WriteLine(JsonSerializer.Serialize(await transferService.ExportGroupAsync(
                        GetWorkspaceCommandSubject(args, options, "group"),
                        Required(options, "output")), jsonOptions));
                    return 0;

                case "import":
                    Console.WriteLine(JsonSerializer.Serialize(await transferService.ImportAsync(
                        Required(options, "input"),
                        ParsePathMap(GetMany(options, "path-map")),
                        expectedKind: "group"), jsonOptions));
                    return 0;

                default:
                    throw new ArgumentException("Group command must be one of: add, create, delete, export, import, list.");
            }
        }

        case "qdrant":
        {
            var action = RequiredSubcommand(args, "qdrant");
            if (action != "status")
            {
                throw new ArgumentException("Qdrant command must be: status.");
            }

            var qdrantStatus = await GetQdrantStatusAsync(ragNetOptions, workspaceRegistry);
            Console.WriteLine(JsonSerializer.Serialize(qdrantStatus, jsonOptions));
            return 0;
        }

        case "profile":
        {
            var action = RequiredSubcommand(args, "profile");
            if (action != "list")
            {
                throw new ArgumentException("Profile command must be: list.");
            }

            WriteProfilesTable();
            return 0;
        }

        default:
            Console.Error.WriteLine($"Unknown command '{command}'.");
            WriteHelp();
            return 1;
    }
}

static int GetOptionStart(string command, string[] args)
{
    if (command == "eval" || command == "index")
    {
        return 1;
    }

    if (command is "qdrant" or "profile")
    {
        return 2;
    }

    if (command == "workspace" &&
        args.Length > 1 &&
        !args[1].StartsWith("-", StringComparison.Ordinal) &&
        !IsHelp(args[1]))
    {
        var action = args[1].Trim().ToLowerInvariant();
        if (action is "adopt" or "collection" or "delete" or "export" or "status" &&
            args.Length > 2 &&
            !args[2].StartsWith("-", StringComparison.Ordinal) &&
            !IsHelp(args[2]))
        {
            return 3;
        }

        return 2;
    }

    if (command == "group" &&
        args.Length > 1 &&
        !args[1].StartsWith("-", StringComparison.Ordinal) &&
        !IsHelp(args[1]))
    {
        var action = args[1].Trim().ToLowerInvariant();
        if (action is "add" or "create" or "delete" or "export" &&
            args.Length > 2 &&
            !args[2].StartsWith("-", StringComparison.Ordinal) &&
            !IsHelp(args[2]))
        {
            return 3;
        }

        return 2;
    }

    return 1;
}

static string RequiredSubcommand(string[] args, string command)
{
    if (args.Length < 2 || args[1].StartsWith("-", StringComparison.Ordinal) || IsHelp(args[1]))
    {
        throw new ArgumentException($"{command} command requires a subcommand.");
    }

    return args[1].Trim().ToLowerInvariant();
}

static string GetWorkspaceCommandSubject(string[] args, Dictionary<string, List<string>> options, string optionName)
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
        "e" => "exclude",
        "f" => "force",
        "o" => "output",
        "i" => "input",
        "q" => "queries",
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

static IReadOnlyList<string>? GetManyDelimited(Dictionary<string, List<string>> options, string name)
    => options.TryGetValue(name, out var values) && values.Count > 0
        ? values
            .SelectMany(SplitManyOptionValue)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
        : null;

static IEnumerable<string> SplitManyOptionValue(string value)
    => value
        .Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

static string? GetString(Dictionary<string, List<string>> options, string name)
    => options.TryGetValue(name, out var values) && values.Count > 0 && !string.IsNullOrWhiteSpace(values[^1])
        ? values[^1]
        : null;

static int? GetInt(Dictionary<string, List<string>> options, string name)
    => options.TryGetValue(name, out var values) && values.Count > 0 && !string.IsNullOrWhiteSpace(values[^1])
        ? int.Parse(values[^1])
        : null;

static bool? GetNullableBool(Dictionary<string, List<string>> options, string name)
    => options.TryGetValue(name, out var values)
        ? values.Count == 0 || bool.Parse(values[^1])
        : null;

static bool GetBool(Dictionary<string, List<string>> options, string name)
    => options.ContainsKey(name) &&
        (options[name].Count == 0 || bool.Parse(options[name][^1]));

static IReadOnlyDictionary<string, string> ParsePathMap(IReadOnlyList<string>? values)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var value in values ?? [])
    {
        var separator = value.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1)
        {
            throw new ArgumentException("--path-map values must use <exported-root>=<local-root>.");
        }

        map[value[..separator].Trim()] = value[(separator + 1)..].Trim();
    }

    return map;
}

static bool IsHelp(string value)
    => value is "-h" or "--help" or "help";

static bool TryWriteScopedHelp(string[] args)
{
    if (args.Length < 2 || !args.Skip(1).Any(IsHelp))
    {
        return false;
    }

    var command = args[0].Trim().ToLowerInvariant();
    var subcommand = args.Skip(1)
        .FirstOrDefault(arg => !IsHelp(arg) && !arg.StartsWith("-", StringComparison.Ordinal))?
        .Trim()
        .ToLowerInvariant();

    switch (command)
    {
        case "index":
            WriteIndexHelp();
            return true;

        case "workspace":
            WriteWorkspaceHelp(subcommand);
            return true;

        case "group":
            WriteGroupHelp(subcommand);
            return true;

        case "qdrant":
            WriteQdrantHelp();
            return true;

        case "profile":
            WriteProfileHelp();
            return true;

        case "eval":
            WriteEvalHelp();
            return true;

        default:
            return false;
    }
}

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
            .Where(workspace => IndexedWorkspaceRecordNames.MatchesAlias(workspace, trimmed))
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
        0 => throw new InvalidOperationException($"Indexed workspace '{trimmed}' was not found. Use 'workspace list' to see available workspaces."),
        _ => throw new InvalidOperationException($"Workspace name '{trimmed}' matches {matches.Length} indexed workspaces. Use a full workspace root.")
    };
}

static async Task<IReadOnlyList<IndexWorkspaceResult>> IndexGroupAsync(
    IWorkspaceIndexer indexer,
    string group,
    Dictionary<string, List<string>> options,
    IProgress<IndexingProgress>? progress)
    => await indexer.IndexGroupAsync(
        group,
        GetManyDelimited(options, "exclude"),
        GetBool(options, "force"),
        GetString(options, "profile") ?? GetString(options, "index-profile"),
        progress: progress);

static async Task<IReadOnlyList<DryRunIndexWorkspaceResult>> DryRunIndexGroupAsync(
    IWorkspaceIndexer indexer,
    string group,
    Dictionary<string, List<string>> options,
    IProgress<IndexingProgress>? progress)
    => await indexer.DryRunIndexGroupAsync(
        group,
        GetManyDelimited(options, "exclude"),
        GetBool(options, "force"),
        GetString(options, "profile") ?? GetString(options, "index-profile"),
        progress: progress);

static void WriteGroupsTable(IReadOnlyList<WorkspaceGroupRecord> groups)
{
    var rows = groups
        .Select(group => new[]
        {
            group.Name,
            group.Source,
            group.Roots.Count.ToString(),
            string.Join(", ", group.Roots),
            group.IsReadOnly ? "yes" : "no",
            group.ExcludeDirectories.Count == 0 ? "-" : string.Join(", ", group.ExcludeDirectories)
        })
        .ToArray();

    WriteTable(["Name", "Source", "Roots", "Targets", "Read Only", "Excludes"], rows);
}

static void WriteProfilesTable()
{
    var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [IndexProfiles.All] = "All indexable content.",
        [IndexProfiles.Code] = "Application and library source code.",
        [IndexProfiles.Documentation] = "Documentation files such as Markdown, HTML docs, and text docs.",
        [IndexProfiles.Metadata] = ".NET project, solution, package, and configuration metadata.",
        [IndexProfiles.Frontend] = "Frontend views and UI source such as HTML views, JSX, TSX, and XAML.",
        [IndexProfiles.Tests] = "Test projects and test files."
    };

    var rows = IndexProfiles.Supported
        .Select(profile => new[]
        {
            profile,
            descriptions[profile]
        })
        .ToArray();

    WriteTable(["Profile", "Description"], rows);
}

static void WriteWorkspacesTable(IReadOnlyList<IndexedWorkspaceRecord> workspaces)
{
    var rows = workspaces
        .Select(workspace => new[]
        {
            workspace.EffectiveDisplayName,
            workspace.Status,
            FormatAliases(workspace),
            workspace.WorkspaceRoot,
            workspace.Groups.Count == 0 ? "-" : string.Join(", ", workspace.Groups),
            workspace.IndexedTargets.Count.ToString(),
            workspace.LastIndexedUtc == DateTimeOffset.MinValue ? "-" : workspace.LastIndexedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"),
            workspace.FilesScanned.ToString(),
            workspace.ChunksIndexed.ToString(),
            workspace.CollectionName
        })
        .ToArray();

    WriteTable(["Name", "Status", "Aliases", "Root", "Groups", "Targets", "Last Indexed", "Files", "Chunks", "Collection"], rows);
}

static string FormatAliases(IndexedWorkspaceRecord workspace)
{
    var aliases = workspace.EffectiveAliases
        .Where(alias => !string.Equals(alias, workspace.EffectiveDisplayName, StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    return aliases.Length == 0 ? "-" : string.Join(", ", aliases);
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

static async Task<DeleteWorkspaceGroupResult> DeleteWorkspaceGroupAsync(
    string group,
    IWorkspaceGroupRegistry workspaceGroupRegistry)
{
    var record = await workspaceGroupRegistry.GetGroupAsync(group);
    if (record is null)
    {
        throw new InvalidOperationException($"Workspace group '{group}' was not found.");
    }

    if (record.Source == WorkspaceGroupSources.Configured || record.IsReadOnly)
    {
        throw new InvalidOperationException($"Workspace group '{record.Name}' is configured in appsettings and cannot be deleted by the indexer. Edit configuration to remove it.");
    }

    if (record.Source == WorkspaceGroupSources.Local)
    {
        return DeleteLocalWorkspaceGroup(record.Name);
    }

    await workspaceGroupRegistry.DeleteGroupAsync(record.Name);
    Console.Error.WriteLine($"Deleted shared workspace group '{record.Name}'.");
    return new DeleteWorkspaceGroupResult(record.Name, record.Source, "qdrant");
}

static DeleteWorkspaceGroupResult DeleteLocalWorkspaceGroup(string group)
{
    var path = GetLocalWorkspaceGroupsPath();
    var currentGroups = LoadLocalWorkspaceGroups();
    var remainingGroups = currentGroups
        .Where(candidate => !string.Equals(candidate.Name, group, StringComparison.OrdinalIgnoreCase))
        .ToArray();

    if (remainingGroups.Length == currentGroups.Count)
    {
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
    IWorkspaceIndexStateStore stateStore,
    RagNetOptions ragNetOptions)
{
    var indexedWorkspaces = await workspaceRegistry.GetIndexedWorkspacesAsync();
    var record = TryResolveIndexedWorkspaceRecord(workspace, indexedWorkspaces);
    var workspaceRoot = record?.WorkspaceRoot ?? ResolveWorkspaceRootForDelete(workspace);
    var state = await stateStore.LoadAsync(workspaceRoot);
    var collectionNames = new List<string>();

    if (record is not null)
    {
        collectionNames.Add(record.CollectionName);
    }

    if (!string.IsNullOrWhiteSpace(state.IndexingCollectionName))
    {
        collectionNames.Add(state.IndexingCollectionName);
    }

    if (collectionNames.Count == 0 && !state.StateExists)
    {
        throw new InvalidOperationException($"Indexed workspace '{workspace}' was not found in the workspace registry or index state. Use a full workspace root for incomplete indexes.");
    }

    if (collectionNames.Count == 0)
    {
        collectionNames.Add(QdrantCollectionNaming.GetCollectionName(ragNetOptions.Qdrant.CollectionPrefix, workspaceRoot));
    }

    var deletedCollections = collectionNames
        .Where(collectionName => !string.IsNullOrWhiteSpace(collectionName))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    foreach (var collectionName in deletedCollections)
    {
        await vectorStore.DeleteCollectionAsync(collectionName);
    }

    await workspaceRegistry.DeleteWorkspaceAsync(workspaceRoot);
    await stateStore.DeleteAsync(workspaceRoot);
    Console.Error.WriteLine($"Deleted indexed workspace '{workspaceRoot}'.");
    return new DeleteIndexedWorkspaceResult(
        workspaceRoot,
        record?.WorkspaceId ?? QdrantCollectionNaming.GetWorkspaceId(workspaceRoot),
        record?.CollectionName,
        deletedCollections,
        RemovedIncompleteState: state.StateExists && !state.IsComplete);
}

static string ResolveWorkspaceRootForDelete(string workspace)
{
    var trimmed = workspace.Trim();
    if (string.IsNullOrWhiteSpace(trimmed))
    {
        throw new ArgumentException("Workspace target cannot be empty.");
    }

    if (!Path.IsPathFullyQualified(trimmed) &&
        trimmed.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0 &&
        !Directory.Exists(trimmed) &&
        !File.Exists(trimmed))
    {
        throw new InvalidOperationException($"Indexed workspace '{trimmed}' was not found in the workspace registry. Use a full workspace root for incomplete indexes.");
    }

    var fullPath = Path.GetFullPath(trimmed);
    var directory = Directory.Exists(fullPath)
        ? fullPath
        : Path.GetDirectoryName(fullPath) ?? fullPath;
    return NormalizePath(directory);
}

static IndexedWorkspaceRecord? TryResolveIndexedWorkspaceRecord(
    string workspace,
    IReadOnlyList<IndexedWorkspaceRecord> indexedWorkspaces)
{
    try
    {
        return ResolveIndexedWorkspaceRecord(workspace, indexedWorkspaces);
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("was not found in the workspace registry", StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }
}

static IndexedWorkspaceRecord ResolveIndexedWorkspaceRecord(
    string workspace,
    IReadOnlyList<IndexedWorkspaceRecord> indexedWorkspaces)
{
    var trimmed = workspace.Trim();
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
            .Where(record => IndexedWorkspaceRecordNames.MatchesAlias(record, trimmed))
            .ToArray();
    }
    else
    {
        var workspaceRoot = NormalizePath(trimmed);
        matches = indexedWorkspaces
            .Where(record => string.Equals(NormalizePath(record.WorkspaceRoot), workspaceRoot, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    return matches.Length switch
    {
        1 => matches[0],
        0 => throw new InvalidOperationException($"Indexed workspace '{trimmed}' was not found in the workspace registry."),
        _ => throw new InvalidOperationException($"Workspace name '{trimmed}' matches {matches.Length} indexed workspaces. Use a full workspace root.")
    };
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

static async Task<CreateWorkspaceGroupResult> SaveWorkspaceGroupAsync(
    string group,
    IReadOnlyList<string> workspaces,
    bool add,
    IIndexedWorkspaceRegistry workspaceRegistry,
    IWorkspaceGroupRegistry workspaceGroupRegistry)
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

    var existingGroup = await workspaceGroupRegistry.GetGroupAsync(group);
    if (existingGroup is { IsReadOnly: true } || existingGroup?.Source == WorkspaceGroupSources.Configured)
    {
        throw new InvalidOperationException($"Workspace group '{existingGroup.Name}' is configured in appsettings and cannot be changed by the indexer. Edit configuration to change it.");
    }

    if (add && existingGroup is not null)
    {
        normalizedRoots = existingGroup.Roots
            .Concat(normalizedRoots)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    var savedGroup = await workspaceGroupRegistry.SaveGroupAsync(group, normalizedRoots);
    var action = add && existingGroup is not null ? "Updated" : "Saved";
    Console.Error.WriteLine($"{action} shared workspace group '{savedGroup.Name}' with {savedGroup.Roots.Count} workspace(s).");
    return new CreateWorkspaceGroupResult(savedGroup.Name, savedGroup.Source, "qdrant", savedGroup.Roots, add && existingGroup is not null);
}

static async Task<QdrantStatusResult> GetQdrantStatusAsync(
    RagNetOptions ragNetOptions,
    IIndexedWorkspaceRegistry workspaceRegistry)
{
    using var httpClient = new HttpClient
    {
        BaseAddress = new Uri(EnsureTrailingSlash(ragNetOptions.Qdrant.BaseUrl))
    };

    var collectionPrefix = SanitizeCollectionPart(ragNetOptions.Qdrant.CollectionPrefix);
    if (string.IsNullOrWhiteSpace(collectionPrefix))
    {
        collectionPrefix = "ragnet";
    }

    var collections = await GetQdrantCollectionsAsync(httpClient);
    var matchingCollections = collections
        .Where(collection => collection.Name.StartsWith($"{collectionPrefix}-", StringComparison.OrdinalIgnoreCase))
        .OrderBy(collection => collection.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var workspaces = await workspaceRegistry.GetIndexedWorkspacesAsync();
    var workspaceCollectionNames = workspaces
        .Select(workspace => workspace.CollectionName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    long approximateVectorCount = 0;
    var vectorCountAvailable = true;
    foreach (var collectionName in workspaceCollectionNames)
    {
        var pointCount = await GetQdrantCollectionPointCountAsync(httpClient, collectionName);
        if (pointCount.HasValue)
        {
            approximateVectorCount += pointCount.Value;
        }
        else
        {
            vectorCountAvailable = false;
        }
    }

    var indexStateCollectionName = $"{collectionPrefix}-index-state";
    var indexStateCount = matchingCollections.Any(collection => string.Equals(collection.Name, indexStateCollectionName, StringComparison.OrdinalIgnoreCase))
        ? await GetQdrantCollectionPointCountAsync(httpClient, indexStateCollectionName)
        : 0;

    return new QdrantStatusResult(
        ragNetOptions.Qdrant.BaseUrl,
        collectionPrefix,
        collections.Count,
        matchingCollections.Length,
        workspaces.Count,
        indexStateCount,
        vectorCountAvailable ? approximateVectorCount : null,
        matchingCollections.Select(collection => collection.Name).ToArray());
}

static async Task<IReadOnlyList<QdrantCollectionListItem>> GetQdrantCollectionsAsync(HttpClient httpClient)
{
    using var response = await httpClient.GetAsync("collections");
    await EnsureQdrantSuccessAsync(response, "list Qdrant collections");

    using var stream = await response.Content.ReadAsStreamAsync();
    using var document = await JsonDocument.ParseAsync(stream);
    if (!document.RootElement.TryGetProperty("result", out var result) ||
        !result.TryGetProperty("collections", out var collections) ||
        collections.ValueKind != JsonValueKind.Array)
    {
        return [];
    }

    return collections.EnumerateArray()
        .Select(collection => GetJsonString(collection, "name"))
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => new QdrantCollectionListItem(name))
        .ToArray();
}

static async Task<long?> GetQdrantCollectionPointCountAsync(HttpClient httpClient, string collectionName)
{
    using var response = await httpClient.GetAsync($"collections/{Uri.EscapeDataString(collectionName)}");
    if (response.StatusCode == HttpStatusCode.NotFound)
    {
        return 0;
    }

    await EnsureQdrantSuccessAsync(response, $"read Qdrant collection '{collectionName}'");

    using var stream = await response.Content.ReadAsStreamAsync();
    using var document = await JsonDocument.ParseAsync(stream);
    if (!document.RootElement.TryGetProperty("result", out var result))
    {
        return null;
    }

    return GetNullableInt64(result, "points_count") ??
        GetNullableInt64(result, "vectors_count");
}

static async Task EnsureQdrantSuccessAsync(HttpResponseMessage response, string action)
{
    if (response.IsSuccessStatusCode)
    {
        return;
    }

    var body = await response.Content.ReadAsStringAsync();
    throw new HttpRequestException(
        $"Failed to {action}. Qdrant returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
        null,
        response.StatusCode);
}

static string EnsureTrailingSlash(string baseUrl)
    => baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : $"{baseUrl}/";

static string SanitizeCollectionPart(string value)
{
    var chars = value
        .Trim()
        .ToLowerInvariant()
        .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-')
        .ToArray();

    return new string(chars).Trim('-', '_', '.');
}

static string GetJsonString(JsonElement element, string name)
    => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? string.Empty
        : string.Empty;

static long? GetNullableInt64(JsonElement element, string name)
{
    if (!element.TryGetProperty(name, out var value))
    {
        return null;
    }

    if (value.TryGetInt64(out var result))
    {
        return result;
    }

    return null;
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
            .Where(workspace => IndexedWorkspaceRecordNames.MatchesAlias(workspace, trimmed))
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

static void WriteIndexHelp()
{
    Console.WriteLine("""
    RagNet Indexer - index

    Usage:
      ragnet-indexer index --workspace|-w <target> [--workspace|-w <target> ...] [options]
      ragnet-indexer index --current|-c [options]
      ragnet-indexer index --group|-g <name> [options]

    Targets:
      --workspace, -w <target>  Workspace root, directory, solution file, or supported file. Repeat to union targets.
      --current, -c             Use the current directory as the target.
      --group, -g <name>        With targets, save/replace a group. Without targets, index an existing group.

    Options:
      --add, -a                 Append targets to an existing group instead of replacing it.
      --force                   Force a full reindex for compatible full-workspace targets.
      --dry-run                 Preview files/chunks without writing vectors, state, registry records, or groups.
      --profile <profile>       all, code, docs, metadata, frontend, or tests. Default is all.
      --exclude, -e <path>      Exclude directory name or relative path. Repeat or pass comma/semicolon-separated values.
      --no-progress             Suppress progress output.

    Examples:
      ragnet-indexer index -w D:\Work\Product\Api\Api.sln
      ragnet-indexer index -w D:\Work\Product\Api\Api.sln -w D:\Work\Product\Admin\Admin.sln -g my-product
      ragnet-indexer index -g my-product --dry-run
    """);
}

static void WriteWorkspaceHelp(string? subcommand)
{
    switch (subcommand)
    {
        case "list":
            Console.WriteLine("""
            RagNet Indexer - workspace list

            Usage:
              ragnet-indexer workspace list

            Lists indexed workspaces from the Qdrant registry as a table.
            """);
            return;

        case "status":
            Console.WriteLine("""
            RagNet Indexer - workspace status

            Usage:
              ragnet-indexer workspace status <indexed-name-or-root>

            Shows index state for one workspace.

            Example:
              ragnet-indexer workspace status Api
            """);
            return;

        case "delete":
            Console.WriteLine("""
            RagNet Indexer - workspace delete

            Usage:
              ragnet-indexer workspace delete <indexed-name-or-root>

            Deletes the workspace vector collection, registry record, index-state points,
            and any incomplete staging collection recorded in state.
            """);
            return;

        case "collection":
            Console.WriteLine("""
            RagNet Indexer - workspace collection

            Usage:
              ragnet-indexer workspace collection <indexed-name-or-root-or-path>
              ragnet-indexer workspace collection --workspace <path-or-name>
              ragnet-indexer workspace collection --group <name>
              ragnet-indexer workspace collection --path <file-or-directory>

            Resolves workspaces, paths, or groups to Qdrant collection names.
            """);
            return;

        case "export":
            Console.WriteLine("""
            RagNet Indexer - workspace export

            Usage:
              ragnet-indexer workspace export <indexed-name-or-root> --output|-o <directory>

            Exports one indexed workspace manifest and Qdrant point data.
            """);
            return;

        case "import":
            Console.WriteLine("""
            RagNet Indexer - workspace import

            Usage:
              ragnet-indexer workspace import --input|-i <directory> [--workspace-root <new-root>|--root <new-root>|--path-map <exported-root=new-root> ...]

            Imports a workspace export into the current Qdrant instance.
            """);
            return;

        case "adopt":
            Console.WriteLine("""
            RagNet Indexer - workspace adopt

            Usage:
              ragnet-indexer workspace adopt <workspace-root> [--target <indexed-target> ...] [--embedding-model <model>]

            Records index state for an existing Qdrant collection.
            """);
            return;

        case "migrate":
            Console.WriteLine("""
            RagNet Indexer - workspace migrate

            Usage:
              ragnet-indexer workspace migrate

            Rewrites current registry records with export-friendly metadata when it can be inferred.
            """);
            return;
    }

    Console.WriteLine("""
    RagNet Indexer - workspace

    Usage:
      ragnet-indexer workspace list
      ragnet-indexer workspace status <indexed-name-or-root>
      ragnet-indexer workspace delete <indexed-name-or-root>
      ragnet-indexer workspace collection [<indexed-name-or-root>|--workspace <path-or-name>|--group <name>|--path <file-or-directory>]
      ragnet-indexer workspace export <indexed-name-or-root> --output|-o <directory>
      ragnet-indexer workspace import --input|-i <directory> [--workspace-root <new-root>|--root <new-root>|--path-map <exported-root=new-root> ...]
      ragnet-indexer workspace adopt <workspace-root> [--target <indexed-target> ...] [--embedding-model <model>]
      ragnet-indexer workspace migrate

    Run `ragnet-indexer workspace <command> --help` for command-specific help.
    """);
}

static void WriteGroupHelp(string? subcommand)
{
    switch (subcommand)
    {
        case "list":
            Console.WriteLine("""
            RagNet Indexer - group list

            Usage:
              ragnet-indexer group list

            Lists configured and shared workspace groups as a table.
            """);
            return;

        case "create":
            Console.WriteLine("""
            RagNet Indexer - group create

            Usage:
              ragnet-indexer group create <name> --workspace|-w <indexed-name-or-root> [--workspace|-w <indexed-name-or-root> ...] [--current|-c]

            Creates or replaces a shared group from already indexed workspaces.
            """);
            return;

        case "add":
            Console.WriteLine("""
            RagNet Indexer - group add

            Usage:
              ragnet-indexer group add <name> --workspace|-w <indexed-name-or-root> [--workspace|-w <indexed-name-or-root> ...] [--current|-c]

            Appends already indexed workspaces to an existing shared group.
            """);
            return;

        case "delete":
            Console.WriteLine("""
            RagNet Indexer - group delete

            Usage:
              ragnet-indexer group delete <name>

            Deletes a shared group. Configured read-only groups must be removed from configuration.
            """);
            return;

        case "export":
            Console.WriteLine("""
            RagNet Indexer - group export

            Usage:
              ragnet-indexer group export <name> --output|-o <directory>

            Exports all indexed workspaces in a group.
            """);
            return;

        case "import":
            Console.WriteLine("""
            RagNet Indexer - group import

            Usage:
              ragnet-indexer group import --input|-i <directory> --path-map <exported-root=new-root> [...]

            Imports a product/group export into the current Qdrant instance.
            """);
            return;
    }

    Console.WriteLine("""
    RagNet Indexer - group

    Usage:
      ragnet-indexer group list
      ragnet-indexer group create <name> --workspace|-w <indexed-name-or-root> [...]
      ragnet-indexer group add <name> --workspace|-w <indexed-name-or-root> [...]
      ragnet-indexer group delete <name>
      ragnet-indexer group export <name> --output|-o <directory>
      ragnet-indexer group import --input|-i <directory> --path-map <exported-root=new-root> [...]

    Run `ragnet-indexer group <command> --help` for command-specific help.
    """);
}

static void WriteQdrantHelp()
{
    Console.WriteLine("""
    RagNet Indexer - qdrant

    Usage:
      ragnet-indexer qdrant status

    Shows configured Qdrant URL, collection counts, registry counts, index-state count,
    approximate vector count, and matching RagNet collection names.
    """);
}

static void WriteProfileHelp()
{
    Console.WriteLine("""
    RagNet Indexer - profile

    Usage:
      ragnet-indexer profile list

    Lists available index/search profiles.
    """);
}

static void WriteEvalHelp()
{
    Console.WriteLine("""
    RagNet Indexer - eval

    Usage:
      ragnet-indexer eval --queries|-q <eval.json> [--workspace-root <path>|--group <name>] [--limit <n>] [--hybrid] [--search-profile <profile>]

    Runs a search evaluation JSON suite through the same search pipeline used by MCP.
    Exits 0 when every query passes and 2 when any query misses.
    """);
}

static void WriteHelp()
{
    Console.WriteLine("""
    RagNet Indexer

    Commands:
      index       --workspace|-w <target> [--workspace|-w <target> ...] [--current|-c] [--group|-g <name>] [--add|-a] [--force] [--dry-run] [--profile <profile>] [--exclude|-e <dir-or-relative-path> ...]
      index       --group|-g <name> [--force] [--dry-run] [--profile <profile>] [--exclude|-e <dir-or-relative-path> ...]
      workspace   list
      workspace   status <indexed-name-or-root>
      workspace   delete <indexed-name-or-root>
      workspace   collection [<indexed-name-or-root>|--workspace <path-or-name>|--group <name>|--path <file-or-directory>]
      workspace   export <indexed-name-or-root> --output|-o <directory>
      workspace   import --input|-i <directory> [--workspace-root <new-root>|--root <new-root>|--path-map <exported-root=new-root> ...]
      workspace   adopt <workspace-root> [--target <indexed-target> ...] [--embedding-model <model>]
      workspace   migrate
      group       list
      group       create <name> --workspace|-w <indexed-name-or-root> [--workspace|-w <indexed-name-or-root> ...] [--current|-c]
      group       add <name> --workspace|-w <indexed-name-or-root> [--workspace|-w <indexed-name-or-root> ...] [--current|-c]
      group       delete <name>
      group       export <name> --output|-o <directory>
      group       import --input|-i <directory> --path-map <exported-root=new-root> [...]
      qdrant      status
      profile     list
      eval        --queries|-q <eval.json> [--workspace-root <path>|--group <name>] [--limit <n>] [--hybrid] [--search-profile <profile>]

    Options:
      --profile       Index profile: all, code, docs, metadata, frontend, or tests. Default is all.
      --group, -g     With index --workspace, saves/replaces a shared group. With index only, indexes an existing group.
      --workspace, -w Index target: workspace root, directory, solution file, or supported file. Repeat to union targets.
      --current, -c   Add the current directory as an index target.
      --add, -a       With --workspace and --group, append targets to the existing group instead of replacing it.
      --dry-run       Preview files and chunks that would be indexed without writing vectors, state, registry, or local groups.
      --exclude, -e   Exclude a directory name or relative path. Repeat, pass several values, or use comma/semicolon-separated values.
      --no-progress   Suppress progress output. Index/status/delete results are written to stdout.
      --queries, -q   Search evaluation JSON file. Eval exits 0 when all queries pass and 2 when any query misses.
      --output, -o    Export directory for a RagNet Qdrant workspace/group export.
      --input, -i     Import directory containing ragnet-export-manifest.json.
      --workspace-root New local root when importing a single exported workspace.
      --root          Shorter alias for --workspace-root during workspace import/adopt.
      --path-map      Import remap from exported root to local root. Repeat for multi-workspace exports.
      --target        Indexed target to record when adopting an existing Qdrant collection. Repeat for solution/product scopes.
      --embedding-model Embedding model to record when adopting index state. Defaults to configured RagNet:Ollama:EmbeddingModel.

    Examples:
      ragnet-indexer index --workspace D:\Work\Product\Api\Api.sln
      ragnet-indexer index --current
      ragnet-indexer index --workspace D:\Work\Product\Api\Api.sln --dry-run
      ragnet-indexer index -w D:\Work\Product\Api -e bin -e obj -e "artifacts;node_modules"
      ragnet-indexer index --workspace D:\Work\Product\Api\Api.sln --workspace D:\Work\Product\Admin\Admin.sln --group my-product
      ragnet-indexer index -w D:\Work\Product\Api\Api.sln -w D:\Work\Product\docs\api
      ragnet-indexer index -w D:\Work\Product\Worker -g my-product -a
      ragnet-indexer index --group my-product --force
      ragnet-indexer workspace status Api
      ragnet-indexer qdrant status
      ragnet-indexer workspace collection D:\Work\Product\Api\Api.sln
      ragnet-indexer workspace export Api -o D:\Backups\ragnet-api
      ragnet-indexer workspace import -i D:\Backups\ragnet-api --root E:\Repos\Product\Api
      ragnet-indexer workspace adopt E:\Repos\Product\Api --target E:\Repos\Product\Api\Api.sln
      ragnet-indexer workspace migrate
      ragnet-indexer group create my-product -w Api -w Admin
      ragnet-indexer group add my-product -w Worker
      ragnet-indexer group export my-product -o D:\Backups\ragnet-product
      ragnet-indexer group import -i D:\Backups\ragnet-product --path-map D:\Work\Product=E:\Repos\Product
      ragnet-indexer group list
      ragnet-indexer group delete my-product
      ragnet-indexer workspace list
      ragnet-indexer workspace delete Api
      ragnet-indexer profile list
      ragnet-indexer eval -q eval.json --workspace-root D:\Work\Product\Api --limit 10 --hybrid
    """);
}

sealed record LocalWorkspaceGroupStore(IReadOnlyList<LocalWorkspaceGroup> Groups);

sealed record LocalWorkspaceGroup(string Name, IReadOnlyList<string> Roots);

sealed record WorkspaceGroupListItem(
    string Name,
    string Source,
    IReadOnlyList<string> Roots,
    IReadOnlyList<string> ExcludeDirectories);

sealed record DeleteWorkspaceGroupResult(string Name, string Source, string StorePath);

sealed record DeleteIndexedWorkspaceResult(
    string WorkspaceRoot,
    string WorkspaceId,
    string? CollectionName,
    IReadOnlyList<string> DeletedCollections,
    bool RemovedIncompleteState);

sealed record CreateWorkspaceGroupResult(
    string Name,
    string Source,
    string StorePath,
    IReadOnlyList<string> Roots,
    bool Appended);

sealed record OfflineService(string Name, string BaseUrl, string SetupHint);

sealed record QdrantStatusResult(
    string QdrantUrl,
    string CollectionPrefix,
    int CollectionsCount,
    int MatchingCollectionsCount,
    int RegisteredWorkspaces,
    long? IndexStateCount,
    long? ApproximateVectorCount,
    IReadOnlyList<string> MatchingCollections);

sealed record QdrantCollectionListItem(string Name);

static class ProgressConsole
{
    private static readonly ConcurrentDictionary<string, int> Sequences = new(StringComparer.OrdinalIgnoreCase);
    private static readonly char[] SpinnerFrames = ['|', '/', '-', '\\'];
    private static readonly object ConsoleLock = new();
    private static int _spinnerIndex;
    private static int _lastLineLength;
    private static string? _lastTemplate;
    private static Timer? _spinnerTimer;

    public static void Write(IndexingProgress progress)
    {
        var displayCurrent = GetDisplayCurrent(progress);
        var count = progress.Total.HasValue
            ? $"{displayCurrent}/{progress.Total.Value}"
            : $"{displayCurrent}/-";
        var workspaceName = Path.GetFileName(progress.WorkspaceRoot);
        if (string.IsNullOrWhiteSpace(workspaceName))
        {
            workspaceName = progress.WorkspaceRoot;
        }

        var template = $"{DateTimeOffset.Now:HH:mm:ss} {{0}} {workspaceName} {progress.Message} {count}";
        if (Console.IsErrorRedirected)
        {
            Console.Error.WriteLine(string.Format(CultureInfo.InvariantCulture, template, NextSpinner()));
            return;
        }

        lock (ConsoleLock)
        {
            _lastTemplate = template;
            EnsureSpinnerTimer();
            RenderLineLocked(string.Format(CultureInfo.InvariantCulture, template, NextSpinner()));
            if (progress.Stage == IndexingProgressStage.Completed)
            {
                Console.Error.WriteLine();
                _lastLineLength = 0;
                _lastTemplate = null;
            }
        }
    }

    private static void EnsureSpinnerTimer()
    {
        _spinnerTimer ??= new Timer(_ =>
        {
            lock (ConsoleLock)
            {
                if (_lastTemplate is null)
                {
                    return;
                }

                RenderLineLocked(string.Format(CultureInfo.InvariantCulture, _lastTemplate, NextSpinner()));
            }
        }, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    private static void RenderLineLocked(string text)
    {
        var line = FitToConsoleWidth(text);
        Console.Error.Write('\r');
        Console.Error.Write(line);
        if (_lastLineLength > line.Length)
        {
            Console.Error.Write(new string(' ', _lastLineLength - line.Length));
            Console.Error.Write('\r');
            Console.Error.Write(line);
        }

        _lastLineLength = line.Length;
    }

    private static char NextSpinner()
    {
        var index = Interlocked.Increment(ref _spinnerIndex) - 1;
        return SpinnerFrames[index % SpinnerFrames.Length];
    }

    private static string FitToConsoleWidth(string text)
    {
        try
        {
            var width = Console.WindowWidth;
            if (width <= 1 || text.Length < width)
            {
                return text;
            }

            return text[..Math.Max(1, width - 1)];
        }
        catch (IOException)
        {
            return text;
        }
    }

    private static int GetDisplayCurrent(IndexingProgress progress)
    {
        if (!progress.Total.HasValue || progress.Total.Value <= 0)
        {
            return progress.Current;
        }

        var total = progress.Total.Value;
        var key = $"{progress.WorkspaceRoot}|{progress.Stage}|{progress.Message}|{total}";
        if (progress.Current <= 0)
        {
            Sequences[key] = 0;
            return 0;
        }

        var displayCurrent = Sequences.AddOrUpdate(
            key,
            _ => Math.Min(progress.Current, total),
            (_, previous) => Math.Min(Math.Max(progress.Current, previous + 1), total));

        if (displayCurrent >= total)
        {
            Sequences.TryRemove(key, out _);
        }

        return displayCurrent;
    }
}
