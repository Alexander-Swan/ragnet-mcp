param(
    [string]$Url = "http://localhost:7331/ragnet-mcp",
    [string]$Name = "ragnet-mcp",
    [switch]$SkipCodex,
    [switch]$SkipClaude
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$VsCodeDir = Join-Path $RepoRoot ".vscode"

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

$VisualStudioConfig |
    ConvertTo-Json -Depth 8 |
    Set-Content -Path (Join-Path $RepoRoot ".mcp.json") -Encoding utf8

$VsCodeConfig |
    ConvertTo-Json -Depth 8 |
    Set-Content -Path (Join-Path $VsCodeDir "mcp.json") -Encoding utf8

Write-Host "Registered MCP configs:"
Write-Host "  Visual Studio: .mcp.json"
Write-Host "  VS Code:       .vscode/mcp.json"

if (-not $SkipCodex) {
    & (Join-Path $PSScriptRoot "register-codex.ps1") -Url $Url -Name $Name -Optional
}

if (-not $SkipClaude) {
    & (Join-Path $PSScriptRoot "register-claude.ps1") -Url $Url -Name $Name -Optional
}
