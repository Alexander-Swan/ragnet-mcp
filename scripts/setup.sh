#!/usr/bin/env bash
set -euo pipefail

MODE="${1:-Docker}"
EMBEDDING_MODEL="${EMBEDDING_MODEL:-mxbai-embed-large}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

export DOTNET_CLI_HOME="$REPO_ROOT/.dotnet-home"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1

cd "$REPO_ROOT"

if [[ "$MODE" == "Docker" ]]; then
  docker compose up -d --build
  docker exec ollama ollama pull "$EMBEDDING_MODEL"
else
  dotnet restore ./RagNet.Mcp.sln
  dotnet publish ./src/RagNet.Mcp/RagNet.Mcp.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    /p:PublishSingleFile=true \
    -o ./artifacts/publish/linux-x64
fi

pwsh ./scripts/register-copilot.ps1 2>/dev/null || true

echo ""
echo "RagNet MCP setup complete."
echo "MCP endpoint: http://localhost:7331/ragnet-mcp"
echo "Health:       http://localhost:7331/health"
