# Setup

This guide sets up RagNet MCP for local use with a Dockerized MCP/search service and a native Windows indexer. That is the recommended mode because the local indexer can read normal host paths like `D:\Work\Product\Api` without mounting source folders into a container, while MCP/search stays available over HTTP for multiple tools.

## Prerequisites

Install these first:

- Docker Desktop, or Rancher Desktop with a nerdctl-compatible CLI
- .NET 10 SDK
- PowerShell 7 or Windows PowerShell

Optional agent CLIs:

- Codex CLI, for `codex mcp add`
- Claude Code CLI, for `claude mcp add`

## Recommended Setup

From the repository root:

```powershell
.\scripts\setup.ps1
```

When setup runs in an interactive terminal with no arguments, it asks for:

- setup mode: Hybrid, Docker, or Native
- container runtime: Docker Desktop/docker compose, Rancher Desktop/nerdctl-compatible, or Auto
- Ollama mode/source: Docker, Local, or Auto
- MCP localhost port, default `7331`
- additional embedding models
- MCP client registration: all supported clients, repo-local files only, or skip

It then shows a summary, including the selected ports, and asks for confirmation before applying changes. Press Enter through the prompts for the recommended Hybrid setup.

Hybrid mode does this by default:

- starts Qdrant at `http://localhost:6333`
- starts Ollama at `http://localhost:11434` in a container
- starts RagNet MCP at `http://localhost:7331` by default
- pulls the primary embedding model, default `mxbai-embed-large`
- pulls additional compatibility embedding models, default `nomic-embed-text`
- publishes `ragnet-indexer.exe`
- registers MCP configs for supported local tools

By default, all services except the indexer run in containers: Qdrant, Ollama, and RagNet MCP. Before starting them, setup checks the required ports. If a port is already open because the matching compose service is already running, setup reuses/updates that service. If the port belongs to something else, setup fails early with a clear conflict message.

If you already run Ollama locally and intentionally want to reuse it, choose `Local` or `Auto`. Local Ollama is reached from the MCP container through `host.docker.internal:11434`.

To choose a different localhost port for the MCP HTTP service:

```powershell
.\scripts\setup.ps1 -Mode Hybrid -McpPort 8331
```

On Linux/macOS:

```bash
MCP_PORT=8331 ./scripts/setup.sh Hybrid
```

For non-interactive CI or scripted setup, pass explicit arguments:

```powershell
.\scripts\setup.ps1 -Mode Hybrid -ContainerRuntime Docker -OllamaMode Docker -SkipRegister -NonInteractive
```

On Linux/macOS:

```bash
NON_INTERACTIVE=1 SKIP_REGISTER=1 CONTAINER_RUNTIME=Docker OLLAMA_MODE=Docker ./scripts/setup.sh Hybrid
```

To explicitly use local Ollama and never start the Docker Ollama image:

```powershell
.\scripts\setup.ps1 -Mode Hybrid -OllamaMode Local
```

On Linux/macOS:

```bash
OLLAMA_MODE=Local ./scripts/setup.sh Hybrid
```

To require Docker Ollama:

```powershell
.\scripts\setup.ps1 -Mode Hybrid -OllamaMode Docker
```

On Linux/macOS:

```bash
OLLAMA_MODE=Docker ./scripts/setup.sh Hybrid
```

`-OllamaMode Auto` is available when you prefer the older behavior: use local Ollama when `localhost:11434` is already occupied, otherwise start the containerized Ollama service.

To use Rancher Desktop or another nerdctl-compatible runtime:

```powershell
.\scripts\setup.ps1 -Mode Hybrid -ContainerRuntime Nerdctl
```

On Linux/macOS:

```bash
CONTAINER_RUNTIME=Nerdctl ./scripts/setup.sh Hybrid
```

To override the extra models pulled by setup:

```powershell
.\scripts\setup.ps1 -AdditionalEmbeddingModels @("nomic-embed-text", "all-minilm")
```

On Linux/macOS:

```bash
ADDITIONAL_EMBEDDING_MODELS="nomic-embed-text all-minilm" ./scripts/setup.sh Hybrid
```

