param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskRoot ('artifacts/crash-' + [Guid]::NewGuid().ToString('N')) }
$taskProbe = Join-Path $taskRoot 'tests/MissionControl.CrashProbe/bin/Release/net10.0/MissionControl.CrashProbe.dll'
& dotnet $taskProbe crash $OutputDirectory
if ($LASTEXITCODE -ne 73) { throw "Expected abrupt exit 73, received $LASTEXITCODE." }
$taskResult = & dotnet $taskProbe verify $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Crash recovery verification failed.' }
$taskResult | Set-Content -LiteralPath (Join-Path $OutputDirectory 'result.json') -Encoding UTF8
$taskResult
