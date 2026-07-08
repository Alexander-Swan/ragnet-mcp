param(
    [string]$Url = "http://localhost:7331/ragnet-mcp",
    [string]$Name = "ragnet-mcp",
    [ValidateSet("local", "user", "project")]
    [string]$Scope = "user",
    [switch]$Optional
)

$ErrorActionPreference = "Stop"

$claude = Get-Command "claude" -ErrorAction SilentlyContinue
if (-not $claude) {
    $message = "Claude Code CLI was not found on PATH; skipping Claude Code MCP registration."
    if ($Optional) {
        Write-Warning $message
        return
    }

    throw $message
}

try {
    & claude mcp remove --scope $Scope $Name 2>$null | Out-Null
}
catch {
    # The remove command fails when the server is not registered in this scope yet.
}

& claude mcp add --scope $Scope --transport http $Name $Url | Out-Host

Write-Host "Registered Claude Code MCP server:"
Write-Host "  $Name -> $Url"
Write-Host "  Scope: $Scope"
