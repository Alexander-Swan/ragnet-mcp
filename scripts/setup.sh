#!/usr/bin/env bash
set -euo pipefail

EXPLICIT_SETUP_INPUT=0
ARGS=("$@")
if [[ "${#ARGS[@]}" -gt 0 ||
  -n "${MODE+x}" ||
  -n "${EMBEDDING_MODEL+x}" ||
  -n "${ADDITIONAL_EMBEDDING_MODELS+x}" ||
  -n "${OLLAMA_MODE+x}" ||
  -n "${CONTAINER_RUNTIME+x}" ||
  -n "${MCP_PORT+x}" ||
  -n "${REGISTER_CLIENTS+x}" ||
  -n "${SKIP_MODEL_PULL+x}" ||
  -n "${SKIP_REGISTER+x}" ||
  -n "${NON_INTERACTIVE+x}" ]]; then
  EXPLICIT_SETUP_INPUT=1
fi

MODE="${MODE:-Hybrid}"
EMBEDDING_MODEL="${EMBEDDING_MODEL:-mxbai-embed-large}"
ADDITIONAL_EMBEDDING_MODELS="${ADDITIONAL_EMBEDDING_MODELS:-nomic-embed-text}"
OLLAMA_MODE="${OLLAMA_MODE:-Docker}"
CONTAINER_RUNTIME="${CONTAINER_RUNTIME:-Auto}"
MCP_PORT="${MCP_PORT:-7331}"
REGISTER_CLIENTS="${REGISTER_CLIENTS:-All}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BIN_DIR="$REPO_ROOT/bin"

export DOTNET_CLI_HOME="$REPO_ROOT/.dotnet-home"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1

cd "$REPO_ROOT"

usage() {
  cat <<'USAGE'
Usage:
  ./scripts/setup.sh [Hybrid|Docker|Native] [OllamaMode]
  ./scripts/setup.sh --mode Hybrid --container-runtime Docker --ollama-mode Docker --mcp-port 7331

Options:
  --mode <Hybrid|Docker|Native>
  --container-runtime <Auto|Docker|Nerdctl>
  --embedding-model <model>
  --additional-embedding-models "<model model>"
  --ollama-mode <Auto|Docker|Local>
  --mcp-port <1-65535>
  --register-clients <All|RepoOnly|Skip>
  --skip-model-pull
  --skip-register
  --non-interactive
  --help

With no args/env in an interactive terminal, setup prompts for choices.
USAGE
}

normalize_choice() {
  local value="$1"
  local choices="$2"
  local choice
  local value_lc
  local choice_lc

  value_lc="$(printf '%s' "$value" | tr '[:upper:]' '[:lower:]')"
  IFS='/' read -r -a parts <<< "$choices"
  for choice in "${parts[@]}"; do
    choice_lc="$(printf '%s' "$choice" | tr '[:upper:]' '[:lower:]')"
    if [[ "$choice_lc" == "$value_lc" ]]; then
      printf '%s\n' "$choice"
      return
    fi
  done

  fail "Expected one of $choices, got '$value'."
}

read_option_value() {
  local option="$1"
  local value="${2:-}"
  if [[ -z "${value// }" || "$value" == --* ]]; then
    fail "$option requires a value."
  fi

  printf '%s\n' "$value"
}

validate_port() {
  local port="$1"
  if ! [[ "$port" =~ ^[0-9]+$ ]] || (( port < 1 || port > 65535 )); then
    fail "MCP port must be a number from 1 to 65535."
  fi
}

