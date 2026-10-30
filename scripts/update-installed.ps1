param(
    [Parameter(Mandatory)][string]$Installer,
    [Parameter(Mandatory)][string]$InstalledExecutable,
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'MissionControl')
)
$ErrorActionPreference = 'Stop'
$Installer = [IO.Path]::GetFullPath($Installer)
$InstalledExecutable = [IO.Path]::GetFullPath($InstalledExecutable)
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
if (-not (Test-Path -LiteralPath $Installer -PathType Leaf) -or [IO.Path]::GetExtension($Installer) -ne '.exe') { throw 'Select the verified Mission Control installer executable.' }
if (-not (Test-Path -LiteralPath $InstalledExecutable -PathType Leaf)) { throw 'Select the existing installed desktop executable.' }
$backend = Join-Path (Split-Path -Parent $InstalledExecutable) 'mission-control-backend.exe'
$running = @(Get-CimInstance Win32_Process -Property ExecutablePath | Where-Object { $_.ExecutablePath -in @($InstalledExecutable,$backend) })
if ($running.Count) { throw 'Use the Mission Control tray menu: Quit completely. Then run this helper again; no running process has been stopped.' }
# Copy only closed SQLite files. The encrypted archive preserves the old schema,
# settings and retired provider references without printing their contents.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.Security
$memory = [IO.MemoryStream]::new()
$archive = [IO.Compression.ZipArchive]::new($memory,[IO.Compression.ZipArchiveMode]::Create,$true)
try {
    foreach ($mode in @('live')) {
        $modeDirectory=Join-Path $DataDirectory $mode
        foreach ($name in @('workspace.db','workspace.db-wal','workspace.db-shm')) {
            $file=Join-Path $modeDirectory $name
            if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
            $inputFile=[IO.File]::Open($file,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
            $entry=$archive.CreateEntry("$mode/$name",[IO.Compression.CompressionLevel]::Optimal)
            $outputFile=$entry.Open()
            try { $inputFile.CopyTo($outputFile) } finally { $inputFile.Dispose();$outputFile.Dispose() }
        }
    }
} finally { $archive.Dispose() }
$plain=$memory.ToArray()
try {
    $protected=[Security.Cryptography.ProtectedData]::Protect($plain,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
    $check=[Security.Cryptography.ProtectedData]::Unprotect($protected,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
    if ([Convert]::ToBase64String($plain) -ne [Convert]::ToBase64String($check)) { throw 'Upgrade backup verification failed.' }
} finally { [Array]::Clear($plain,0,$plain.Length);if ($check) { [Array]::Clear($check,0,$check.Length) };$memory.Dispose() }
$backupDirectory=Join-Path $DataDirectory 'upgrade-backups'
New-Item -ItemType Directory -Force -Path $backupDirectory | Out-Null
$backup=Join-Path $backupDirectory ('before-0.4-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)+'.zip.dpapi')
$stream=[IO.File]::Open($backup,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write)
try { $stream.Write($protected,0,$protected.Length);$stream.Flush($true) } finally { $stream.Dispose() }
$installation=Start-Process -FilePath $Installer -ArgumentList '/S' -WindowStyle Hidden -PassThru -Wait
if ($installation.ExitCode -ne 0) { throw "Installer failed with exit $($installation.ExitCode). Backup: $backup" }
# Keep the version just installed; the memory server the host packages use is protected separately.
$installedVersion = ((Get-Item -LiteralPath $backend).VersionInfo.ProductVersion -split '[+-]')[0]
if ($installedVersion -notmatch '^\d+\.\d+\.\d+$') { throw "Installed version could not be read ($installedVersion); storage maintenance was skipped. Backup: $backup" }
& (Join-Path $PSScriptRoot 'maintain-storage.ps1') -DataDirectory $DataDirectory -CurrentVersion $installedVersion -Apply
[pscustomobject]@{ installed=$true; encryptedBackup=$backup; next='Open Mission Control. Existing settings and project approvals are preserved.' } | ConvertTo-Json
