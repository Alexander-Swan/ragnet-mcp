param(
    [string]$Url = "http://localhost:7331/ragnet-mcp",
    [string]$Name = "ragnet-mcp",
    [switch]$Optional
)

$ErrorActionPreference = "Stop"

$codex = Get-Command "codex" -ErrorAction SilentlyContinue
if (-not $codex) {
    $message = "Codex CLI was not found on PATH; skipping Codex MCP registration."
    if ($Optional) {
        Write-Warning $message
        return
    }

    throw $message
}

try {
    & codex mcp remove $Name 2>$null | Out-Null
}
catch {
    # The remove command fails when the server is not registered yet.
}

& codex mcp add $Name --url $Url | Out-Host

Write-Host "Registered Codex MCP server:"
Write-Host "  $Name -> $Url"
Write-Host "  Config: $HOME\.codex\config.toml"
