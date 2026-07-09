param(
    [string]$Url = "http://localhost:7331/ragnet-mcp",
    [string]$Name = "ragnet-mcp",
    [switch]$Optional
)

$ErrorActionPreference = "Stop"

function Resolve-CommandSource {
    param(
        [string]$Name,
        [string[]]$CandidatePaths = @()
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    foreach ($candidate in $CandidatePaths) {
        if ($candidate -and (Test-Path $candidate)) {
            return $candidate
        }
    }

    return $null
}

function Invoke-ExternalCommand {
    param(
        [string]$Executable,
        [string[]]$Arguments
    )

    $output = & $Executable @Arguments 2>&1
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = ($output | Out-String).Trim()
    }
}

function Test-McpCommand {
    param(
        [string]$Executable,
        [string[]]$HelpArguments
    )

    try {
        $result = Invoke-ExternalCommand -Executable $Executable -Arguments $HelpArguments
        return $result.ExitCode -eq 0 -and $result.Output -match "\bmcp\b"
    }
    catch {
        return $false
    }
}

function Register-WithCopilotCommand {
    param(
        [string]$Executable,
        [string[]]$HelpArguments,
        [string[][]]$RemoveVariants,
        [string[][]]$AddVariants,
        [string]$DisplayName
    )

    if (-not (Test-McpCommand -Executable $Executable -HelpArguments $HelpArguments)) {
        return $false
    }

    foreach ($variant in $RemoveVariants) {
        try {
            $null = Invoke-ExternalCommand -Executable $Executable -Arguments $variant
        }
        catch {
            # Removal can fail when the server is not registered yet.
        }
    }

    $lastError = $null
    foreach ($variant in $AddVariants) {
        $result = Invoke-ExternalCommand -Executable $Executable -Arguments $variant
        if ($result.ExitCode -eq 0) {
            Write-Host "Registered GitHub Copilot CLI MCP server:"
            Write-Host "  $Name -> $Url"
            Write-Host "  CLI: $DisplayName"
            return $true
        }

        $lastError = $result.Output
    }

    if ($lastError) {
        throw "GitHub Copilot CLI MCP registration failed for ${DisplayName}: $lastError"
    }

    throw "GitHub Copilot CLI MCP registration failed for ${DisplayName}."
}

$copilotCommand = Resolve-CommandSource -Name "copilot"
if ($copilotCommand) {
    $registered = Register-WithCopilotCommand `
        -Executable $copilotCommand `
        -HelpArguments @("mcp", "--help") `
        -RemoveVariants @(
            @("mcp", "remove", $Name),
            @("mcp", "delete", $Name)
        ) `
        -AddVariants @(
            @("mcp", "add", $Name, "--url", $Url),
            @("mcp", "add", $Name, $Url),
            @("mcp", "add", "--transport", "http", $Name, $Url)
        ) `
        -DisplayName "copilot"

    if ($registered) {
        return
    }
}

$ghCandidatePaths = @()
if ($env:ProgramFiles) {
    $ghCandidatePaths += (Join-Path $env:ProgramFiles "GitHub CLI\gh.exe")
}

if ($env:LOCALAPPDATA) {
    $ghCandidatePaths += (Join-Path $env:LOCALAPPDATA "GitHub CLI\gh.exe")
}

$ghCommand = Resolve-CommandSource -Name "gh" -CandidatePaths $ghCandidatePaths
if ($ghCommand) {
    $registered = Register-WithCopilotCommand `
        -Executable $ghCommand `
        -HelpArguments @("copilot", "--", "mcp", "--help") `
        -RemoveVariants @(
            @("copilot", "--", "mcp", "remove", $Name),
            @("copilot", "--", "mcp", "delete", $Name)
        ) `
        -AddVariants @(
            @("copilot", "--", "mcp", "add", $Name, "--url", $Url),
            @("copilot", "--", "mcp", "add", $Name, $Url),
            @("copilot", "--", "mcp", "add", "--transport", "http", $Name, $Url)
        ) `
        -DisplayName "gh copilot"

    if ($registered) {
        return
    }
}

$message = "GitHub Copilot CLI with MCP support was not found on PATH; skipping GitHub Copilot CLI MCP registration."
if ($Optional) {
    Write-Warning $message
    return
}

throw $message
