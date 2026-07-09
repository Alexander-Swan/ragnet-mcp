param(
    [ValidateSet("Docker", "Hybrid", "Native")]
    [string]$Mode = "Hybrid",

    [ValidateSet("Auto", "Docker", "Nerdctl")]
    [string]$ContainerRuntime = "Auto",

    [string]$EmbeddingModel = "mxbai-embed-large",

    [string[]]$AdditionalEmbeddingModels = @("nomic-embed-text"),

    [ValidateSet("Auto", "Docker", "Local")]
    [string]$OllamaMode = "Docker",

    [ValidateRange(1, 65535)]
    [int]$McpPort = 7331,

    [ValidateSet("All", "RepoOnly", "Skip")]
    [string]$RegisterClients = "All",

    [switch]$SkipModelPull,

    [switch]$SkipRegister,

    [switch]$NonInteractive
)

$ErrorActionPreference = "Stop"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$BinPath = Join-Path $RepoRoot "bin"
$QdrantStorageVolumeName = "ragnet-mcp_qdrant_storage"
$env:DOTNET_CLI_HOME = Join-Path $RepoRoot ".dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$ExplicitSetupParameterCount = $PSBoundParameters.Count

$script:UseColor = -not [Console]::IsOutputRedirected

function Write-SetupInfo {
    param([string]$Message)
    if ($script:UseColor) { Write-Host $Message -ForegroundColor Cyan } else { Write-Host $Message }
}

function Write-SetupSuccess {
    param([string]$Message)
    if ($script:UseColor) { Write-Host $Message -ForegroundColor Green } else { Write-Host $Message }
}

function Write-SetupWarning {
    param([string]$Message)
    if ($script:UseColor) { Write-Host "WARNING: $Message" -ForegroundColor Yellow } else { Write-Warning $Message }
}

function Write-SetupError {
    param([string]$Message)
    if ($script:UseColor) { Write-Host "ERROR: $Message" -ForegroundColor Red } else { Write-Host "ERROR: $Message" }
}

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

function Test-InteractiveSetup {
    if ($NonInteractive) {
        return $false
    }

    if ($ExplicitSetupParameterCount -gt 0) {
        return $false
    }

    if ([Console]::IsInputRedirected -or [Console]::IsOutputRedirected) {
        return $false
    }

    if ($env:CI) {
        return $false
    }

    return $true
}

