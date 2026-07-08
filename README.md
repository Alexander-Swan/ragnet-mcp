# RagNet MCP

RagNet MCP is a .NET 10, container-first Model Context Protocol server for local semantic code search. It is inspired by `rag-code-mcp`, but uses ASP.NET Core, the official .NET MCP SDK, Roslyn for C# analysis, Ollama for local embeddings, and Qdrant as the durable vector database.

## What It Runs

The default setup is hybrid:

- Docker starts `qdrant` on `http://localhost:6333`
- Docker starts `ollama` on `http://localhost:11434`, unless Hybrid setup is configured to use local Ollama
- Docker starts `ragnet-mcp` on `http://localhost:7331`
- .NET publishes the local `ragnet-indexer` executable under `bin`

Native MCP mode is still available, but the recommended local shape is Docker MCP plus the local indexer. The containerized MCP server should be treated as the shared search/query service; use the local indexer for host paths like `D:\Work\Product\Api`.

MCP endpoint:

```text
http://localhost:7331/ragnet-mcp
```

Health endpoint:

```text
http://localhost:7331/health
```

## Quick Start

```powershell
.\scripts\setup.ps1
```

The setup script starts Qdrant, RagNet MCP, and Ollama in Docker, publishes the local indexer executable, pulls the default Ollama embedding model plus `nomic-embed-text` for compatibility, writes repo-local MCP registration files for Visual Studio and VS Code, registers Codex/Codex CLI when `codex` is available on PATH, and registers Claude Code when `claude` is available on PATH.

If Ollama is already running on `localhost:11434`, Hybrid setup reuses it and starts only Qdrant in Docker.

To force local Ollama instead of the Docker Ollama image:

```powershell
.\scripts\setup.ps1 -Mode Hybrid -OllamaMode Local
```

In Hybrid mode, setup leaves `ragnet-mcp` running in Docker. Check it with:

```powershell
docker compose ps ragnet-mcp
```

See [SETUP.md](SETUP.md) for the full setup, verification, indexing, and troubleshooting flow.

## MCP Tools

The initial tool surface is:

- `index_workspace`
- `index_workspace_group`
- `trigger_indexing`
- `get_index_status`
- `search_code`
- `hybrid_search`
- `get_code_context`
- `get_symbol_details`

The current implementation indexes C# files through Roslyn, stores embeddings and chunk payloads in Qdrant, and reconstructs search results from Qdrant payload data. Hybrid search asks Qdrant for semantic candidates and applies a local lexical boost over the stored chunk content before returning results.

Agents can use `trigger_indexing` as the generic indexing entry point. Pass `workspace_path` to index one workspace, or `workspace_group` to index a configured multi-project product. The older `index_workspace` and `index_workspace_group` tools remain available as explicit lower-level variants.

In the default Hybrid setup, `ragnet-mcp` runs in Docker. Use MCP indexing tools only for paths visible inside that container. For ordinary local host paths, use `bin\ragnet-indexer.exe`; it writes to the same Qdrant collections used by MCP search.

Indexing and search support conservative profiles: `all`, `code`, `docs`, `metadata`, `frontend`, and `tests`. Use `index_profile` to update one profile at a time, and `search_profile` to constrain `search_code` or `hybrid_search` results. `all` is the default.

## Incremental Indexing

After the first `index_workspace` run, RagNet stores content-hash file fingerprints in `.ragnet/state.json` under the indexed workspace. Later indexing runs compare the current file list with that state and only re-analyze/re-embed files that changed. Deleted files are removed from the vector store before the state is saved.

The state file also records the embedding model, index/analyzer schema version, and last saved timestamp. If the configured embedding model or schema version changes, RagNet automatically clears the workspace vectors and performs a full reindex. You can force the same lifecycle manually by passing `force: true` to `index_workspace` or `index_workspace_group`. Profile-scoped indexing updates only files in that profile and requires a compatible existing all-profile state.

