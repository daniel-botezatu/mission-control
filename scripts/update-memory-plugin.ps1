param(
    [Parameter(Mandatory)][string]$McpExecutable,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedSha256,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.4.19',
    [ValidateSet('allow','public','ask')][string]$PrivateSlack,
    [string]$TimeZone,
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'MissionControl')
)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSource = [IO.Path]::GetFullPath($McpExecutable)
$taskData = [IO.Path]::GetFullPath($DataDirectory)
if ((Get-FileHash -LiteralPath $taskSource -Algorithm SHA256).Hash -ne $ExpectedSha256) { throw 'Verified MCP checksum changed.' }
& python (Join-Path $taskRoot 'integrations\verify_memory_package.py') --exe $taskSource --data-dir $taskData --version $Version
if ($LASTEXITCODE -ne 0) { throw 'Standalone memory-server preflight failed; installation was not changed.' }
$taskDestinationRoot = Join-Path $taskData ('mcp\' + $Version)
$taskDestination = Join-Path $taskDestinationRoot 'mission-control-mcp.exe'
$taskAncestor = $taskDestination
while ($taskAncestor) {
    if ((Test-Path -LiteralPath $taskAncestor) -and ((Get-Item -LiteralPath $taskAncestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Refusing a redirected installation path.' }
    $taskParent = Split-Path -Parent $taskAncestor
    if ($taskParent -eq $taskAncestor) { break }
    $taskAncestor = $taskParent
}
# Existing client processes keep their previous binary. Never stop an AI host,
# MCP reader, or collector just to update the read-only memory interface.
if (Test-Path -LiteralPath $taskDestination) {
    if ((Get-FileHash -LiteralPath $taskDestination -Algorithm SHA256).Hash -ne $ExpectedSha256) { throw 'Versioned MCP path already contains different bytes; preserve it and use a new version.' }
} else {
    New-Item -ItemType Directory -Path $taskDestinationRoot -Force | Out-Null
    Copy-Item -LiteralPath $taskSource -Destination $taskDestination -ErrorAction Stop
}
$taskBundle = Join-Path $taskData 'host-packages'
$taskPreference = @()
if ($PrivateSlack) { $taskPreference += @('--private-slack', $PrivateSlack) }
if ($TimeZone) { $taskPreference += @('--time-zone', $TimeZone) }
& python (Join-Path $taskRoot 'integrations\manage.py') prepare --output $taskBundle --mcp $taskDestination --data-dir $taskData @taskPreference
if ($LASTEXITCODE -ne 0) { throw 'Host bundle preparation failed.' }
& python (Join-Path $taskRoot 'integrations\manage.py') apply-config --host codex --bundle $taskBundle --target (Join-Path $env:USERPROFILE '.codex\config.toml')
if ($LASTEXITCODE -ne 0) { throw 'Owned Codex configuration update failed.' }
& codex.cmd plugin add mission-control@mission-control-local
if ($LASTEXITCODE -ne 0) { throw 'Native Codex plugin refresh failed.' }
& claude plugin update mission-control@mission-control-local --scope user --json
if ($LASTEXITCODE -ne 0) { throw 'Native Claude plugin refresh failed.' }
& (Join-Path $PSScriptRoot 'maintain-storage.ps1') -DataDirectory $taskData -CurrentVersion $Version -Apply -VersionsOnly
& (Join-Path $PSScriptRoot 'maintain-plugin-cache.ps1') -CurrentVersion $Version -Apply
[pscustomobject]@{ version = $Version; installedMcp = $taskDestination; collectorStopped = $false; readersStopped = $false; databaseWritesRequested = $false; next = 'Reconnect/restart the host to load the new tool schema and skill. Existing clients retain their previous process until then.' } | ConvertTo-Json