The published executables are written to:

```text
bin\ragnet-indexer.exe
```

## Start The MCP Server

In Hybrid mode, setup starts the MCP server in Docker. Check it with:

```powershell
docker compose ps ragnet-mcp
```

For nerdctl-compatible runtimes:

```powershell
nerdctl compose ps ragnet-mcp
```

Then check:

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

The MCP endpoint is:

```text
http://localhost:7331/ragnet-mcp
```

## Registered Clients

Setup registers `ragnet-mcp` where the relevant client is available:

- Visual Studio: `.mcp.json`
- VS Code: `.vscode/mcp.json`
- Codex and Codex CLI: `$HOME\.codex\config.toml`
- Claude Code: user-scope Claude MCP config

All direct registrations use HTTP/streamable HTTP.

In the interactive installer, choose `RepoOnly` to write only `.mcp.json` and `.vscode/mcp.json`, or `Skip` to skip registration. In non-interactive runs, use:

```powershell
.\scripts\setup.ps1 -RegisterClients RepoOnly
.\scripts\setup.ps1 -SkipRegister
```

On Linux/macOS:

```bash
REGISTER_CLIENTS=RepoOnly ./scripts/setup.sh Hybrid
SKIP_REGISTER=1 ./scripts/setup.sh Hybrid
```

To re-run only registration:

```powershell
.\scripts\register-copilot.ps1
```

Individual registration scripts:

```powershell
.\scripts\register-codex.ps1
.\scripts\register-claude.ps1
```

Restart Visual Studio, VS Code, Codex, or Claude Code if the MCP server is not discovered immediately.

## Index A Workspace

You can index container-visible workspaces from an agent through the MCP tool:

```text
trigger_indexing
```

Pass:

```text
workspace_path = /workspace/Product/Api
```

For normal local Windows paths like `D:\Work\Product\Api`, use the local indexer executable instead. The Docker MCP container cannot read arbitrary host paths unless you mount or sync them. `--workspace`/`-w` is an index target and can point at a workspace root, subdirectory, solution file, or supported file:

```powershell
.\bin\ragnet-indexer.exe index --workspace "D:\Work\Product\Api\Api.sln"
.\bin\ragnet-indexer.exe index --current
.\bin\ragnet-indexer.exe index -w "D:\Work\Product\Api\Api.sln" -w "D:\Work\Product\docs\api"
```

Use `--current` or `-c` to index the current directory. Repeat `--workspace` to union multiple targets. Two solution files in the same repo index only those two solution graphs. If no target is provided and the current directory is inside an already indexed workspace, the CLI reindexes that workspace incrementally. Add `--group` to save that target set under a local group name for future runs:

```powershell
.\bin\ragnet-indexer.exe index --workspace "D:\Work\Product\Api\Api.sln" --workspace "D:\Work\Product\Admin\Admin.sln" --group my-product
.\bin\ragnet-indexer.exe index -w "D:\Work\Product\Worker" -g my-product -a
.\bin\ragnet-indexer.exe index --group my-product
```

Preview a run without writing embeddings, vectors, index state, registry records, or local groups:

```powershell
.\bin\ragnet-indexer.exe index --workspace "D:\Work\Product\Api\Api.sln" --dry-run
```

Dry-run output includes the resolved workspace root, target paths, scanned file count, chunks that would be indexed, index profile, state compatibility, whether a full reindex would be required, and changed/deleted/unchanged file counts.

List local indexer groups and indexed workspaces as tables, or remove them:

```powershell
.\bin\ragnet-indexer.exe list groups
.\bin\ragnet-indexer.exe list workspaces
.\bin\ragnet-indexer.exe create group my-product -w Api -w Admin
.\bin\ragnet-indexer.exe delete group my-product
.\bin\ragnet-indexer.exe delete workspace "D:\Work\Product\Api"
```

`create group <name>` creates or replaces a local group from workspaces that already exist in the Qdrant workspace registry. Use indexed workspace names from `list workspaces`, full indexed workspace roots, or `--current` when the current directory is inside an indexed workspace. Add `--add`/`-a` to append to an existing local group.

