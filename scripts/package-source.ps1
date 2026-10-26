param([string]$Destination = 'artifacts/release/MissionControl-source.zip')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
Push-Location $taskRoot
try {
    & python (Join-Path $PSScriptRoot 'package-source.py') --output $Destination
    if ($LASTEXITCODE -ne 0) { throw 'Source packaging failed.' }
} finally { Pop-Location }
