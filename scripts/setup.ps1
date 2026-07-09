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
        [string]$OllamaBaseUrl
    )

    $envPath = Join-Path $RepoRoot ".env"
    $content = @(
        "# Generated by scripts/setup.ps1. Used by Docker Compose.",
        "RAGNET_MCP_PORT=$McpPort",
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

function Pull-OllamaModel {
    param(
        [string]$Model,
        [bool]$UseContainer
    )

    if ($SkipModelPull) {
        return
    }

    if ($UseContainer) {
        Invoke-ContainerExec ollama ollama pull $Model
        return
    }

    $ollama = Get-Command "ollama" -ErrorAction SilentlyContinue
    if ($ollama) {
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

            throw "OllamaMode is Docker, but localhost:11434 is already in use. Stop the local service or use -OllamaMode Local."
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
        if ($OllamaMode -eq "Local") {
            throw "Docker mode requires the containerized Ollama service from docker-compose.yml. Use Hybrid or Native mode with -OllamaMode Local."
        }

        Write-ComposeEnvFile -OllamaBaseUrl "http://ollama:11434"
        Test-ServicePorts @(
            @{ Name = "qdrant"; Ports = @(6333, 6334); AllowExternal = $false },
            @{ Name = "ollama"; Ports = @(11434); AllowExternal = $false },
            @{ Name = "ragnet-mcp"; Ports = @($McpPort); AllowExternal = $false }
        )
        Invoke-Compose up -d --build

        if (-not $SkipModelPull) {
            Pull-OllamaModels -UseContainer $true
        }
    }
    elseif ($Mode -eq "Hybrid") {
        Require-Command "dotnet"

        $useOllamaContainer = Use-OllamaContainer
        if ($useOllamaContainer) {
            Write-ComposeEnvFile -OllamaBaseUrl "http://ollama:11434"
            Test-ServicePorts @(
                @{ Name = "qdrant"; Ports = @(6333, 6334); AllowExternal = $false },
                @{ Name = "ollama"; Ports = @(11434); AllowExternal = $false },
                @{ Name = "ragnet-mcp"; Ports = @($McpPort); AllowExternal = $false }
            )
            Invoke-Compose up -d qdrant ollama
        }
        else {
            Write-ComposeEnvFile -OllamaBaseUrl "http://host.docker.internal:11434"
            Test-ServicePorts @(
                @{ Name = "qdrant"; Ports = @(6333, 6334); AllowExternal = $false },
                @{ Name = "ollama"; Ports = @(11434); AllowExternal = $true },
                @{ Name = "ragnet-mcp"; Ports = @($McpPort); AllowExternal = $false }
            )
            Invoke-Compose up -d qdrant
        }

        Pull-OllamaModels -UseContainer $useOllamaContainer
        Invoke-Compose up -d --build --no-deps ragnet-mcp

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
            Write-SetupWarning "Native mode does not start containerized Ollama. Use Hybrid mode with -OllamaMode Docker if you want setup to start Ollama in a container."
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

    if ($RegisterClients -ne "Skip") {
        if ($RegisterClients -eq "RepoOnly") {
            & (Join-Path $PSScriptRoot "register-copilot.ps1") -Url $mcpEndpoint -SkipCodex -SkipClaude
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
    elseif ($Mode -eq "Native") {
        Write-Host "Server:       .\bin\ragnet-mcp.exe"
        Write-Host "Indexer:      .\bin\ragnet-indexer.exe"
    }
}
finally {
    Pop-Location
}