parse_args() {
  local positional=()
  local arg
  local value

  while [[ "$#" -gt 0 ]]; do
    arg="$1"
    case "$arg" in
      --mode)
        value="$(read_option_value "$arg" "${2:-}")"
        MODE="$(normalize_choice "$value" "Hybrid/Docker/Native")"
        shift 2
        ;;
      --container-runtime)
        value="$(read_option_value "$arg" "${2:-}")"
        CONTAINER_RUNTIME="$(normalize_choice "$value" "Auto/Docker/Nerdctl")"
        shift 2
        ;;
      --embedding-model)
        EMBEDDING_MODEL="$(read_option_value "$arg" "${2:-}")"
        shift 2
        ;;
      --additional-embedding-models)
        ADDITIONAL_EMBEDDING_MODELS="$(read_option_value "$arg" "${2:-}")"
        shift 2
        ;;
      --ollama-mode)
        value="$(read_option_value "$arg" "${2:-}")"
        OLLAMA_MODE="$(normalize_choice "$value" "Auto/Docker/Local")"
        shift 2
        ;;
      --mcp-port)
        MCP_PORT="$(read_option_value "$arg" "${2:-}")"
        validate_port "$MCP_PORT"
        shift 2
        ;;
      --register-clients)
        value="$(read_option_value "$arg" "${2:-}")"
        REGISTER_CLIENTS="$(normalize_choice "$value" "All/RepoOnly/Skip")"
        shift 2
        ;;
      --skip-model-pull)
        SKIP_MODEL_PULL=1
        shift
        ;;
      --skip-register)
        SKIP_REGISTER=1
        shift
        ;;
      --non-interactive)
        NON_INTERACTIVE=1
        shift
        ;;
      --help|-h)
        usage
        exit 0
        ;;
      --*)
        fail "Unknown option: $arg"
        ;;
      *)
        positional+=("$arg")
        shift
        ;;
    esac
  done

  if [[ "${#positional[@]}" -gt 0 ]]; then
    MODE="$(normalize_choice "${positional[0]}" "Hybrid/Docker/Native")"
  fi

  if [[ "${#positional[@]}" -gt 1 ]]; then
    OLLAMA_MODE="$(normalize_choice "${positional[1]}" "Auto/Docker/Local")"
  fi

  if [[ "${#positional[@]}" -gt 2 ]]; then
    fail "Unexpected positional argument: ${positional[2]}"
  fi

  MODE="$(normalize_choice "$MODE" "Hybrid/Docker/Native")"
  CONTAINER_RUNTIME="$(normalize_choice "$CONTAINER_RUNTIME" "Auto/Docker/Nerdctl")"
  OLLAMA_MODE="$(normalize_choice "$OLLAMA_MODE" "Auto/Docker/Local")"
  REGISTER_CLIENTS="$(normalize_choice "$REGISTER_CLIENTS" "All/RepoOnly/Skip")"
  validate_port "$MCP_PORT"
}

if [[ -t 1 ]]; then
  COLOR_CYAN=$'\033[36m'
  COLOR_GREEN=$'\033[32m'
  COLOR_YELLOW=$'\033[33m'
  COLOR_RED=$'\033[31m'
  COLOR_RESET=$'\033[0m'
else
  COLOR_CYAN=""
  COLOR_GREEN=""
  COLOR_YELLOW=""
  COLOR_RED=""
  COLOR_RESET=""
fi

info() {
  printf '%s%s%s\n' "$COLOR_CYAN" "$1" "$COLOR_RESET"
}

success() {
  printf '%s%s%s\n' "$COLOR_GREEN" "$1" "$COLOR_RESET"
}

warn() {
  printf '%sWARNING: %s%s\n' "$COLOR_YELLOW" "$1" "$COLOR_RESET" >&2
}

fail() {
  printf '%sERROR: %s%s\n' "$COLOR_RED" "$1" "$COLOR_RESET" >&2
  exit 1
}

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    fail "Required command '$1' was not found on PATH."
  fi
}

