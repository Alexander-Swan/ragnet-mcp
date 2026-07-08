param(
    [switch]$RemoveFiles,
    [switch]$SkipCodex,
    [switch]$SkipClaude
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")

if ($RemoveFiles) {
    $targets = @(
        (Join-Path $RepoRoot ".mcp.json"),
        (Join-Path $RepoRoot ".vscode\mcp.json")
    )

    foreach ($target in $targets) {
        if (Test-Path $target) {
            Remove-Item -LiteralPath $target
            Write-Host "Removed $target"
        }
    }
}
else {
    Write-Host "RagNet MCP registration is repo-local:"
    Write-Host "  .mcp.json"
    Write-Host "  .vscode/mcp.json"
    Write-Host "Codex registration is user-local:"
    Write-Host "  $HOME\.codex\config.toml"
    Write-Host "Claude Code registration is user-local by default:"
    Write-Host "  claude mcp list"
    Write-Host "Run with -RemoveFiles to remove repo-local files, Codex registration, and Claude Code registration."
}

if ($RemoveFiles -and -not $SkipCodex) {
    $codex = Get-Command "codex" -ErrorAction SilentlyContinue
    if ($codex) {
        try {
            & codex mcp remove "ragnet-mcp" | Out-Host
            Write-Host "Removed Codex MCP server: ragnet-mcp"
        }
        catch {
            Write-Warning "Codex MCP server ragnet-mcp was not removed: $($_.Exception.Message)"
        }
    }
    else {
        Write-Warning "Codex CLI was not found on PATH; skipping Codex MCP unregister."
    }
}

if ($RemoveFiles -and -not $SkipClaude) {
    $claude = Get-Command "claude" -ErrorAction SilentlyContinue
    if ($claude) {
        try {
            & claude mcp remove --scope user "ragnet-mcp" | Out-Host
            Write-Host "Removed Claude Code MCP server: ragnet-mcp"
        }
        catch {
            Write-Warning "Claude Code MCP server ragnet-mcp was not removed: $($_.Exception.Message)"
        }
    }
    else {
        Write-Warning "Claude Code CLI was not found on PATH; skipping Claude Code MCP unregister."
    }
}