function Read-SetupChoice {
    param(
        [string]$Prompt,
        [string[]]$Choices,
        [string]$Default
    )

    $choiceList = $Choices -join "/"
    while ($true) {
        $value = Read-Host "$Prompt [$choiceList] default: $Default"
        if ([string]::IsNullOrWhiteSpace($value)) {
            return $Default
        }

        $match = $Choices | Where-Object { $_.Equals($value, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
        if ($match) {
            return $match
        }

        Write-SetupWarning "Choose one of: $choiceList"
    }
}

function Read-ModelList {
    param([string[]]$DefaultModels)

    $defaultText = $DefaultModels -join ", "
    $value = Read-Host "Additional embedding models, comma-separated or blank for default [$defaultText]"
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $DefaultModels
    }

    return @(
        $value -split "," |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
}

function Show-SetupSummary {
    $mcpEndpoint = "http://localhost:$McpPort/ragnet-mcp"
    $healthEndpoint = "http://localhost:$McpPort/health"
    Write-Host ""
    Write-SetupInfo "Setup summary"
    Write-Host "  Mode:                 $Mode"
    Write-Host "  Container runtime:    $ContainerRuntime"
    Write-Host "  Ollama mode:          $OllamaMode"
    Write-Host "  Embedding model:      $EmbeddingModel"
    Write-Host "  Additional models:    $($AdditionalEmbeddingModels -join ', ')"
    Write-Host "  Pull models:          $(-not $SkipModelPull)"
    Write-Host "  MCP registration:     $(if ($SkipRegister) { 'Skip' } else { $RegisterClients })"
    Write-Host "  MCP endpoint:         $mcpEndpoint"
    Write-Host "  Health endpoint:      $healthEndpoint"
    Write-Host "  Ports:                MCP $McpPort, Qdrant 6333/6334, Ollama 11434"
    Write-Host ""
}

function Invoke-InteractiveSetup {
    Write-SetupInfo "RagNet MCP interactive setup"
    Write-Host ""

    $script:Mode = Read-SetupChoice -Prompt "Setup mode" -Choices @("Hybrid", "Docker", "Native") -Default "Hybrid"
    if ($Mode -ne "Native") {
        $script:ContainerRuntime = Read-SetupChoice -Prompt "Container runtime" -Choices @("Docker", "Nerdctl", "Auto") -Default "Docker"
    }

    $script:OllamaMode = Read-SetupChoice -Prompt "Ollama mode/source" -Choices @("Docker", "Local", "Auto") -Default "Docker"
    $portValue = Read-Host "MCP localhost port default: $McpPort"
    if (-not [string]::IsNullOrWhiteSpace($portValue)) {
        $parsedPort = 0
        if (-not [int]::TryParse($portValue, [ref]$parsedPort) -or $parsedPort -lt 1 -or $parsedPort -gt 65535) {
            throw "MCP port must be a number from 1 to 65535."
        }

        $script:McpPort = $parsedPort
    }

    $script:AdditionalEmbeddingModels = Read-ModelList -DefaultModels $AdditionalEmbeddingModels
    $script:RegisterClients = Read-SetupChoice -Prompt "MCP client registration" -Choices @("All", "RepoOnly", "Skip") -Default "All"

    Show-SetupSummary
    $confirm = Read-SetupChoice -Prompt "Apply these changes?" -Choices @("Yes", "No") -Default "Yes"
    if ($confirm -ne "Yes") {
        Write-SetupWarning "Setup cancelled."
        exit 0
    }
}

function Resolve-ContainerRuntime {
    if ($Mode -eq "Native") {
        return $null
    }

    if ($ContainerRuntime -eq "Docker") {
        Require-Command "docker"
        return "docker"
    }

    if ($ContainerRuntime -eq "Nerdctl") {
        Require-Command "nerdctl"
        return "nerdctl"
    }

    if (Get-Command "docker" -ErrorAction SilentlyContinue) {
        return "docker"
    }

    if (Get-Command "nerdctl" -ErrorAction SilentlyContinue) {
        return "nerdctl"
    }

    throw "No supported container runtime was found on PATH. Install Docker Desktop or Rancher Desktop with nerdctl."
}

function Invoke-Compose {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$Arguments
    )

    Invoke-Native { & $script:ResolvedContainerRuntime compose @Arguments }
}

function Write-ComposeEnvFile {
    param(
        [string]$QdrantBaseUrl,
        [string]$OllamaBaseUrl
    )

    $envPath = Join-Path $RepoRoot ".env"
    $content = @(
        "# Generated by scripts/setup.ps1. Used by Docker Compose.",
        "RAGNET_MCP_PORT=$McpPort",
        "RAGNET_QDRANT_BASE_URL=$QdrantBaseUrl",
        "RAGNET_OLLAMA_BASE_URL=$OllamaBaseUrl",
        "RAGNET_EMBEDDING_MODEL=$EmbeddingModel"
    )

    Set-Content -Path $envPath -Value $content -Encoding utf8
}

function Test-ComposeServiceRunning {
    param([string]$Service)

    if (-not $script:ResolvedContainerRuntime) {
        return $false
    }

    $output = & $script:ResolvedContainerRuntime compose ps --status running $Service 2>$null
    if ($LASTEXITCODE -eq 0 -and ($output -match $Service)) {
        return $true
    }

    $output = & $script:ResolvedContainerRuntime compose ps $Service 2>$null
    return $LASTEXITCODE -eq 0 -and ($output -match $Service) -and ($output -match "running|Up")
}

function Test-QdrantStorageVolume {
    if (-not $script:ResolvedContainerRuntime) {
        return $false
    }

    try {
        & $script:ResolvedContainerRuntime volume inspect $QdrantStorageVolumeName *> $null
        return $LASTEXITCODE -eq 0
    }
    catch {
        Write-SetupWarning "Could not inspect Qdrant storage volume '$QdrantStorageVolumeName': $($_.Exception.Message)"
        return $false
    }
}

function Write-QdrantPersistenceInfo {
    param([bool]$UseQdrantContainer)

    if (-not $script:ResolvedContainerRuntime) {
        Write-SetupInfo "Qdrant persistence: Native mode uses the configured Qdrant service; setup does not manage its storage."
        return
    }

    if ($UseQdrantContainer) {
        $exists = Test-QdrantStorageVolume
        Write-SetupInfo "Qdrant persistence: compose stores data in Docker volume '$QdrantStorageVolumeName' (exists: $exists)."
        Write-Host "  Restart/rebuild keeps it. 'docker compose down -v' deletes it."
        return
    }

    Write-SetupInfo "Qdrant persistence: setup is using an existing host Qdrant service; check that service's storage/backup policy."
}

function Test-ServicePorts {
    param(
        [array]$Services
    )

    foreach ($service in $Services) {
        $name = $service.Name
        $ports = @($service.Ports)
        $allowExternal = [bool]$service.AllowExternal
        $isComposeRunning = Test-ComposeServiceRunning $name

        foreach ($port in $ports) {
            if (-not (Test-PortInUse $port)) {
                continue
            }

            if ($isComposeRunning) {
                Write-SetupSuccess "$name already appears to be running on port $port; setup will reuse/update it."
                continue
            }

            if ($allowExternal) {
                Write-SetupWarning "$name port $port is already open; setup will use the existing service."
                continue
            }

            throw "Port $port is already in use, but '$name' is not running in this compose project. Stop the conflicting service or choose a compatible setup mode."
        }
    }
}

function Invoke-ContainerExec {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$Arguments
    )

    Invoke-Native { & $script:ResolvedContainerRuntime exec @Arguments }
}

