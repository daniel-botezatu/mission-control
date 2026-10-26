param(
    [Parameter(Mandatory)][string]$DataDirectory,
    # The memory server version being installed. Required: a stale default once removed the server
    # that Claude/Codex were configured to use whenever it was not running at that moment.
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$CurrentVersion,
    [switch]$Apply,
    [switch]$VersionsOnly
)
$ErrorActionPreference = 'Stop'
$taskData = [IO.Path]::GetFullPath($DataDirectory).TrimEnd('\','/')
function Assert-OwnedPath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($taskData + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Maintenance path escapes the selected data directory.' }
    $ancestor = $resolved
    while ($ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Maintenance refuses redirected paths.' }
        $parent = Split-Path -Parent $ancestor
        if ($parent -eq $ancestor) { break }
        $ancestor = $parent
    }
    return $resolved
}
$candidates = @()
if (-not $VersionsOnly) {
    $backupRoot = Assert-OwnedPath (Join-Path $taskData 'upgrade-backups')
    $backups = @(Get-ChildItem -LiteralPath $backupRoot -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^before-[\w.-]+\.zip\.dpapi$' } | Sort-Object LastWriteTimeUtc -Descending)
    if ($backups.Count -ge 2) {
        Add-Type -AssemblyName System.Security
        Add-Type -AssemblyName System.IO.Compression
        foreach ($backup in $backups | Select-Object -First 2) {
            $path = Assert-OwnedPath $backup.FullName
            $plain = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($path), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
            $memory = [IO.MemoryStream]::new($plain, $false)
            try {
                $archive = [IO.Compression.ZipArchive]::new($memory, [IO.Compression.ZipArchiveMode]::Read)
                try {
                    $entry = $archive.GetEntry('live/workspace.db')
                    if (-not $entry) { throw 'Retained backup lacks the live database; no cleanup performed.' }
                    $input = $entry.Open(); $header = New-Object byte[] 16
                    try { $read = $input.Read($header, 0, 16) } finally { $input.Dispose() }
                    if ($read -ne 16 -or [Text.Encoding]::ASCII.GetString($header) -ne "SQLite format 3`0") { throw 'Retained backup is not a valid SQLite archive.' }
                } finally { $archive.Dispose() }
            } finally { $memory.Dispose(); [Array]::Clear($plain, 0, $plain.Length) }
        }
        $candidates += $backups | Select-Object -Skip 2
        foreach ($mode in @('live','demo')) {
            $migrationRoot = Assert-OwnedPath (Join-Path $taskData ($mode + '\migration-backups'))
            $candidates += Get-ChildItem -LiteralPath $migrationRoot -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^workspace-v\d+-\d{8}T\d+-[a-f0-9]{8}\.sqlite$' }
        }
    }
}
$versionRoot = Assert-OwnedPath (Join-Path $taskData 'mcp')
$running = @(Get-CimInstance Win32_Process -Property ExecutablePath | ForEach-Object { $_.ExecutablePath } | Where-Object { $_ })
# Never remove the server that the prepared Claude/Codex packages point to, even when no host is
# running it right now (for example right after a desktop-only upgrade).
$configured = @()
$packages = Join-Path $taskData 'host-packages'
foreach ($config in @('installation.json','claude\.mcp.json','codex\.mcp.json','codex\mcp.json')) {
    $file = Join-Path $packages $config
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
    try { $text = Get-Content -LiteralPath $file -Raw -ErrorAction Stop } catch { continue }
    foreach ($match in [regex]::Matches($text, 'mcp[\\/]+(\d+\.\d+\.\d+)[\\/]+mission-control-mcp')) { $configured += $match.Groups[1].Value }
}
$deferred = @()
foreach ($directory in Get-ChildItem -LiteralPath $versionRoot -Directory -ErrorAction SilentlyContinue) {
    if ($directory.Name -notmatch '^\d+\.\d+\.\d+$' -or $directory.Name -eq $CurrentVersion -or $configured -contains $directory.Name) { continue }
    $path = Assert-OwnedPath $directory.FullName
    if ($running | Where-Object { $_.StartsWith($path + '\', [StringComparison]::OrdinalIgnoreCase) }) { $deferred += $directory.Name; continue }
    # Remove only the known binary; never recursively erase unknown contents.
    $binary = Join-Path $path 'mission-control-mcp.exe'
    if (Test-Path -LiteralPath $binary -PathType Leaf) { $candidates += Get-Item -LiteralPath $binary }
}
$bytes = 0L; $removed = 0
foreach ($candidate in $candidates) {
    $path = Assert-OwnedPath $candidate.FullName
    $length = $candidate.Length
    if ($Apply) {
        try { Remove-Item -LiteralPath $path -Force -ErrorAction Stop; $removed++ }
        catch [IO.IOException] { continue } # a newly started reader keeps its old executable
    }
    $bytes += $length
}
if ($Apply) {
    foreach ($directory in Get-ChildItem -LiteralPath $versionRoot -Directory -ErrorAction SilentlyContinue) {
        if ($directory.Name -notmatch '^\d+\.\d+\.\d+$' -or $directory.Name -eq $CurrentVersion -or $configured -contains $directory.Name) { continue }
        $path = Assert-OwnedPath $directory.FullName
        if (@(Get-ChildItem -LiteralPath $path -Force).Count -eq 0) { Remove-Item -LiteralPath $path }
    }
}
[pscustomobject]@{ applied = [bool]$Apply; candidates = $candidates.Count; removed = $removed; bytes = $bytes; versionsInUseDeferred = $deferred; configuredVersionsKept = @($configured | Sort-Object -Unique); retainedBackupLimit = 2 } | ConvertTo-Json
