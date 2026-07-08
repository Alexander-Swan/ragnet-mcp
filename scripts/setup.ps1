param(
    [ValidateSet("Docker", "Native")]
    [string]$Mode = "Docker",

    [string]$EmbeddingModel = "mxbai-embed-large",

    [switch]$SkipModelPull,

    [switch]$SkipRegister
)

$ErrorActionPreference = "Stop"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$env:DOTNET_CLI_HOME = Join-Path $RepoRoot ".dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

function Require-Command {
    param([string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found on PATH."
    }
}

Push-Location $RepoRoot
try {
    if ($Mode -eq "Docker") {
        Require-Command "docker"

        docker compose up -d --build

        if (-not $SkipModelPull) {
            docker exec ollama ollama pull $EmbeddingModel
        }
    }
    else {
        Require-Command "dotnet"

        dotnet restore .\RagNet.Mcp.sln
        dotnet publish .\src\RagNet.Mcp\RagNet.Mcp.csproj `
            -c Release `
            -r win-x64 `
            --self-contained true `
            /p:PublishSingleFile=true `
            -o .\artifacts\publish\win-x64
    }

    if (-not $SkipRegister) {
        & (Join-Path $PSScriptRoot "register-copilot.ps1")
    }

    Write-Host ""
    Write-Host "RagNet MCP setup complete."
    Write-Host "MCP endpoint: http://localhost:7331/ragnet-mcp"
    Write-Host "Health:       http://localhost:7331/health"
}
finally {
    Pop-Location
}