function Test-OllamaModelInstalled {
    param(
        [string]$Model,
        [bool]$UseContainer
    )

    $output = if ($UseContainer) {
        & $script:ResolvedContainerRuntime exec ollama ollama list 2>$null
    }
    else {
        $ollama = Get-Command "ollama" -ErrorAction SilentlyContinue
        if (-not $ollama) {
            return $false
        }

        & ollama list 2>$null
    }

    if ($LASTEXITCODE -ne 0) {
        return $false
    }

    $modelNames = @($output) |
        Select-Object -Skip 1 |
        ForEach-Object { ($_ -split "\s+")[0] } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    $match = $modelNames | Where-Object {
        $_.Equals($Model, [StringComparison]::OrdinalIgnoreCase) -or
            $_.Equals("${Model}:latest", [StringComparison]::OrdinalIgnoreCase)
    } | Select-Object -First 1

    return $null -ne $match
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
        if (Test-OllamaModelInstalled -Model $Model -UseContainer $true) {
            Write-SetupSuccess "Ollama model '$Model' is already available."
            return
        }

        Invoke-ContainerExec ollama ollama pull $Model
        return
    }

    $ollama = Get-Command "ollama" -ErrorAction SilentlyContinue
    if ($ollama) {
        if (Test-OllamaModelInstalled -Model $Model -UseContainer $false) {
            Write-SetupSuccess "Ollama model '$Model' is already available."
            return
        }

        Invoke-Native { ollama pull $Model }
        return
    }

    Write-SetupWarning "Local Ollama is selected, but the ollama CLI was not found. Skipping model pull for '$Model'."
    Write-SetupWarning "Run 'ollama pull $Model' manually if the model is not already installed."
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

