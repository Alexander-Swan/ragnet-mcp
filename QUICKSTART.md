# Quickstart

For detailed setup, modes, registration, and troubleshooting, see [SETUP.md](SETUP.md).

## 1. Run Setup

```powershell
.\scripts\setup.ps1 -Mode Hybrid
```

This starts Qdrant and RagNet MCP in Docker, starts Ollama in Docker unless local Ollama is already available, pulls the default RagNet embedding model plus `nomic-embed-text` for compatibility, publishes the local indexer executable, and registers supported MCP clients:

- `.mcp.json` for Visual Studio
- `.vscode/mcp.json` for VS Code
- `ragnet-mcp` in Codex/Codex CLI when `codex` is available on PATH
- `ragnet-mcp` in Claude Code when `claude` is available on PATH

When setup starts the Compose Qdrant service, indexed data is persisted in the named Docker volume `ragnet-mcp_qdrant_storage`. Restarting or rebuilding containers keeps the volume; `docker compose down -v` deletes it.

To use an existing local Ollama instead of the Docker Ollama image:

```powershell
.\scripts\setup.ps1 -Mode Hybrid -OllamaMode Local
```

## 2. Check RagNet MCP

Hybrid setup leaves the server running in Docker:

```powershell
docker compose ps ragnet-mcp
```

The MCP endpoint is:

```text
http://localhost:7331/ragnet-mcp
```

Health check:

```text
http://localhost:7331/health
```

Expected response:

```json
{
  "status": "ok",
  "service": "ragnet-mcp"
}
```

## 3. Use From An Agent

Restart Visual Studio, VS Code, Codex, Codex CLI, or Claude Code if the MCP server is not discovered immediately. Open the agent chat and enable the `ragnet-mcp` server/tools if prompted.

## 4. Index Targets

For normal local workspaces, run the local indexer. `--workspace`/`-w` accepts a workspace root, subdirectory, solution file, or supported file:

```powershell
.\bin\ragnet-indexer.exe index --workspace "D:\Work\Product\Api\Api.sln"
.\bin\ragnet-indexer.exe index --workspace "D:\Work\Product\Api\Api.sln" --profile docs
.\bin\ragnet-indexer.exe index -w "D:\Work\Product\Api\Api.sln" -w "D:\Work\Product\docs\api"
.\bin\ragnet-indexer.exe status --workspace "D:\Work\Product\Api"
.\bin\ragnet-indexer.exe status qdrant
```

To index multiple local targets and save that set as a local group:

```powershell
.\bin\ragnet-indexer.exe index --workspace "D:\Work\Product\Api\Api.sln" --workspace "D:\Work\Product\Admin\Admin.sln" --group my-product
.\bin\ragnet-indexer.exe index -w "D:\Work\Product\Worker" -g my-product -a
.\bin\ragnet-indexer.exe index --group my-product
```

Call the MCP indexing tool only for paths visible inside the `ragnet-mcp` container:

```text
trigger_indexing
```

Pass `workspace_path` with a file or folder inside the workspace you want indexed. For configured multi-project products, pass `workspace_group` instead. The explicit `index_workspace` and `index_workspace_group` tools are also available.

Use `index_profile` when you want to update a narrower slice: `all`, `code`, `docs`, `metadata`, `frontend`, or `tests`. `all` is the default.

After the first run, indexing is incremental. RagNet stores content-hash file fingerprints plus embedding/index metadata in a Qdrant-backed index-state collection and only reindexes files that changed or removes files that disappeared.

Embeddings and chunk payloads are persisted in Qdrant collections named `{CollectionPrefix}-{workspaceId}`. The default collection prefix is `ragnet`, and the workspace ID is derived from the normalized workspace root.

For full Qdrant backup/restore, prefer snapshots. To move one indexed workspace or group without reindexing, use:

```powershell
.\bin\ragnet-indexer.exe workspace export Api --output D:\Backups\ragnet-api
.\bin\ragnet-indexer.exe workspace import --input D:\Backups\ragnet-api --workspace-root E:\Repos\Product\Api
.\bin\ragnet-indexer.exe group export my-product --output D:\Backups\ragnet-product
.\bin\ragnet-indexer.exe group import D:\Backups\ragnet-product --path-map D:\Work\Product=E:\Repos\Product
```

Use `workspace collection --path <file-or-directory>` to see which Qdrant collection backs a local path, and `workspace migrate` to enrich older registry records with export-friendly source metadata when it can be inferred.

Pass `force = true` to `index_workspace` when you want to clear the workspace's Qdrant collection/state and reindex every file. You can also reset manually by deleting the workspace collection in Qdrant, then running `index_workspace` again.

To inspect saved state, call:

```text
get_index_status
```

The indexer writes progress to stderr while keeping the final JSON result on stdout. Use `--no-progress` for quiet automation:

```powershell
.\bin\ragnet-indexer.exe index --workspace "D:\Work\Product\Api" --no-progress
```

This is the preferred shape for local automation or future CI/webhook indexing because the indexer runs where the source files are available.

## 5. Search Code

Call:

```text
search_code
```

or:

```text
hybrid_search
```

Provide a `file_path` inside the indexed workspace and a query. Use `search_profile` to restrict results to `code`, `docs`, `metadata`, `frontend`, `tests`, or `all`.