Configured groups are listed as read-only. `delete workspace` removes the Qdrant vector collection, Qdrant registry record, and Qdrant index-state point.

The indexer prints progress to stderr and writes index/status/delete results to stdout. For quiet automation:

```powershell
.\bin\ragnet-indexer.exe index --workspace "D:\Work\Product\Api" --no-progress
```

Embedding requests run as concurrent Ollama batches while indexing. Tune concurrent batch count with `RagNet:Indexing:MaxEmbeddingConcurrency`, embedding batch size with `RagNet:Indexing:MaxEmbeddingBatchSize`, and Qdrant write size with `RagNet:Qdrant:UpsertBatchSize` in `appsettings.json` or an environment-specific override. Defaults are `4`, `16`, and `256`.

Check index state:

```powershell
.\bin\ragnet-indexer.exe status --workspace "D:\Work\Product\Api"
```

## Search

From an MCP client, use:

```text
search_code
```

or:

```text
hybrid_search
```

Provide a `file_path` inside an indexed workspace and a natural-language query.

## Setup Modes

### Hybrid

```powershell
.\scripts\setup.ps1 -Mode Hybrid
```

Recommended for local development. Qdrant, Ollama, and RagNet MCP run in containers by default. The indexer runs as a local executable so it can read host source folders directly. Use `-OllamaMode Local` or `-OllamaMode Auto` only when you intentionally want to reuse a host Ollama instance.

Use Rancher Desktop/nerdctl-compatible compose commands instead of Docker Desktop:

```powershell
.\scripts\setup.ps1 -Mode Hybrid -ContainerRuntime Nerdctl
```

Use local Ollama instead of the Docker image:

```powershell
.\scripts\setup.ps1 -Mode Hybrid -OllamaMode Local
```

### Docker

```powershell
.\scripts\setup.ps1 -Mode Docker
```

Runs Qdrant, Ollama, and `ragnet-mcp` in Docker. Use this only when the code to index is available inside the container through a mount, checkout, or sync process.

### Native

```powershell
.\scripts\setup.ps1 -Mode Native
```

Publishes native `ragnet-mcp` and `ragnet-indexer` executables only. Use this when Qdrant and Ollama already exist locally or remotely, or when you explicitly want MCP outside Docker.

## Common Commands

Start infrastructure only:

```powershell
docker compose up -d qdrant ollama
```

Pull the default embedding model:

```powershell
docker exec ollama ollama pull mxbai-embed-large
```

Pull the default embedding model into local Ollama:

```powershell
ollama pull mxbai-embed-large
```

Start the MCP server from source:

```powershell
dotnet run --project .\src\RagNet.Mcp\RagNet.Mcp.csproj
```

Run the published indexer executable:

```powershell
.\bin\ragnet-indexer.exe index --workspace "D:\Work\Product\Api"
```

## Troubleshooting

If the agent cannot connect, make sure `ragnet-mcp.exe` is running and `http://localhost:7331/health` responds.

If indexing fails to connect to Qdrant or Ollama, check containers:

```powershell
docker compose ps
```

If setup fails with this message:

```text
ports are not available: exposing port TCP 0.0.0.0:11434
```

another Ollama instance is already using `localhost:11434`. The default Hybrid setup expects containerized Ollama, so either stop the conflicting host service or intentionally opt into local Ollama:

```powershell
.\scripts\setup.ps1 -Mode Hybrid -OllamaMode Local
```

The script will reuse the existing Ollama and start Qdrant plus RagNet MCP in containers. If you prefer containerized Ollama, stop the host Ollama service first, then rerun setup with `-OllamaMode Docker`.

If Claude Code shows `Failed to connect` after registration, start `ragnet-mcp.exe`; the registration can exist before the server is running.

If Codex or Claude Code does not show the tool immediately, restart the client after registration.

If indexing cannot see a local project in Docker mode, switch to Hybrid mode or mount/sync the source into the container.
