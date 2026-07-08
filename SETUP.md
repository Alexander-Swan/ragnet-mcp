# Setup

This guide sets up RagNet MCP for local use with Dockerized infrastructure and native Windows executables. That is the recommended mode because the native indexer can read normal host paths like `D:\Work\Product\Api` without mounting source folders into a container.

## Prerequisites

Install these first:

- Docker Desktop
- .NET 10 SDK
- PowerShell 7 or Windows PowerShell

Optional agent CLIs:

- Codex CLI, for `codex mcp add`
- Claude Code CLI, for `claude mcp add`

## Recommended Setup

From the repository root:

```powershell
.\scripts\setup.ps1 -Mode Hybrid
```

Hybrid mode does this:

- starts Qdrant at `http://localhost:6333`
- starts Ollama at `http://localhost:11434`, unless something is already listening there
- pulls the primary embedding model, default `mxbai-embed-large`
- pulls additional compatibility embedding models, default `nomic-embed-text`
- publishes `ragnet-mcp.exe`
- publishes `ragnet-indexer.exe`
- registers MCP configs for supported local tools

If you already run Ollama locally, setup reuses that host Ollama and starts only Qdrant in Docker. This avoids Docker port conflicts on `11434`.

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

The default is `-OllamaMode Auto`, which uses local Ollama when `localhost:11434` is already occupied and otherwise starts the Docker Ollama service.

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
artifacts\publish\win-x64\ragnet-mcp\ragnet-mcp.exe
artifacts\publish\win-x64\ragnet-indexer\ragnet-indexer.exe
```

## Start The MCP Server

In Hybrid mode, setup publishes the server but does not leave it running. Start it in a terminal:

```powershell
.\artifacts\publish\win-x64\ragnet-mcp\ragnet-mcp.exe
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

You can index from an agent through the MCP tool:

```text
trigger_indexing
```

Pass:

```text
workspace_path = D:\Work\Product\Api
```

You can also use the local indexer executable:

```powershell
.\artifacts\publish\win-x64\ragnet-indexer\ragnet-indexer.exe index --workspace "D:\Work\Product\Api"
```

The indexer prints progress to stderr and writes the final JSON result to stdout. For quiet automation:

```powershell
.\artifacts\publish\win-x64\ragnet-indexer\ragnet-indexer.exe index --workspace "D:\Work\Product\Api" --no-progress
```

Check index state:

```powershell
.\artifacts\publish\win-x64\ragnet-indexer\ragnet-indexer.exe status --workspace "D:\Work\Product\Api"
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

Recommended for local development. Qdrant and Ollama run in Docker; RagNet MCP and the indexer run as native executables.

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

Publishes native executables only. Use this when Qdrant and Ollama already exist locally or remotely.

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

Run the indexer from source:

```powershell
dotnet run --project .\src\RagNet.Indexer -- index --workspace "D:\Work\Product\Api"
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

another Ollama instance is already using `localhost:11434`. In Hybrid mode, rerun setup after this update:

```powershell
.\scripts\setup.ps1 -Mode Hybrid -OllamaMode Local
```

The script will reuse the existing Ollama and start only Qdrant in Docker. If you prefer Docker Ollama instead, stop the host Ollama service first, then rerun setup with `-OllamaMode Docker`.

If Claude Code shows `Failed to connect` after registration, start `ragnet-mcp.exe`; the registration can exist before the server is running.

If Codex or Claude Code does not show the tool immediately, restart the client after registration.

If indexing cannot see a local project in Docker mode, switch to Hybrid mode or mount/sync the source into the container.
