# AGENTS.md

Instructions for coding agents working in this repository.

## Project Purpose

RagNet MCP is a .NET 10, container-first Model Context Protocol server for semantic code and documentation search. It provides:

- a Docker-hosted MCP/search service;
- a local self-contained indexer executable for host workspaces;
- Qdrant-backed vector storage, workspace registry, group registry, and index state;
- Ollama-backed embeddings;
- analyzers for C#, .NET project metadata, JavaScript/TypeScript/JSX/TSX, markup/views, and documentation.

The default local mode is Hybrid: Qdrant, Ollama, and RagNet MCP run in containers; `ragnet-indexer` runs locally so it can read arbitrary host paths without mounting source folders into the MCP container.

## Repository Layout

- `src/RagNet.Mcp`: ASP.NET Core MCP server and MCP tool surface.
- `src/RagNet.Indexer`: local CLI indexer executable.
- `src/RagNet.Core`: core contracts, models, indexing abstractions, configuration.
- `src/RagNet.Analysis`: analyzers and chunking logic.
- `src/RagNet.Infrastructure`: Qdrant, Ollama, git/source identity, state stores, registries.
- `src/RagNet.Composition`: dependency injection wiring.
- `tests/RagNet.Mcp.Tests`: unit and integration-style tests.
- `scripts`: setup and MCP client registration scripts.

Keep interfaces in `.Interfaces` namespaces under their owning domain. Keep implementations out of those namespaces.

## Development Rules

- Prefer existing project patterns over new abstractions.
- Keep changes scoped to the requested behavior.
- Use `rg` for searching files and text.
- Use `apply_patch` for manual file edits.
- Do not use or reintroduce legacy `ragcode` names, commands, or MCP tools.
- Do not write generated build outputs into source folders. Build artifacts belong in ignored output folders.
- Keep public CLI behavior clear and friendly; no raw stack traces for expected service or configuration failures.
- Preserve self-contained executables under the repository `bin` folder when publishing.

## Naming And API Conventions

- C# methods should use standard .NET naming.
- MCP tool names should be set with `McpServerTool(Name = "...")`; do not rename C# methods to snake case just to match MCP names.
- Avoid hardcoded language names. Use constants or enums. If a literal is analyzer-specific, keep it as a constant field in that analyzer.
- Schema versions should be stored as numbers, not string duplicates.

## Setup And Runtime

Use the setup scripts rather than hand-editing generated local state:

```powershell
.\scripts\setup.ps1
```

```bash
./scripts/setup.sh --mode Hybrid --container-runtime Docker --ollama-mode Docker --skip-register --non-interactive
```

Setup scripts must support both interactive use and explicit arguments.

Default mode:

- Qdrant runs in a container.
- Ollama runs in a container unless explicitly configured as local.
- RagNet MCP runs in a container.
- The indexer runs locally.

Docker Desktop and Rancher Desktop with a nerdctl-compatible CLI should both be supported where possible.

## Configuration And Environment

Prefer configuration files over generated environment variables when possible.

Current boundary:

- Setup-generated Docker Compose values are written to repo-local `.env`.
- `.env` is ignored by git.
- `.env.example` documents supported Compose keys.
- Keep process/runtime variables where the underlying tool requires them, such as `ASPNETCORE_URLS` and .NET CLI setup variables.
- Keep `setup.sh` input environment variables for CI compatibility, but do not export generated `RAGNET_*` values just to feed Compose.

Important Compose keys:

- `RAGNET_MCP_PORT`
- `RAGNET_OLLAMA_BASE_URL`
- `RAGNET_EMBEDDING_MODEL`

## Indexing Behavior

The indexer should support:

- indexing one or more workspace paths with `-w|--workspace`;
- indexing the current directory with `-c|--current`;
- assigning indexed workspaces to groups with `-g|--group`;
- adding to existing groups with `-a|--add`;
- listing groups and workspaces in neat tables;
- deleting persisted groups and workspaces using `delete group <name>` and `delete workspace <name>`;
- dry runs with `--dry-run`;
- incremental reindexing by default when a workspace was already indexed;
- forcing full reindex with `--force`.

If a workspace name is provided instead of a path:

- if it is already registered, resolve it and run incremental indexing;
- if it is not registered, show a clear error requiring a full path.

Workspace registry, group registry, and index state should be backed by Qdrant. Do not add new `.ragnet/state.json` dependencies.

## Scopes And Source Selection

When a path points to a solution file, index the sources related to that solution rather than the entire repository root. Users should still be able to include multiple solution files or additional exact paths in the same workspace.

Support excluding directories manually. Exclusions must be respected by indexing, dry-run estimates, and incremental reindexing.

Keep a TODO for local uncommitted git changes with clear separation from committed content. For now, git-aware behavior may be limited when no git repository exists, but non-git workspaces should still function where possible.

## Analyzers

Supported analyzer areas:

- C#
- .NET project metadata and related project files
- JavaScript/TypeScript, including JSX and TSX
- application markup/views, including HTML-like application files
- documentation, including Markdown, HTML, text, and similar formats

Hold off on F# and VB support until explicitly requested.

For ambiguous extensions, use content-based classification plus config overrides. Do not rely only on file extension to decide whether a file is documentation or application source.

## Retrieval

Code and documentation may share the same product/workspace-level retrieval surface, but chunks should carry content type/profile metadata so retrieval can filter, blend, or rank them differently.

Search should support selecting:

- all indexed workspaces;
- a named workspace;
- a named group;
- a specific set of workspaces that together make one product.

Keep result packing token-aware. Avoid returning huge chunks that can exceed model or embedding context limits.

## MCP Tools

The MCP server should expose both search and indexing operations so an agent can trigger indexing without shell access.

Current tool categories should include:

- indexing trigger/status tools;
- semantic and hybrid search tools;
- code context tools;
- symbol/detail tools.

Prefer streamable HTTP for MCP transport everywhere it is supported.

## Testing

Before committing meaningful code changes, run focused tests when possible:

```powershell
dotnet test .\RagNet.Mcp.sln
```

For setup-only or docs-only changes, at least run syntax/config checks relevant to the changed files:

```powershell
powershell -NoProfile -Command "`$null = [scriptblock]::Create((Get-Content -Raw .\scripts\setup.ps1)); 'setup.ps1 syntax ok'"
docker compose config
git diff --check
```

If Bash checks cannot run on Windows because WSL or Git Bash is blocked, state that clearly in the final response.

## Git

- Keep commits small and logically grouped.
- Do not rewrite history unless the user explicitly asks.
- Do not revert user changes.
- If the working tree is dirty, inspect changes before editing and avoid touching unrelated files.
- When a feature is complete, commit it if the user asked for commits or if the current task is part of an ongoing committed implementation flow.

## Documentation

When setup behavior changes, update both `README.md` and `SETUP.md` if the behavior affects users.

Keep instructions explicit about:

- Docker vs Hybrid vs Native mode;
- local Ollama vs containerized Ollama;
- configurable MCP localhost port;
- generated `.env` behavior;
- how to run the local indexer executable.

