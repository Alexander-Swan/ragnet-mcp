# RagNet MCP

RagNet MCP is a .NET 10, container-first Model Context Protocol server for local semantic code search. It is inspired by `rag-code-mcp`, but uses ASP.NET Core, the official .NET MCP SDK, Roslyn for C# analysis, Ollama for local embeddings, and Qdrant as the durable vector database.

## What It Runs

The default setup is hybrid:

- Docker starts `qdrant` on `http://localhost:6333`
- Docker starts `ollama` on `http://localhost:11434`, unless Hybrid setup is configured to use local Ollama
- .NET publishes native `ragnet-mcp` and `ragnet-indexer` executables under `artifacts/publish`

Full Docker mode is still available, but it requires explicit source mounts or another source-sync strategy before the containerized MCP server can read arbitrary host workspaces.

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

The setup script starts Qdrant/Ollama, publishes the native web service and indexer executables, pulls the default Ollama embedding model, writes repo-local MCP registration files for Visual Studio and VS Code, registers Codex/Codex CLI when `codex` is available on PATH, and registers Claude Code when `claude` is available on PATH.

If Ollama is already running on `localhost:11434`, Hybrid setup reuses it and starts only Qdrant in Docker.

To force local Ollama instead of the Docker Ollama image:

```powershell
.\scripts\setup.ps1 -Mode Hybrid -OllamaMode Local
```

In Hybrid mode, setup publishes `ragnet-mcp.exe` but does not leave it running. Start it with:

```powershell
.\artifacts\publish\win-x64\ragnet-mcp\ragnet-mcp.exe
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

## Incremental Indexing

After the first `index_workspace` run, RagNet stores content-hash file fingerprints in `.ragnet/state.json` under the indexed workspace. Later indexing runs compare the current file list with that state and only re-analyze/re-embed files that changed. Deleted files are removed from the vector store before the state is saved.

The state file also records the embedding model, index/analyzer schema version, and last saved timestamp. If the configured embedding model or schema version changes, RagNet automatically clears the workspace vectors and performs a full reindex. You can force the same lifecycle manually by passing `force: true` to `index_workspace` or `index_workspace_group`.

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
dotnet run --project .\src\RagNet.Indexer -- index --workspace D:\Work\Product\Api
dotnet run --project .\src\RagNet.Indexer -- index --workspace D:\Work\Product\Api --force
dotnet run --project .\src\RagNet.Indexer -- index-group --group my-product
dotnet run --project .\src\RagNet.Indexer -- status --workspace D:\Work\Product\Api
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
- Keep Docker-only mode optional. If `ragnet-mcp` itself runs in Docker, workspace folders must be mounted or synced explicitly; the preferred local mode is native indexer plus web search service connected to Dockerized Qdrant/Ollama.

## Language Support

C# is the MVP language and is analyzed through Roslyn.

Planned .NET language support:

- F#: `*.fs` files and `*.fsproj` projects
- VB.NET: `*.vb` files and `*.vbproj` projects

The analyzer registration in `Program.cs` includes TODO placeholders for future `FSharpAnalyzer` and `VisualBasicAnalyzer` implementations.

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

## Native Publish

Hybrid native/container setup is the default. You can still choose full Docker mode or native-only publishing:

```powershell
.\scripts\setup.ps1 -Mode Hybrid
.\scripts\setup.ps1 -Mode Docker
.\scripts\setup.ps1 -Mode Native
```

The Windows executables are published to:

```text
artifacts/publish/win-x64/ragnet-mcp
artifacts/publish/win-x64/ragnet-indexer
```

## Development

```powershell
$env:DOTNET_CLI_HOME = "$PWD\.dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
dotnet restore .\RagNet.Mcp.sln
dotnet build .\RagNet.Mcp.sln
```
