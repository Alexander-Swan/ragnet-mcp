#!/usr/bin/env bash
set -euo pipefail

MODE="${1:-Hybrid}"
EMBEDDING_MODEL="${EMBEDDING_MODEL:-mxbai-embed-large}"
ADDITIONAL_EMBEDDING_MODELS="${ADDITIONAL_EMBEDDING_MODELS:-nomic-embed-text}"
OLLAMA_MODE="${OLLAMA_MODE:-${2:-Auto}}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

export DOTNET_CLI_HOME="$REPO_ROOT/.dotnet-home"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1

cd "$REPO_ROOT"

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Required command '$1' was not found on PATH." >&2
    exit 1
  fi
}

port_in_use() {
  local port="$1"
  if command -v ss >/dev/null 2>&1; then
    ss -ltn | awk '{print $4}' | grep -Eq "[:.]${port}$"
    return
  fi

  if command -v lsof >/dev/null 2>&1; then
    lsof -iTCP:"$port" -sTCP:LISTEN >/dev/null 2>&1
    return
  fi

  return 1
}

use_ollama_container() {
  case "$OLLAMA_MODE" in
    Local)
      if ! port_in_use 11434; then
        echo "WARNING: OLLAMA_MODE is Local, but nothing is listening on localhost:11434 yet." >&2
        echo "WARNING: Start local Ollama before indexing, or rerun setup with OLLAMA_MODE=Docker." >&2
      fi
      return 1
      ;;
    Docker)
      if port_in_use 11434; then
        echo "OLLAMA_MODE is Docker, but localhost:11434 is already in use. Stop the local service or use OLLAMA_MODE=Local." >&2
        exit 1
      fi
      return 0
      ;;
    Auto)
      if port_in_use 11434; then
        echo "WARNING: Port 11434 is already in use. Hybrid setup will use the existing Ollama at http://localhost:11434 and start only Qdrant in Docker." >&2
        return 1
      fi
      return 0
      ;;
    *)
      echo "OLLAMA_MODE must be Auto, Docker, or Local." >&2
      exit 1
      ;;
  esac
}

pull_ollama_model() {
  local use_container="$1"
  local model="$2"
  if [[ "${SKIP_MODEL_PULL:-}" == "1" || "${SKIP_MODEL_PULL:-}" == "true" ]]; then
    return
  fi

  if [[ "$use_container" == "true" ]]; then
    docker exec ollama ollama pull "$model"
    return
  fi

  if command -v ollama >/dev/null 2>&1; then
    ollama pull "$model"
    return
  fi

  echo "WARNING: Local Ollama is selected, but the ollama CLI was not found. Skipping model pull for '$model'." >&2
  echo "WARNING: Run 'ollama pull $model' manually if the model is not already installed." >&2
}

pull_ollama_models() {
  local use_container="$1"
  local pulled=" "

  for model in "$EMBEDDING_MODEL" $ADDITIONAL_EMBEDDING_MODELS; do
    if [[ -z "${model// }" || "$pulled" == *" $model "* ]]; then
      continue
    fi

    pull_ollama_model "$use_container" "$model"
    pulled="$pulled$model "
  done
}

if [[ "$MODE" == "Docker" ]]; then
  require_command docker
  if [[ "$OLLAMA_MODE" == "Local" ]]; then
    echo "Docker mode requires the Docker Ollama service from docker-compose.yml. Use Hybrid or Native mode with OLLAMA_MODE=Local." >&2
    exit 1
  fi
  docker compose up -d --build
  pull_ollama_models true
elif [[ "$MODE" == "Hybrid" ]]; then
  require_command docker
  require_command dotnet
  if use_ollama_container; then
    docker compose up -d qdrant ollama
    pull_ollama_models true
  else
    docker compose up -d qdrant
    pull_ollama_models false
  fi
  dotnet restore ./RagNet.Mcp.sln
  dotnet publish ./src/RagNet.Mcp/RagNet.Mcp.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    /p:PublishSingleFile=true \
    -o ./artifacts/publish/linux-x64/ragnet-mcp
  dotnet publish ./src/RagNet.Indexer/RagNet.Indexer.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    /p:PublishSingleFile=true \
    -o ./artifacts/publish/linux-x64/ragnet-indexer
else
  require_command dotnet
  if [[ "$OLLAMA_MODE" == "Docker" ]]; then
    echo "WARNING: Native mode does not start Docker Ollama. Use Hybrid mode with OLLAMA_MODE=Docker if you want setup to start Ollama in Docker." >&2
  else
    pull_ollama_models false
  fi
  dotnet restore ./RagNet.Mcp.sln
  dotnet publish ./src/RagNet.Mcp/RagNet.Mcp.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    /p:PublishSingleFile=true \
    -o ./artifacts/publish/linux-x64/ragnet-mcp
  dotnet publish ./src/RagNet.Indexer/RagNet.Indexer.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    /p:PublishSingleFile=true \
    -o ./artifacts/publish/linux-x64/ragnet-indexer
fi

pwsh ./scripts/register-copilot.ps1 2>/dev/null || true

echo ""
echo "RagNet MCP setup complete."
echo "MCP endpoint: http://localhost:7331/ragnet-mcp"
echo "Health:       http://localhost:7331/health"
if [[ "$MODE" != "Docker" ]]; then
  echo "Indexer:      ./artifacts/publish/linux-x64/ragnet-indexer/ragnet-indexer"
fi
