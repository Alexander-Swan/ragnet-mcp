param(
    [string]$Url = "http://localhost:7331/ragnet-mcp",
    [string]$Name = "ragnet-mcp",
    [switch]$SkipCopilotCli,
    [switch]$SkipCodex,
    [switch]$SkipClaude
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$VsCodeDir = Join-Path $RepoRoot ".vscode"

function Write-Utf8Json {
    param(
        [Parameter(Mandatory = $true)] [object]$Value,
        [Parameter(Mandatory = $true)] [string]$Path
    )

    $json = $Value | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

if (-not (Test-Path $VsCodeDir)) {
    New-Item -ItemType Directory -Path $VsCodeDir | Out-Null
}

$VisualStudioConfig = [ordered]@{
    servers = @(
        [ordered]@{
            name = $Name
            transport = "http"
            url = $Url
        }
    )
}

$VsCodeConfig = [ordered]@{
    servers = [ordered]@{
        $Name = [ordered]@{
            type = "http"
            url = $Url
        }
    }
}

Write-Utf8Json -Value $VisualStudioConfig -Path (Join-Path $RepoRoot ".mcp.json")
Write-Utf8Json -Value $VsCodeConfig -Path (Join-Path $VsCodeDir "mcp.json")

Write-Host "Registered MCP configs:"
Write-Host "  Visual Studio / GitHub Copilot app: .mcp.json"
Write-Host "  VS Code / GitHub Copilot app:       .vscode/mcp.json"

if (-not $SkipCopilotCli) {
    & (Join-Path $PSScriptRoot "register-copilot-cli.ps1") -Url $Url -Name $Name -Optional
}

if (-not $SkipCodex) {
    & (Join-Path $PSScriptRoot "register-codex.ps1") -Url $Url -Name $Name -Optional
}

if (-not $SkipClaude) {
    & (Join-Path $PSScriptRoot "register-claude.ps1") -Url $Url -Name $Name -Optional
}