## 6. Configure a Multi-Project Product

Add a named workspace group to `src/RagNet.Mcp/appsettings.json` or an environment-specific override:

```json
{
  "RagNet": {
    "WorkspaceGroups": [
      {
        "Name": "my-product",
        "Roots": [
          "D:\\Work\\Product\\Api",
          "D:\\Work\\Product\\Worker"
        ],
        "ExcludeDirectories": [
          "artifacts",
          "src\\Generated"
        ]
      }
    ]
  }
}
```

Use `RagNet:Workspace:ExcludeDirectories` for global excludes and `WorkspaceGroups[].ExcludeDirectories` for project/product-specific folders. `.ragnet`, `.git`, `bin`, and `obj` are excluded by default. You can also pass `exclude_directories` directly to `index_workspace` or `index_workspace_group` for a one-off run.

Generated C# files are skipped by default with `RagNet:Workspace:ExcludeFilePatterns` (`*.g.cs`, `*.Designer.cs`) and `RagNet:Workspace:ExcludeAutoGeneratedFiles` for files containing `<auto-generated` near the top. Set `RagNet:Workspace:AllowedPaths` to restrict tool paths and workspace roots; leave it empty for unrestricted local use.

Index the group with:

```text
trigger_indexing
```

Pass `workspace_group = my-product`. Pass `force = true` to reindex every workspace in the group.

Then search with:

```text
scope = named_workspace_group
workspace_group = my-product
```

Default searches stay scoped to the current workspace.

## Planned Language Support

C# is supported first. Planned code analyzers are:

- JavaScript: `*.js`, `*.jsx`, `*.mjs`, `*.cjs`
- TypeScript: `*.ts`, `*.tsx`, `*.mts`, `*.cts`

Deferred until explicitly requested:

- F#: `*.fs`, `*.fsproj`
- VB.NET: `*.vb`, `*.vbproj`

## Planned UI and Markup Support

.NET product indexing should include UI/view markup because those files often contain routes, bindings, validation rules, component composition, localization keys, and view-model references.

Planned inputs:

- Razor and Blazor: `*.cshtml`, `*.razor`, `_ViewImports.cshtml`, `_ViewStart.cshtml`
- Legacy ASP.NET views: `*.vbhtml`, `*.aspx`, `*.ascx`, `*.master`
- XAML UI and resources: `*.xaml`
- SPA/application views and templates: `*.html`, including Angular, Aurelia, and similar framework templates
- React UI files: `*.jsx`, `*.tsx`
- Web styles and assets where relevant: `*.css`, `*.scss`, `*.less`

These analyzers should preserve route templates, directives, model types, component names, props, event handlers, bindings, resource keys, layout relationships, framework-specific template syntax, and linked code-behind files.

## Planned .NET Project Metadata Support

.NET indexing should include more than source files.

Planned inputs:

- Solutions and project files: `*.sln`, `*.slnx`, `*.csproj`
- MSBuild shared files: `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `*.props`, `*.targets`
- NuGet and SDK metadata: `NuGet.config`, `global.json`, `packages.lock.json`
- App/config files: `appsettings*.json`, `launchSettings.json`, `.editorconfig`, `.runsettings`
- Build/deployment files where relevant: Dockerfiles, compose files, CI workflows, publish profiles, and deployment manifests

These files should be indexed with metadata for target frameworks, package references, project references, SDK, nullable/implicit-usings settings, analyzers, source generators, build properties, and configuration keys.

Solution-scoped indexing: pass a `.sln` or `.slnx` path as `--workspace`/`-w` to index only projects and files reachable from that solution instead of scanning every analyzable file under the workspace root. Repeated targets are unioned, so a product can index two solutions plus an explicit docs directory in one run.

## Planned Documentation Support

RagNet should also index documentation as first-class RAG content.

Planned inputs:

- Markdown and MDX: `README.md`, `docs/**/*.md`, `*.md`, `*.mdx`
- HTML: `docs/**/*.html`, generated static docs, exported API docs, and checked-in product documentation sites
- Text docs: `*.txt`, `*.rst`, `*.adoc`
- Later: `*.pdf`, `*.docx`, and other generated documentation artifacts

Documentation analyzers should preserve headings, anchors, page titles, source paths, workspace groups, and `contentType = documentation`.

For one product made from one or more related projects, code and docs should use the same retrieval surface. The analyzers and chunking are different, but MCP tools should support product-scoped searches with filters for `code`, `documentation`, or both, plus retrieval modes such as `docs_first`, `code_first`, and `balanced`.

## Planned Hosting Modes

RagNet is expected to support two indexing modes later:

- Local mode: run a native `RagNet.Indexer` executable on the host so it can read local project directories without Docker mounts, while the web MCP/search service queries Qdrant.
- Hosted/team mode: run the same indexing pipeline from CI or a webhook-triggered worker after GitHub/GitLab/Azure DevOps changes, then let teammates query the shared web search service.

Git metadata should be used when available for repo roots, commit SHAs, and changed-file indexing, but filesystem-based indexing should still work without Git with reduced functionality. Planned Git-only local-change indexing should keep committed baseline chunks separate from staged/working-tree chunks, with retrieval controls to include, exclude, or prefer uncommitted local edits.