choose() {
  local prompt="$1"
  local choices="$2"
  local default="$3"
  local value
  local value_lc
  local choice_lc

  while true; do
    read -r -p "$prompt [$choices] default: $default " value
    if [[ -z "${value// }" ]]; then
      printf '%s\n' "$default"
      return
    fi

    value_lc="$(printf '%s' "$value" | tr '[:upper:]' '[:lower:]')"
    IFS='/' read -r -a parts <<< "$choices"
    for choice in "${parts[@]}"; do
      choice_lc="$(printf '%s' "$choice" | tr '[:upper:]' '[:lower:]')"
      if [[ "$choice_lc" == "$value_lc" ]]; then
        printf '%s\n' "$choice"
        return
      fi
    done

    warn "Choose one of: $choices"
  done
}

show_summary() {
  local mcp_endpoint="http://localhost:${MCP_PORT}/ragnet-mcp"
  local health_endpoint="http://localhost:${MCP_PORT}/health"
  echo ""
  info "Setup summary"
  echo "  Mode:                 $MODE"
  echo "  Container runtime:    $CONTAINER_RUNTIME"
  echo "  Ollama mode:          $OLLAMA_MODE"
  echo "  Embedding model:      $EMBEDDING_MODEL"
  echo "  Additional models:    $ADDITIONAL_EMBEDDING_MODELS"
  echo "  Pull models:          $([[ "${SKIP_MODEL_PULL:-}" == "1" || "${SKIP_MODEL_PULL:-}" == "true" ]] && echo false || echo true)"
  echo "  MCP registration:     $REGISTER_CLIENTS"
  echo "  MCP endpoint:         $mcp_endpoint"
  echo "  Health endpoint:      $health_endpoint"
  echo "  Ports:                MCP $MCP_PORT, Qdrant 6333/6334, Ollama 11434"
  echo ""
}

interactive_setup() {
  info "RagNet MCP interactive setup"
  echo ""

  MODE="$(choose "Setup mode" "Hybrid/Docker/Native" "Hybrid")"
  if [[ "$MODE" != "Native" ]]; then
    CONTAINER_RUNTIME="$(choose "Container runtime" "Docker/Nerdctl/Auto" "Docker")"
  fi
  OLLAMA_MODE="$(choose "Ollama mode/source" "Docker/Local/Auto" "Docker")"

  local port
  read -r -p "MCP localhost port default: $MCP_PORT " port
  if [[ -n "${port// }" ]]; then
    if ! [[ "$port" =~ ^[0-9]+$ ]] || (( port < 1 || port > 65535 )); then
      fail "MCP port must be a number from 1 to 65535."
    fi

    MCP_PORT="$port"
  fi

  local models
  read -r -p "Additional embedding models, space-separated or blank for default [$ADDITIONAL_EMBEDDING_MODELS] " models
  if [[ -n "${models// }" ]]; then
    ADDITIONAL_EMBEDDING_MODELS="$models"
  fi

  REGISTER_CLIENTS="$(choose "MCP client registration" "All/RepoOnly/Skip" "All")"
  show_summary

  local confirm
  confirm="$(choose "Apply these changes?" "Yes/No" "Yes")"
  if [[ "$confirm" != "Yes" ]]; then
    warn "Setup cancelled."
    exit 0
  fi
}

resolve_container_runtime() {
  if [[ "$MODE" == "Native" ]]; then
    return
  fi

  case "$CONTAINER_RUNTIME" in
    Docker)
      require_command docker
      RESOLVED_CONTAINER_RUNTIME=docker
      ;;
    Nerdctl)
      require_command nerdctl
      RESOLVED_CONTAINER_RUNTIME=nerdctl
      ;;
    Auto)
      if command -v docker >/dev/null 2>&1; then
        RESOLVED_CONTAINER_RUNTIME=docker
      elif command -v nerdctl >/dev/null 2>&1; then
        RESOLVED_CONTAINER_RUNTIME=nerdctl
      else
        fail "No supported container runtime was found on PATH. Install Docker Desktop or Rancher Desktop with nerdctl."
      fi
      ;;
    *)
      fail "CONTAINER_RUNTIME must be Auto, Docker, or Nerdctl."
      ;;
  esac
}