Qdrant collections are named deterministically as `{CollectionPrefix}-{workspaceId}`, where `workspaceId` is derived from the normalized workspace root rather than the raw path. The default prefix is `ragnet`. A forced/full reindex deletes the workspace collection and recreates it with the current embedding vector size. To manually reset a workspace index, run `index_workspace` with `force: true`, or delete the matching collection from Qdrant and re-run indexing.

Use `get_index_status` with a file or directory inside a workspace to inspect whether state exists, the last indexed timestamp, indexed file count, embedding model, schema version, and whether the stored state requires a full reindex.

## Solution Layout

The solution is split into logical assemblies:

- `RagNet.Core`: options, contracts, workspace/indexing models, and storage/analyzer abstractions.
- `RagNet.Analysis`: language analyzers, starting with the Roslyn C# analyzer.
- `RagNet.Infrastructure`: workspace detection, indexing orchestration, Ollama embeddings, and vector-store implementations.
- `RagNet.Composition`: shared dependency-injection wiring for the web host and indexer executable.
- `RagNet.Mcp`: ASP.NET Core host, MCP transport, endpoints, and tool declarations.
- `RagNet.Indexer`: native CLI executable for local/CI indexing using the same pipeline as the MCP tools.

## Indexer Executable

Agents can trigger indexing through the `trigger_indexing` MCP tool. Humans, scripts, CI jobs, and future webhook workers can run the same pipeline through `ragnet-indexer`:

```powershell
.\bin\ragnet-indexer.exe index --workspace "D:\Work\Product\Api"
.\bin\ragnet-indexer.exe index --workspace "D:\Work\Product\Api" --force
.\bin\ragnet-indexer.exe index-group --group my-product
.\bin\ragnet-indexer.exe status --workspace "D:\Work\Product\Api"
```

The CLI prints progress for each indexing phase to stderr and leaves the final JSON result on stdout for scripts. Pass `--no-progress` to suppress progress output.

The indexer is intended to run where source files are accessible: directly on a developer machine for local projects, or in CI/webhook workers after checking out a repository. The web MCP/search service remains HTTP-based and queries Qdrant.

## Planned Architecture TODO

RagNet should support both local-only and hosted/team usage without forcing the same indexing mechanics into both modes:

- Keep the search/MCP portion web-based so multiple teammates and IDEs can query the same indexed product concurrently.
- Split indexing into a reusable indexing pipeline plus a separate `RagNet.Indexer` executable/worker. Local mode should run this executable on the host so it can read local project files directly without mounting source folders into a container.
- Cloud/team mode should run the same indexing pipeline in CI, a worker, or a webhook-triggered job that checks out the repository, performs incremental indexing, and writes vectors into shared Qdrant.
- Prefer Git metadata when available: repository root, remote URL, branch, commit SHA, changed files, and deleted files. The system should still work without Git, but with reduced functionality based on filesystem scanning and content fingerprints.
- Add GitHub/GitLab/Azure DevOps-style change notifications later. Push/webhook events should enqueue incremental reindexing for affected repositories/workspaces instead of requiring a full scan every time.
- Store enough source metadata in vector payloads for hosted search: repository URL, commit SHA, relative path, symbol details, line numbers, and chunk content. A cloud-hosted search service cannot read `D:\...` local files, so context must come from indexed payloads or a repo checkout/object store.
- Keep full Docker indexing optional. Since `ragnet-mcp` runs in Docker by default, arbitrary host workspace paths should be indexed with the local indexer unless those paths are mounted or synced into the container.

## Documentation Indexing TODO

RagNet should index project documentation as first-class RAG content, not only source code.

For a real product that spans one or more related projects, code and documentation should participate in the same retrieval surface. They can be parsed, chunked, stored, and ranked with different strategies, but agents should be able to issue one product-scoped query and receive the relevant docs plus implementation chunks together.

Planned documentation inputs:

