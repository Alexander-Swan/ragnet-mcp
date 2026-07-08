param(
    [ValidateSet("Docker", "Hybrid", "Native")]
    [string]$Mode = "Hybrid",

    [string]$EmbeddingModel = "mxbai-embed-large",

    [string[]]$AdditionalEmbeddingModels = @("nomic-embed-text"),

    [ValidateSet("Auto", "Docker", "Local")]
    [string]$OllamaMode = "Auto",

    [switch]$SkipModelPull,

    [switch]$SkipRegister
)

$ErrorActionPreference = "Stop"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$BinPath = Join-Path $RepoRoot "bin"
$env:DOTNET_CLI_HOME = Join-Path $RepoRoot ".dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

function Require-Command {
    param([string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found on PATH."
    }
}

function Invoke-Native {
    param(
        [scriptblock]$Command
    )

    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE."
    }
}

function Test-PortInUse {
    param([int]$Port)

    $connections = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    return $null -ne $connections
}

function Pull-OllamaModel {
    param(
        [string]$Model,
        [bool]$UseContainer
    )

    if ($SkipModelPull) {
        return
    }

    if ($UseContainer) {
        Invoke-Native { docker exec ollama ollama pull $Model }
        return
    }

    $ollama = Get-Command "ollama" -ErrorAction SilentlyContinue
    if ($ollama) {
        Invoke-Native { ollama pull $Model }
        return
    }

    Write-Warning "Local Ollama is selected, but the ollama CLI was not found. Skipping model pull for '$Model'."
    Write-Warning "Run 'ollama pull $Model' manually if the model is not already installed."
}

function Pull-OllamaModels {
    param(
        [bool]$UseContainer
    )

    $models = @($EmbeddingModel) + @($AdditionalEmbeddingModels)
    $models |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Unique |
        ForEach-Object { Pull-OllamaModel -Model $_ -UseContainer $UseContainer }
}

function Use-OllamaContainer {
    if ($OllamaMode -eq "Local") {
        if (-not (Test-PortInUse 11434)) {
            Write-Warning "OllamaMode is Local, but nothing is listening on localhost:11434 yet."
            Write-Warning "Start local Ollama before indexing, or rerun setup with -OllamaMode Docker."
        }

        return $false
    }

    if ($OllamaMode -eq "Docker") {
        if (Test-PortInUse 11434) {
            throw "OllamaMode is Docker, but localhost:11434 is already in use. Stop the local service or use -OllamaMode Local."
        }

        return $true
    }

    if (Test-PortInUse 11434) {
        Write-Warning "Port 11434 is already in use. Hybrid setup will use the existing Ollama at http://localhost:11434 and start only Qdrant in Docker."
        return $false
    }

    return $true
}

Push-Location $RepoRoot
try {
    if ($Mode -eq "Docker") {
        Require-Command "docker"

        if ($OllamaMode -eq "Local") {
            throw "Docker mode requires the Docker Ollama service from docker-compose.yml. Use Hybrid or Native mode with -OllamaMode Local."
        }

        $env:RAGNET_EMBEDDING_MODEL = $EmbeddingModel
        $env:RAGNET_OLLAMA_BASE_URL = "http://ollama:11434"
        Invoke-Native { docker compose up -d --build }

        if (-not $SkipModelPull) {
            Pull-OllamaModels -UseContainer $true
        }
    }
    elseif ($Mode -eq "Hybrid") {
        Require-Command "docker"
        Require-Command "dotnet"

        $env:RAGNET_EMBEDDING_MODEL = $EmbeddingModel
        $useOllamaContainer = Use-OllamaContainer
        if ($useOllamaContainer) {
            $env:RAGNET_OLLAMA_BASE_URL = "http://ollama:11434"
            Invoke-Native { docker compose up -d qdrant ollama }
        }
        else {
            $env:RAGNET_OLLAMA_BASE_URL = "http://host.docker.internal:11434"
            Invoke-Native { docker compose up -d qdrant }
        }

        Pull-OllamaModels -UseContainer $useOllamaContainer
        Invoke-Native { docker compose up -d --build --no-deps ragnet-mcp }

        Invoke-Native { dotnet restore .\RagNet.Mcp.sln }
        Invoke-Native { dotnet publish .\src\RagNet.Indexer\RagNet.Indexer.csproj `
            -c Release `
            -r win-x64 `
            --self-contained true `
            /p:PublishSingleFile=true `
            -o $BinPath }
    }
    else {
        Require-Command "dotnet"

        if ($OllamaMode -eq "Docker") {
            Write-Warning "Native mode does not start Docker Ollama. Use Hybrid mode with -OllamaMode Docker if you want setup to start Ollama in Docker."
        }
        elseif (-not $SkipModelPull) {
            Pull-OllamaModels -UseContainer $false
        }

        Invoke-Native { dotnet restore .\RagNet.Mcp.sln }
        Invoke-Native { dotnet publish .\src\RagNet.Indexer\RagNet.Indexer.csproj `
            -c Release `
            -r win-x64 `
            --self-contained true `
            /p:PublishSingleFile=true `
            -o $BinPath }
        Invoke-Native { dotnet publish .\src\RagNet.Mcp\RagNet.Mcp.csproj `
            -c Release `
            -r win-x64 `
            --self-contained true `
            /p:PublishSingleFile=true `
            -o $BinPath }
    }

    if (-not $SkipRegister) {
        & (Join-Path $PSScriptRoot "register-copilot.ps1")
    }

    Write-Host ""
    Write-Host "RagNet MCP setup complete."
    Write-Host "MCP endpoint: http://localhost:7331/ragnet-mcp"
    Write-Host "Health:       http://localhost:7331/health"
    if ($Mode -eq "Hybrid") {
        Write-Host "Server:       docker container ragnet-mcp"
        Write-Host "Indexer:      .\bin\ragnet-indexer.exe"
    }
    elseif ($Mode -eq "Native") {
        Write-Host "Server:       .\bin\ragnet-mcp.exe"
        Write-Host "Indexer:      .\bin\ragnet-indexer.exe"
    }
}
finally {
    Pop-Location
}