compose() {
  "$RESOLVED_CONTAINER_RUNTIME" compose "$@"
}

write_compose_env_file() {
  local ollama_base_url="$1"
  cat > "$REPO_ROOT/.env" <<EOF
# Generated by scripts/setup.sh. Used by Docker Compose.
RAGNET_MCP_PORT=$MCP_PORT
RAGNET_OLLAMA_BASE_URL=$ollama_base_url
RAGNET_EMBEDDING_MODEL=$EMBEDDING_MODEL
EOF
}

container_exec() {
  "$RESOLVED_CONTAINER_RUNTIME" exec "$@"
}

compose_service_running() {
  local service="$1"
  local output

  if output="$("$RESOLVED_CONTAINER_RUNTIME" compose ps --status running "$service" 2>/dev/null)" &&
    [[ "$output" == *"$service"* ]]; then
    return 0
  fi

  if output="$("$RESOLVED_CONTAINER_RUNTIME" compose ps "$service" 2>/dev/null)" &&
    [[ "$output" == *"$service"* ]] &&
    [[ "$output" =~ running|Up ]]; then
    return 0
  fi

  return 1
}

check_service_ports() {
  local spec
  local service
  local allow_external
  local ports
  local port
  local is_running

  for spec in "$@"; do
    IFS='|' read -r service allow_external ports <<< "$spec"
    is_running=0
    if compose_service_running "$service"; then
      is_running=1
    fi

    IFS=',' read -r -a port_list <<< "$ports"
    for port in "${port_list[@]}"; do
      if ! port_in_use "$port"; then
        continue
      fi

      if [[ "$is_running" == "1" ]]; then
        success "$service already appears to be running on port $port; setup will reuse/update it."
        continue
      fi

      if [[ "$allow_external" == "true" ]]; then
        warn "$service port $port is already open; setup will use the existing service."
        continue
      fi

      fail "Port $port is already in use, but '$service' is not running in this compose project. Stop the conflicting service or choose a compatible setup mode."
    done
  done
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
        warn "OLLAMA_MODE is Local, but nothing is listening on localhost:11434 yet."
        warn "Start local Ollama before indexing, or rerun setup with OLLAMA_MODE=Docker."
      fi
      return 1
      ;;
    Docker)
      if port_in_use 11434; then
        if compose_service_running ollama; then
          success "ollama already appears to be running on port 11434; setup will reuse/update it."
          return 0
        fi

        fail "OLLAMA_MODE is Docker, but localhost:11434 is already in use. Stop the local service or use OLLAMA_MODE=Local."
      fi
      return 0
      ;;
    Auto)
      if port_in_use 11434; then
        if compose_service_running ollama; then
          success "ollama already appears to be running on port 11434; setup will reuse/update it."
          return 0
        fi

        warn "Port 11434 is already in use. Hybrid setup will use the existing Ollama at http://localhost:11434 and start only Qdrant/RagNet MCP in containers."
        return 1
      fi
      return 0
      ;;
    *)
      fail "OLLAMA_MODE must be Auto, Docker, or Local."
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
    container_exec ollama ollama pull "$model"
    return
  fi

  if command -v ollama >/dev/null 2>&1; then
    ollama pull "$model"
    return
  fi

  warn "Local Ollama is selected, but the ollama CLI was not found. Skipping model pull for '$model'."
  warn "Run 'ollama pull $model' manually if the model is not already installed."
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

publish_indexer() {
  dotnet restore ./src/RagNet.Indexer/RagNet.Indexer.csproj -r linux-x64
  dotnet publish ./src/RagNet.Indexer/RagNet.Indexer.csproj \
    -c Release \
    -r linux-x64 \
    --no-restore \
    --self-contained true \
    /p:PublishSingleFile=true \
    -o "$BIN_DIR"
}