- Markdown: `README.md`, `docs/**/*.md`, `*.md`, `*.mdx`
- HTML: `docs/**/*.html`, generated static docs, exported API docs, and product documentation sites checked into a repository
- Plain text and lightweight docs: `*.txt`, `*.rst`, `*.adoc`
- Later binary/document formats: `*.pdf`, `*.docx`, and generated documentation artifacts

Documentation indexing should use document-specific analyzers instead of code analyzers:

- Split Markdown/MDX by headings, paragraphs, lists, tables, and fenced code blocks.
- Split HTML by document outline, headings, semantic sections, paragraphs, tables, and code/pre blocks after stripping navigation chrome where possible.
- Preserve heading hierarchy, page title, anchors, source path, workspace group, repository metadata, and `contentType = documentation` in vector payloads.
- Add search filters for `code`, `documentation`, or both, plus retrieval modes such as `docs_first`, `code_first`, and `balanced`.
- Add a general context tool or extend existing context tools so agents can retrieve project documentation alongside code context.

Ambiguous extensions such as `*.html`, `*.htm`, and `*.mdx` are classified by analyzer confidence instead of path alone. Documentation and markup analyzers both inspect content signals, then configurable path overrides can force known folders toward documentation or application markup:

```json
{
  "RagNet": {
    "Classification": {
      "DocumentationPathPatterns": [
        "**/docs/**",
        "**/knowledge-base/**"
      ],
      "ApplicationMarkupPathPatterns": [
        "**/src/**",
        "**/client-app/templates/**"
      ]
    }
  }
}
```

## Language Support

C# is the MVP language and is analyzed through Roslyn.

Planned additional code analyzers:

- JavaScript: `*.js`, `*.jsx`, `*.mjs`, `*.cjs`
- TypeScript: `*.ts`, `*.tsx`, `*.mts`, `*.cts`

JavaScript and TypeScript should use their own analyzer implementation rather than the C# analyzer, ideally preserving imports, exports, classes, functions, React components, route handlers, and test boundaries.

Deferred until explicitly requested:

- F#: `*.fs` files and `*.fsproj` projects
- VB.NET: `*.vb` files and `*.vbproj` projects

## UI and Markup Analyzer TODO

.NET product indexing should also include UI/view markup because it often contains routes, bindings, validation rules, component composition, localization keys, and view-model references.

Planned UI and markup inputs:

- Razor and Blazor: `*.cshtml`, `*.razor`, `_ViewImports.cshtml`, `_ViewStart.cshtml`
- Legacy ASP.NET views: `*.vbhtml`, `*.aspx`, `*.ascx`, `*.master`
- XAML UI and resources: `*.xaml`
- SPA/application views and templates: `*.html`, including Angular, Aurelia, and similar framework templates
- React UI files: `*.jsx`, `*.tsx`
- Web styles and assets where relevant: `*.css`, `*.scss`, `*.less`

These analyzers should preserve route templates, directives, model types, component names, props, event handlers, bindings, resource keys, layout relationships, framework-specific template syntax, and linked code-behind files. Razor/Blazor analysis should eventually connect markup chunks to generated C# symbols where possible.

## .NET Project Metadata TODO

.NET indexing should treat project structure and build configuration as first-class context, not only language source files.

Planned .NET project inputs:

