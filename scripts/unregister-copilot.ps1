param(
    [switch]$RemoveFiles
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
    Write-Host "Run with -RemoveFiles to delete those files."
}
