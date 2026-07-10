param(
    [string]$Url = "http://localhost:7331/ragnet-mcp",
    [string]$Name = "ragnet-mcp",
    [string]$ConfigPath = $env:COPILOT_MCP_CONFIG,
    [switch]$Optional
)

$ErrorActionPreference = "Stop"

function Get-DefaultCopilotMcpConfigPath {
    if ($ConfigPath) {
        return $ConfigPath
    }

    if ($env:USERPROFILE) {
        return (Join-Path $env:USERPROFILE ".copilot\mcp.json")
    }

    if ($env:HOME) {
        return (Join-Path $env:HOME ".copilot/mcp.json")
    }

    throw "Cannot determine a user profile directory for GitHub Copilot CLI MCP config."
}

function Read-JsonObject {
    param([Parameter(Mandatory = $true)] [string]$Path)

    if (-not (Test-Path $Path)) {
        return [ordered]@{}
    }

    $text = [System.IO.File]::ReadAllText($Path)
    if ([string]::IsNullOrWhiteSpace($text)) {
        return [ordered]@{}
    }

    $converted = ConvertTo-Hashtable ($text | ConvertFrom-Json)
    if ($converted -is [System.Collections.IDictionary]) {
        return $converted
    }

    return [ordered]@{}
}

function ConvertTo-Hashtable {
    param([object]$Value)

    if ($null -eq $Value) {
        return [ordered]@{}
    }

    if ($Value -is [System.Collections.IDictionary]) {
        $result = [ordered]@{}
        foreach ($key in $Value.Keys) {
            $result[$key] = ConvertTo-Hashtable $Value[$key]
        }

        return $result
    }

    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        return @($Value | ForEach-Object { ConvertTo-Hashtable $_ })
    }

    if ($Value.PSObject.Properties.Count -gt 0 -and $Value.GetType().Namespace -ne "System") {
        $result = [ordered]@{}
        foreach ($property in $Value.PSObject.Properties) {
            $result[$property.Name] = ConvertTo-Hashtable $property.Value
        }

        return $result
    }

    return $Value
}

function Write-Utf8Json {
    param(
        [Parameter(Mandatory = $true)] [object]$Value,
        [Parameter(Mandatory = $true)] [string]$Path
    )

    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path $directory)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }

    $json = $Value | ConvertTo-Json -Depth 16
    [System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Set-McpServer {
    param(
        [Parameter(Mandatory = $true)] [System.Collections.IDictionary]$Config,
        [Parameter(Mandatory = $true)] [string]$SectionName,
        [Parameter(Mandatory = $true)] [string]$ServerName,
        [Parameter(Mandatory = $true)] [string]$ServerUrl
    )

    if (-not $Config.Contains($SectionName) -or $null -eq $Config[$SectionName]) {
        $Config[$SectionName] = [ordered]@{}
    }

    $Config[$SectionName][$ServerName] = [ordered]@{
        type = "http"
        transport = "http"
        url = $ServerUrl
    }
}

try {
    $resolvedConfigPath = Get-DefaultCopilotMcpConfigPath
    $config = Read-JsonObject -Path $resolvedConfigPath

    if ($config.Contains("servers")) {
        Set-McpServer -Config $config -SectionName "servers" -ServerName $Name -ServerUrl $Url
    }
    else {
        Set-McpServer -Config $config -SectionName "mcpServers" -ServerName $Name -ServerUrl $Url
    }

    Write-Utf8Json -Value $config -Path $resolvedConfigPath

    Write-Host "Registered GitHub Copilot CLI MCP config:"
    Write-Host "  $Name -> $Url"
    Write-Host "  Config: $resolvedConfigPath"
}
catch {
    if ($Optional) {
        Write-Warning "GitHub Copilot CLI MCP config was not written: $($_.Exception.Message)"
        return
    }

    throw
}
