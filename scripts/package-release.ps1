param([string]$Version='0.4.0')
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$buildVersion=(Get-Content -Raw -LiteralPath (Join-Path $taskRoot 'src-tauri/tauri.conf.json') | ConvertFrom-Json).version
if (-not $PSBoundParameters.ContainsKey('Version')) { $Version=$buildVersion }
if ($Version -ne $buildVersion) { throw 'Release label must match the desktop build version.' }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version.' }
$releaseRoot=Join-Path $taskRoot ('artifacts/release/'+$Version)
if ((Test-Path -LiteralPath $releaseRoot) -and @(Get-ChildItem -LiteralPath $releaseRoot -Force).Count) { throw 'Release directory is not empty. Preserve it and choose a new version.' }
New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
$build=Join-Path $taskRoot 'src-tauri/target/x86_64-pc-windows-msvc/release'
$portable=Join-Path $releaseRoot 'MissionControl-windows-x64'
New-Item -ItemType Directory -Path $portable | Out-Null
foreach ($name in @('mission-control.exe','mission-control-backend.exe','mission-control-mcp.exe','mission-control-providers.exe')) { Copy-Item -LiteralPath (Join-Path $build $name) -Destination (Join-Path $portable $name) }
Copy-Item -LiteralPath (Join-Path $taskRoot 'integrations') -Destination (Join-Path $portable 'integrations') -Recurse
# Exclude development-only host probes and Python cache from the portable app.
$testPath=[IO.Path]::GetFullPath((Join-Path $portable 'integrations/tests'))
if (-not $testPath.StartsWith($releaseRoot+[IO.Path]::DirectorySeparatorChar)) { throw 'Unexpected staging path.' }
if (Test-Path -LiteralPath $testPath) { Remove-Item -LiteralPath $testPath -Recurse -Force }
$cachePath=[IO.Path]::GetFullPath((Join-Path $portable 'integrations/__pycache__'))
if (-not $cachePath.StartsWith($releaseRoot+[IO.Path]::DirectorySeparatorChar)) { throw 'Unexpected cache path.' }
if (Test-Path -LiteralPath $cachePath) { Remove-Item -LiteralPath $cachePath -Recurse -Force }
foreach ($name in @('README.md','KNOWN_LIMITATIONS.md','BENCHMARKS.md','FINAL_INTEGRATION_CHECKLIST.md')) { Copy-Item -LiteralPath (Join-Path $taskRoot $name) -Destination (Join-Path $portable $name) }
$installerName="Mission Control_${Version}_x64-setup.exe"
Copy-Item -LiteralPath (Join-Path $build ('bundle/nsis/'+$installerName)) -Destination (Join-Path $releaseRoot $installerName)
# Generate host packages on the recipient's computer using integrations/setup.py.
# Generated manifests contain absolute paths and must never be shipped from the builder's profile.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Archive-Directory([string]$Directory,[string]$Destination) {
    $archive=[IO.Compression.ZipArchive]::new([IO.File]::Create($Destination),[IO.Compression.ZipArchiveMode]::Create,$false)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File -Force) {
            $relative=$file.FullName.Substring($Directory.Length+1).Replace('\','/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,$relative,[IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose() }
}
Archive-Directory $portable (Join-Path $releaseRoot "MissionControl-${Version}-windows-x64.zip")
& (Join-Path $taskRoot 'scripts/package-source.ps1') -Destination (Join-Path $releaseRoot "MissionControl-${Version}-source.zip")
$checksums=foreach ($file in Get-ChildItem -LiteralPath $releaseRoot -File | Where-Object { $_.Extension -in @('.zip','.exe') }) { $hash=Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256; $hash.Hash.ToLowerInvariant()+'  '+$file.Name }
[IO.File]::WriteAllLines((Join-Path $releaseRoot 'SHA256SUMS.txt'),$checksums,[Text.UTF8Encoding]::new($false))
[pscustomobject]@{releaseDirectory=$releaseRoot;note='Recipient runs integrations/setup.py to generate local host packages. No developer paths, host settings, history or credentials are packaged.'} | ConvertTo-Json
