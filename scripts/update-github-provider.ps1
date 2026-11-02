param(
    [Parameter(Mandatory)][string]$LauncherExecutable,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedSha256,
    [Parameter(Mandatory)][string]$GitHubServerExecutable,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedServerSha256,
    [Parameter(Mandatory)][string]$GhExecutable,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.4.20',
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'MissionControl')
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = [IO.Path]::GetFullPath($LauncherExecutable)
if ((Get-FileHash -LiteralPath $source).Hash -ne $ExpectedSha256) { throw 'Provider checksum changed.' }
if ((Get-FileHash -LiteralPath $GitHubServerExecutable).Hash -ne $ExpectedServerSha256) { throw 'Official server checksum changed.' }
$data = [IO.Path]::GetFullPath($DataDirectory)
$destination = Join-Path $data "providers\mission-control\$Version\mission-control-providers.exe"
$ancestor = $destination
while ($ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Refusing a redirected provider installation.' }
    $parent = Split-Path -Parent $ancestor
    if ($parent -eq $ancestor) { break }
    $ancestor = $parent
}
if (Test-Path -LiteralPath $destination) {
    if ((Get-FileHash -LiteralPath $destination).Hash -ne $ExpectedSha256) { throw 'Versioned provider already contains different bytes.' }
} else {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
}
foreach ($hostName in @('codex','claude')) {
    $target = if ($hostName -eq 'codex') { Join-Path $env:USERPROFILE '.codex\config.toml' } else { Join-Path $env:USERPROFILE '.claude.json' }
    & python (Join-Path $root 'integrations\configure_github_provider.py') --host $hostName --target $target --launcher $destination --gh $GhExecutable --server $GitHubServerExecutable --server-sha256 $ExpectedServerSha256
    if ($LASTEXITCODE -ne 0) { throw "Provider setup stopped for $hostName; no unrelated settings were changed." }
}
[pscustomobject]@{version=$Version;installed=$true;credentialsPersistedByLauncher=$false;readOnly=$true;next='Reload the host to use the existing GitHub CLI login.'} | ConvertTo-Json