- Solutions and project files: `*.sln`, `*.slnx`, `*.csproj`
- MSBuild shared files: `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `*.props`, `*.targets`
- NuGet and SDK metadata: `NuGet.config`, `global.json`, `packages.lock.json`
- Application configuration: `appsettings*.json`, `launchSettings.json`, `.editorconfig`, `.runsettings`
- Build and deployment helpers where relevant: Dockerfiles, compose files, CI workflow files, publish profiles, and deployment manifests

These files should be chunked and indexed with metadata such as target frameworks, package references, project references, SDK, nullable/implicit-usings settings, analyzers, source generators, build properties, and configuration keys. This lets retrieval answer questions about dependencies, build behavior, project relationships, runtime configuration, and solution layout without relying only on source-code chunks.

## Multi-Project Products

RagNet can search a product that spans multiple repos or project roots by configuring named workspace groups:

```json
{
  "RagNet": {
    "WorkspaceGroups": [
      {
        "Name": "billing-product",
        "Roots": [
          "D:\\Work\\Products\\Billing.Api",
          "D:\\Work\\Products\\Billing.Workers"
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

Directory exclusions are merged in this order:

- global `RagNet:Workspace:ExcludeDirectories`
- workspace-group `ExcludeDirectories`
- ad hoc `exclude_directories` passed to `index_workspace` or `index_workspace_group`

Entries can be directory names, such as `bin`, or workspace-relative paths, such as `src\\Generated`.

File exclusions are configured with `RagNet:Workspace:ExcludeFilePatterns`. Defaults skip common generated .NET outputs such as `*.g.cs` and `*.Designer.cs`. `RagNet:Workspace:ExcludeAutoGeneratedFiles` also skips C# files whose header contains `<auto-generated`.

Set `RagNet:Workspace:AllowedPaths` to restrict all tool paths and workspace roots to approved filesystem roots. An empty list keeps the default unrestricted behavior:

```json
{
  "RagNet": {
    "Workspace": {
      "AllowedPaths": [
        "D:\\Work"
      ]
    }
  }
}
```

Search defaults to the current workspace only. To search beyond the current workspace, explicitly pass one of these scopes:

- `current_workspace`
- `explicit_workspace_root`
- `named_workspace_group`
- `all_indexed_workspaces`

For a group search, pass `scope: "named_workspace_group"` and `workspace_group: "billing-product"` to `search_code` or `hybrid_search`.

Use `search_profile` when you want a narrower retrieval surface, such as `docs`, `metadata`, `frontend`, or `tests`. `content_type` remains available for lower-level filters like `documentation`, `project_metadata`, or `markup`.

## Visual Studio and GitHub Copilot

Visual Studio registration is repo-local:

```json
{
  "servers": [
    {
      "name": "ragnet-mcp",
      "transport": "http",
      "url": "http://localhost:7331/ragnet-mcp"
    }
  ]
}
```

This is written to `.mcp.json` and uses streamable HTTP.

## VS Code and GitHub Copilot

VS Code registration is repo-local:

```json
{
  "servers": {
    "ragnet-mcp": {
      "type": "http",
      "url": "http://localhost:7331/ragnet-mcp"
    }
  }
}
```

This is written to `.vscode/mcp.json`.

## Codex and Codex CLI

Codex registration is user-local and uses the installed CLI:

```powershell
.\scripts\register-codex.ps1
```

This runs:

```powershell
codex mcp add ragnet-mcp --url http://localhost:7331/ragnet-mcp
```

Codex stores the MCP server in `$HOME\.codex\config.toml`, which is shared by the Codex desktop app and Codex CLI. Restart Codex after registration if the server is not discovered immediately.

## Claude Code

Claude Code registration is user-local by default and uses the installed CLI:

```powershell
.\scripts\register-claude.ps1
```

This runs:

```powershell
claude mcp add --scope user --transport http ragnet-mcp http://localhost:7331/ragnet-mcp
```

Use `-Scope local` or `-Scope project` when you want Claude Code registration scoped differently. Restart Claude Code after registration if the server is not discovered immediately.

## Setup Modes

Hybrid Docker MCP plus local indexer is the default. You can still choose full Docker mode or native-only publishing:

```powershell
.\scripts\setup.ps1 -Mode Hybrid
.\scripts\setup.ps1 -Mode Docker
.\scripts\setup.ps1 -Mode Native
```

Hybrid publishes the local indexer to:

```text
bin/ragnet-indexer.exe
```

Native mode publishes both Windows executables to:

```text
bin/ragnet-mcp.exe
bin/ragnet-indexer.exe
```

## Development

```powershell
$env:DOTNET_CLI_HOME = "$PWD\.dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
dotnet restore .\RagNet.Mcp.sln
dotnet build .\RagNet.Mcp.sln
```