function Publish-Indexer {
    Invoke-Native { dotnet restore .\src\RagNet.Indexer\RagNet.Indexer.csproj -r win-x64 }
    Invoke-Native { dotnet publish .\src\RagNet.Indexer\RagNet.Indexer.csproj `
        -c Release `
        -r win-x64 `
        --no-restore `
        --self-contained true `
        /p:PublishSingleFile=true `
        -o $BinPath }
}

function Publish-NativeServer {
    Invoke-Native { dotnet restore .\src\RagNet.Mcp\RagNet.Mcp.csproj -r win-x64 }
    Invoke-Native { dotnet publish .\src\RagNet.Mcp\RagNet.Mcp.csproj `
        -c Release `
        -r win-x64 `
        --no-restore `
        --self-contained true `
        /p:PublishSingleFile=true `
        -o $BinPath }
}

function Use-QdrantContainer {
    if (Test-ComposeServiceRunning "qdrant") {
        Write-SetupSuccess "qdrant already appears to be running on port 6333; setup will reuse/update it."
        return $true
    }

    if (Test-PortInUse 6333) {
        Write-SetupWarning "Qdrant port 6333 is already open; setup will use the existing host Qdrant service."
        return $false
    }

    if (Test-PortInUse 6334) {
        throw "Qdrant gRPC port 6334 is already in use, but port 6333 is not available as a host Qdrant HTTP service. Stop the conflicting service or run Qdrant locally on port 6333."
    }

    return $true
}

function Use-OllamaContainer {
    if ($OllamaMode -eq "Local") {
        if (-not (Test-PortInUse 11434)) {
            Write-SetupWarning "OllamaMode is Local, but nothing is listening on localhost:11434 yet."
            Write-SetupWarning "Start local Ollama before indexing, or rerun setup with -OllamaMode Docker."
        }

        return $false
    }

    if ($OllamaMode -eq "Docker") {
        if (Test-PortInUse 11434) {
            if (Test-ComposeServiceRunning "ollama") {
                Write-SetupSuccess "ollama already appears to be running on port 11434; setup will reuse/update it."
                return $true
            }

            Write-SetupWarning "OllamaMode is Docker, but localhost:11434 is already in use. Setup will use the existing host Ollama service."
            return $false
        }

        return $true
    }

    if (Test-PortInUse 11434) {
        if (Test-ComposeServiceRunning "ollama") {
            Write-SetupSuccess "ollama already appears to be running on port 11434; setup will reuse/update it."
            return $true
        }

        Write-SetupWarning "Port 11434 is already in use. Hybrid setup will use the existing Ollama at http://localhost:11434 and start only Qdrant/RagNet MCP in containers."
        return $false
    }

    return $true
}

if ($SkipRegister) {
    $RegisterClients = "Skip"
}

if (Test-InteractiveSetup) {
    Invoke-InteractiveSetup
}
else {
    Show-SetupSummary
}

$script:ResolvedContainerRuntime = Resolve-ContainerRuntime
$mcpEndpoint = "http://localhost:$McpPort/ragnet-mcp"
$healthEndpoint = "http://localhost:$McpPort/health"

