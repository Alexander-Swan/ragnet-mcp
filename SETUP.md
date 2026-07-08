# Setup

This guide sets up RagNet MCP for local use with a Dockerized MCP/search service and a native Windows indexer. That is the recommended mode because the local indexer can read normal host paths like `D:\Work\Product\Api` without mounting source folders into a container, while MCP/search stays available over HTTP for multiple tools.

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
- starts RagNet MCP at `http://localhost:7331`
- pulls the primary embedding model, default `mxbai-embed-large`
- pulls additional compatibility embedding models, default `nomic-embed-text`
- publishes `ragnet-indexer.exe`
- registers MCP configs for supported local tools

If you already run Ollama locally, setup reuses that host Ollama from the MCP container through `host.docker.internal:11434` and starts only Qdrant plus RagNet MCP in Docker. This avoids Docker port conflicts on `11434`.

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
bin\ragnet-indexer.exe
```

## Start The MCP Server

In Hybrid mode, setup starts the MCP server in Docker. Check it with:

```powershell
docker compose ps ragnet-mcp
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

You can index container-visible workspaces from an agent through the MCP tool:

```text
trigger_indexing
```

Pass:

```text
workspace_path = /workspace/Product/Api
```

For normal local Windows paths like `D:\Work\Product\Api`, use the local indexer executable instead. The Docker MCP container cannot read arbitrary host paths unless you mount or sync them:

```powershell
.\bin\ragnet-indexer.exe index --workspace "D:\Work\Product\Api"
```

The indexer prints progress to stderr and writes the final JSON result to stdout. For quiet automation:

```powershell
.\bin\ragnet-indexer.exe index --workspace "D:\Work\Product\Api" --no-progress
```

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

Recommended for local development. Qdrant and RagNet MCP run in Docker. Ollama runs in Docker unless an existing local Ollama is detected. The indexer runs as a local executable so it can read host source folders directly.

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

another Ollama instance is already using `localhost:11434`. In Hybrid mode, rerun setup after this update:

```powershell
.\scripts\setup.ps1 -Mode Hybrid -OllamaMode Local
```

The script will reuse the existing Ollama and start only Qdrant in Docker. If you prefer Docker Ollama instead, stop the host Ollama service first, then rerun setup with `-OllamaMode Docker`.

If Claude Code shows `Failed to connect` after registration, start `ragnet-mcp.exe`; the registration can exist before the server is running.

If Codex or Claude Code does not show the tool immediately, restart the client after registration.

If indexing cannot see a local project in Docker mode, switch to Hybrid mode or mount/sync the source into the container.
