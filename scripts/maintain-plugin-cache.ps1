param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$CurrentVersion,
    [string]$ProfileDirectory = $env:USERPROFILE,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$profileRoot = [IO.Path]::GetFullPath($ProfileDirectory).TrimEnd('\','/')
$removed = 0; $bytes = 0L
foreach ($hostName in @('.claude','.codex')) {
    $cacheRoot = [IO.Path]::GetFullPath((Join-Path $profileRoot "$hostName\plugins\cache\mission-control-local\mission-control"))
    if (-not (Test-Path -LiteralPath $cacheRoot -PathType Container)) { continue }
    # Validate every ancestor; a cache redirect must not escape this named root.
    $ancestor = $cacheRoot
    while ($ancestor) {
        if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing a redirected plugin cache.' }
        $parent = Split-Path -Parent $ancestor
        if ($parent -eq $ancestor) { break }
        $ancestor = $parent
    }
    $versions = @(Get-ChildItem -LiteralPath $cacheRoot -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } | Sort-Object { [Version]$_.Name } -Descending)
    # Keep the immediate prior release for hosts which have not yet reloaded.
    $previous = $versions | Where-Object { [Version]$_.Name -lt [Version]$CurrentVersion } | Select-Object -First 1
    foreach ($version in $versions) {
        if ([Version]$version.Name -ge [Version]$CurrentVersion -or $version.Name -eq $previous.Name) { continue }
        $target = [IO.Path]::GetFullPath($version.FullName)
        if (-not $target.StartsWith($cacheRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Plugin cache target escapes the named cache.' }
        $manifest = Join-Path $target $(if ($hostName -eq '.claude') { '.claude-plugin\plugin.json' } else { '.codex-plugin\plugin.json' })
        $pending = [Collections.Generic.Stack[string]]::new(); $pending.Push($target)
        $size = 0L
        while ($pending.Count) {
            $directory = $pending.Pop()
            if ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing redirected cache contents.' }
            foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
                if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing redirected cache contents.' }
                if ($item.PSIsContainer) { $pending.Push($item.FullName) } else { $size += $item.Length }
            }
        }
        if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { continue }
        $identity = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        if ($identity.name -ne 'mission-control' -or $identity.version -ne $version.Name) { continue }
        if ($Apply) { Remove-Item -LiteralPath $target -Recurse -Force; $removed++ }
        $bytes += $size
    }
}
[pscustomobject]@{applied=[bool]$Apply;removed=$removed;bytes=$bytes;retention='current and prior release; newer and unrecognized folders untouched'} | ConvertTo-Json