Push-Location $RepoRoot
try {
    if ($Mode -eq "Docker") {
        Require-Command "dotnet"

        $useQdrantContainer = Use-QdrantContainer
        $useOllamaContainer = Use-OllamaContainer
        $qdrantBaseUrl = if ($useQdrantContainer) { "http://qdrant:6333" } else { "http://host.docker.internal:6333" }
        $ollamaBaseUrl = if ($useOllamaContainer) { "http://ollama:11434" } else { "http://host.docker.internal:11434" }
        Write-ComposeEnvFile -QdrantBaseUrl $qdrantBaseUrl -OllamaBaseUrl $ollamaBaseUrl

        $services = @()
        $portChecks = @(
            @{ Name = "qdrant"; Ports = @(6333, 6334); AllowExternal = -not $useQdrantContainer },
            @{ Name = "ollama"; Ports = @(11434); AllowExternal = -not $useOllamaContainer },
            @{ Name = "ragnet-mcp"; Ports = @($McpPort); AllowExternal = $false }
        )
        if ($useQdrantContainer) { $services += "qdrant" }
        if ($useOllamaContainer) { $services += "ollama" }
        $services += "ragnet-mcp"

        Test-ServicePorts $portChecks
        Invoke-Compose up -d --build @services
        Write-QdrantPersistenceInfo -UseQdrantContainer $useQdrantContainer

        if (-not $SkipModelPull) {
            Pull-OllamaModels -UseContainer $useOllamaContainer
        }

        Publish-Indexer
    }
    elseif ($Mode -eq "Hybrid") {
        Require-Command "dotnet"

        $useQdrantContainer = Use-QdrantContainer
        $useOllamaContainer = Use-OllamaContainer
        $qdrantBaseUrl = if ($useQdrantContainer) { "http://qdrant:6333" } else { "http://host.docker.internal:6333" }
        $ollamaBaseUrl = if ($useOllamaContainer) { "http://ollama:11434" } else { "http://host.docker.internal:11434" }
        Write-ComposeEnvFile -QdrantBaseUrl $qdrantBaseUrl -OllamaBaseUrl $ollamaBaseUrl

        $services = @()
        $portChecks = @(
            @{ Name = "qdrant"; Ports = @(6333, 6334); AllowExternal = -not $useQdrantContainer },
            @{ Name = "ollama"; Ports = @(11434); AllowExternal = -not $useOllamaContainer },
            @{ Name = "ragnet-mcp"; Ports = @($McpPort); AllowExternal = $false }
        )
        if ($useQdrantContainer) { $services += "qdrant" }
        if ($useOllamaContainer) { $services += "ollama" }

        Test-ServicePorts $portChecks
        if ($services.Count -gt 0) {
            Invoke-Compose up -d @services
        }
        Write-QdrantPersistenceInfo -UseQdrantContainer $useQdrantContainer

        Pull-OllamaModels -UseContainer $useOllamaContainer
        Invoke-Compose up -d --build --no-deps ragnet-mcp

        Publish-Indexer
    }
    else {
        Require-Command "dotnet"

        if ($OllamaMode -eq "Docker") {
            Write-SetupWarning "Native mode does not start containerized Ollama. Use Hybrid mode with -OllamaMode Docker if you want setup to start Ollama in a container."
        }
        elseif (-not $SkipModelPull) {
            Pull-OllamaModels -UseContainer $false
        }

        Publish-Indexer
        Publish-NativeServer
        Write-QdrantPersistenceInfo -UseQdrantContainer $false
    }

    if ($RegisterClients -ne "Skip") {
        if ($RegisterClients -eq "RepoOnly") {
            & (Join-Path $PSScriptRoot "register-copilot.ps1") -Url $mcpEndpoint -SkipCopilotCli -SkipCodex -SkipClaude
        }
        else {
            & (Join-Path $PSScriptRoot "register-copilot.ps1") -Url $mcpEndpoint
        }
    }

    Write-Host ""
    Write-SetupSuccess "RagNet MCP setup complete."
    Write-Host "MCP endpoint: $mcpEndpoint"
    Write-Host "Health:       $healthEndpoint"
    if ($Mode -eq "Hybrid") {
        Write-Host "Server:       $ResolvedContainerRuntime container ragnet-mcp"
        Write-Host "Indexer:      .\bin\ragnet-indexer.exe"
    }
    elseif ($Mode -eq "Docker") {
        Write-Host "Server:       $ResolvedContainerRuntime container ragnet-mcp"
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