publish_native_server() {
  dotnet restore ./src/RagNet.Mcp/RagNet.Mcp.csproj -r linux-x64
  dotnet publish ./src/RagNet.Mcp/RagNet.Mcp.csproj \
    -c Release \
    -r linux-x64 \
    --no-restore \
    --self-contained true \
    /p:PublishSingleFile=true \
    -o "$BIN_DIR"
}

parse_args "${ARGS[@]}"

if [[ "${SKIP_REGISTER:-}" == "1" || "${SKIP_REGISTER:-}" == "true" ]]; then
  REGISTER_CLIENTS=Skip
fi

if [[ "$EXPLICIT_SETUP_INPUT" == "0" && -t 0 && -t 1 && -z "${CI:-}" ]]; then
  interactive_setup
else
  show_summary
fi

resolve_container_runtime

if [[ "$MODE" == "Docker" ]]; then
  require_command dotnet
  if [[ "$OLLAMA_MODE" == "Local" ]]; then
    fail "Docker mode requires the containerized Ollama service from docker-compose.yml. Use Hybrid or Native mode with OLLAMA_MODE=Local."
  fi
  write_compose_env_file "http://ollama:11434"
  check_service_ports \
    "qdrant|false|6333,6334" \
    "ollama|false|11434" \
    "ragnet-mcp|false|$MCP_PORT"
  compose up -d --build
  pull_ollama_models true
  publish_indexer
elif [[ "$MODE" == "Hybrid" ]]; then
  require_command dotnet
  if use_ollama_container; then
    write_compose_env_file "http://ollama:11434"
    check_service_ports \
      "qdrant|false|6333,6334" \
      "ollama|false|11434" \
      "ragnet-mcp|false|$MCP_PORT"
    compose up -d qdrant ollama
    pull_ollama_models true
  else
    write_compose_env_file "http://host.docker.internal:11434"
    check_service_ports \
      "qdrant|false|6333,6334" \
      "ollama|true|11434" \
      "ragnet-mcp|false|$MCP_PORT"
    compose up -d qdrant
    pull_ollama_models false
  fi
  compose up -d --build --no-deps ragnet-mcp
  publish_indexer
else
  require_command dotnet
  if [[ "$OLLAMA_MODE" == "Docker" ]]; then
    warn "Native mode does not start containerized Ollama. Use Hybrid mode with OLLAMA_MODE=Docker if you want setup to start Ollama in a container."
  else
    pull_ollama_models false
  fi
  publish_indexer
  publish_native_server
fi

if [[ "$REGISTER_CLIENTS" == "RepoOnly" ]]; then
  pwsh ./scripts/register-copilot.ps1 -Url "http://localhost:${MCP_PORT}/ragnet-mcp" -SkipCodex -SkipClaude 2>/dev/null || true
elif [[ "$REGISTER_CLIENTS" != "Skip" ]]; then
  pwsh ./scripts/register-copilot.ps1 -Url "http://localhost:${MCP_PORT}/ragnet-mcp" 2>/dev/null || true
fi

echo ""
success "RagNet MCP setup complete."
echo "MCP endpoint: http://localhost:${MCP_PORT}/ragnet-mcp"
echo "Health:       http://localhost:${MCP_PORT}/health"
if [[ "$MODE" == "Hybrid" ]]; then
  echo "Server:       $RESOLVED_CONTAINER_RUNTIME container ragnet-mcp"
  echo "Indexer:      ./bin/ragnet-indexer"
elif [[ "$MODE" == "Docker" ]]; then
  echo "Server:       $RESOLVED_CONTAINER_RUNTIME container ragnet-mcp"
  echo "Indexer:      ./bin/ragnet-indexer"
elif [[ "$MODE" == "Native" ]]; then
  echo "Server:       ./bin/ragnet-mcp"
  echo "Indexer:      ./bin/ragnet-indexer"
fi
