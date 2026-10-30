[CmdletBinding()]
param(
    [string]$Executable = '',
    [string]$ArtifactRoot = '',
    [ValidateRange(300, 3600)][int]$QuietSeconds = 300,
    [ValidateRange(1024, 65535)][int]$AutomationPort = 9241

)

# The release application has no diagnostics or benchmark timer enabled. All
# sampling happens in this external process and is excluded from app counters.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Executable) { $Executable = Join-Path $repository 'src-tauri/target/release/mission-control.exe' }
if (-not $ArtifactRoot) { $ArtifactRoot = Join-Path $repository 'artifacts/tauri-verification' }
$Executable = [IO.Path]::GetFullPath($Executable)
$ArtifactRoot = [IO.Path]::GetFullPath($ArtifactRoot)
if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw "Release executable is missing: $Executable" }
$artifactDirectory = Join-Path $ArtifactRoot ('benchmark-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
if (Test-Path -LiteralPath $artifactDirectory) { throw 'Refusing to reuse benchmark artifacts.' }
$null = New-Item -ItemType Directory -Path $artifactDirectory
$dataDirectory = Join-Path $artifactDirectory 'data'
$sourceExecutable = $Executable
$backendExecutable = Join-Path (Split-Path $sourceExecutable -Parent) 'mission-control-backend.exe'
if (-not (Test-Path -LiteralPath $backendExecutable -PathType Leaf)) { throw 'The release package must include mission-control-backend.exe beside its desktop executable.' }
$packageDirectory = Join-Path $artifactDirectory 'package'
$null = New-Item -ItemType Directory -Path $packageDirectory
Copy-Item -LiteralPath $sourceExecutable, $backendExecutable -Destination $packageDirectory
$Executable = Join-Path $packageDirectory (Split-Path $sourceExecutable -Leaf)
$helper = Join-Path $repository 'ui/tests/tauri-lifecycle.mjs'
$root = $null
$tracked = @{}
$samples = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[object]]::new()
$failure = $null
$allExited = $false
$closedWebViews = $false
$quietStart = $null
$quietEnd = $null
$hardware = [ordered]@{ logicalProcessors = [Environment]::ProcessorCount; os = [Environment]::OSVersion.VersionString }
$oldCdp = $env:MC_CDP_URL
$oldData = $env:MC_NATIVE_TEST_DATA_ROOT
$env:MC_CDP_URL = "http://127.0.0.1:$AutomationPort"
$env:MC_NATIVE_TEST_DATA_ROOT = $dataDirectory

function Launch-TestApp {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Executable
    $info.Arguments = $arguments -join ' '
    $info.WorkingDirectory = Split-Path $Executable -Parent
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    return [Diagnostics.Process]::Start($info)
}
function Write-Json([string]$File, $Value) {
    [IO.File]::WriteAllText((Join-Path $artifactDirectory $File), ($Value | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
}
function Quote-Argument([string]$Value) {
    if ($Value.Contains('"')) { throw 'An application argument cannot contain a quotation mark.' }
    return '"' + $Value.TrimEnd('\') + '"'
}
function Process-StartTicks([Diagnostics.Process]$Process) {
    try {
        $started = $Process.StartTime
        if ($null -eq $started) { return 0L }
        return $started.ToUniversalTime().Ticks
    } catch [InvalidOperationException] { return 0L } catch [ComponentModel.Win32Exception] { return 0L }
}
function Register-OwnedProcess([Diagnostics.Process]$Process, [int]$ParentId) {
    try {
        $ticks = Process-StartTicks $Process
        if ($ticks -le 0) { return }
        $key = '{0}:{1}' -f $Process.Id, $ticks
        if (-not $tracked.ContainsKey($key)) {
            $tracked[$key] = [pscustomobject]@{
                pid = $Process.Id; startUtcTicks = $ticks; parentPid = $ParentId
                name = $Process.ProcessName; cpuSeconds = 0.0; alive = $true
                firstSeenUtc = [DateTime]::UtcNow.ToString('O')
            }
        }
    } catch [InvalidOperationException] { } catch [ComponentModel.Win32Exception] { }
}
function Discover-OwnedProcesses {
    # Read process identities/parent links only, never command lines or user data.
    $systemProcesses = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name -OperationTimeoutSec 10)
    $parents = [Collections.Generic.HashSet[int]]::new()
    foreach ($entry in @($tracked.Values)) {
        $process = Get-Process -Id $entry.pid -ErrorAction SilentlyContinue
        if ($null -eq $process) { continue }
        try {
            if ((Process-StartTicks $process) -eq $entry.startUtcTicks -and -not $process.HasExited) { $null = $parents.Add($entry.pid) }
        } catch [InvalidOperationException] { } catch [ComponentModel.Win32Exception] { }
        finally { $process.Dispose() }
    }
    do {
        $added = $false
        foreach ($row in $systemProcesses) {
            if ($parents.Contains([int]$row.ParentProcessId) -and -not $parents.Contains([int]$row.ProcessId)) {
                $child = Get-Process -Id ([int]$row.ProcessId) -ErrorAction SilentlyContinue
                if ($null -eq $child) { continue }
                try {
                    Register-OwnedProcess $child ([int]$row.ParentProcessId)
                    $null = $parents.Add([int]$row.ProcessId)
                    $added = $true
                } finally { $child.Dispose() }
            }
        }
    } while ($added)
}
function Sample([string]$Stage) {
    $cpu = 0.0; $private = 0L; $working = 0L; $webviews = 0; $alive = 0
    $details = [Collections.Generic.List[object]]::new()
    foreach ($entry in @($tracked.Values)) {
        $entry.alive = $false
        $process = Get-Process -Id $entry.pid -ErrorAction SilentlyContinue
        $processPrivate = 0L; $processWorking = 0L
        if ($null -ne $process) {
            try {
                if ((Process-StartTicks $process) -eq $entry.startUtcTicks -and -not $process.HasExited) {
                    $entry.alive = $true; $alive++
                    $entry.cpuSeconds = [Math]::Max($entry.cpuSeconds, $process.TotalProcessorTime.TotalSeconds)
                    $processPrivate = $process.PrivateMemorySize64; $processWorking = $process.WorkingSet64
                    if ($entry.name -like 'msedgewebview2*') { $webviews++ }
                }
            } catch [InvalidOperationException] { } catch [ComponentModel.Win32Exception] { }
            finally { $process.Dispose() }
        }
        $cpu += $entry.cpuSeconds; $private += $processPrivate; $working += $processWorking
        $details.Add([pscustomobject]@{ pid = $entry.pid; startUtcTicks = $entry.startUtcTicks; name = $entry.name; alive = $entry.alive; cpuSeconds = $entry.cpuSeconds; privateBytes = $processPrivate; workingSetBytes = $processWorking })
    }
    $sample = [pscustomobject]@{ timestampUtc = [DateTime]::UtcNow.ToString('O'); stage = $Stage; cpuSeconds = $cpu; privateBytes = $private; workingSetBytes = $working; aliveProcesses = $alive; aliveWebViews = $webviews; processes = @($details.ToArray()) }
    $samples.Add($sample)
    return $sample
}
function Drive-Dashboard([string]$Action) {
    $result = & node $helper $Action
    if ($LASTEXITCODE -ne 0) { throw "Tauri lifecycle helper failed during $Action." }
    $check = ($result | Select-Object -Last 1) | ConvertFrom-Json
    $checks.Add($check)
    return $check
}
function Assert-CoreAlive($Sample) {
    $native = @($Sample.processes | Where-Object { $_.alive -and $_.pid -eq $root.Id })
    $backend = @($Sample.processes | Where-Object { $_.alive -and $_.name -eq 'mission-control-backend' })
    if ($native.Count -ne 1 -or $backend.Count -ne 1) { throw 'The release shell and exactly one collector must remain alive throughout the quiet measurement.' }
}

Write-Output "Isolated Tauri benchmark: $artifactDirectory"
try {
    # Resolve CIM before launch so an unavailable process inventory fails without
    # leaving an untracked native host running.
    $null = Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -Property ProcessId -OperationTimeoutSec 10
    $processor = Get-CimInstance Win32_Processor -Property Name -OperationTimeoutSec 10 | Select-Object -First 1
    $machine = Get-CimInstance Win32_ComputerSystem -Property TotalPhysicalMemory -OperationTimeoutSec 10
    $hardware.cpuName = $processor.Name.Trim()
    $hardware.physicalRamBytes = [long]$machine.TotalPhysicalMemory
    $arguments = @('--data-dir', (Quote-Argument $dataDirectory), '--automation-port', $AutomationPort)
    $root = Launch-TestApp
    Register-OwnedProcess $root 0
    $null = Drive-Dashboard 'ready'
    Discover-OwnedProcesses
    $open = Sample 'open'
    Assert-CoreAlive $open
    if ($open.aliveWebViews -lt 1) { throw 'No owned WebView2 process was discovered for the open dashboard.' }
    $null = Drive-Dashboard 'hide'
    $settle = [Diagnostics.Stopwatch]::StartNew()
    do {
        Discover-OwnedProcesses
        $closed = Sample 'closed-settle'
        Assert-CoreAlive $closed
        if ($closed.aliveWebViews -eq 0) { break }
        if ($settle.Elapsed.TotalSeconds -gt 30) { throw 'Owned WebView2 processes did not exit after dashboard close.' }
        Start-Sleep -Seconds 1
    } while ($true)
    $closedWebViews = $true
    # Permit startup/finalization to settle without changing normal app behavior.
    for ($step = 0; $step -lt 10; $step++) { $null = Sample 'closed-settle'; Start-Sleep -Seconds 1 }
    Discover-OwnedProcesses
    $first = Sample 'quiet'
    $quietStart = [DateTime]::Parse($first.timestampUtc).ToUniversalTime()
    $quiet = [Diagnostics.Stopwatch]::StartNew()
    $nextDiscovery = 5.0; $nextProgress = 30.0
    do {
        Start-Sleep -Milliseconds 1000
        if ($quiet.Elapsed.TotalSeconds -ge $nextDiscovery) { Discover-OwnedProcesses; $nextDiscovery += 5 }
        $sample = Sample 'quiet'
        Assert-CoreAlive $sample
        if ($sample.aliveWebViews -ne 0) { throw 'WebView2 restarted during the closed quiet measurement.' }
        if ($quiet.Elapsed.TotalSeconds -ge $nextProgress) {
            Write-Output ('Quiet {0:N0}/{1} s: {2:N1} MiB private, {3} owned processes.' -f $quiet.Elapsed.TotalSeconds, $QuietSeconds, ($sample.privateBytes / 1MB), $sample.aliveProcesses)
            $nextProgress += 30
        }
    } while ($quiet.Elapsed.TotalSeconds -lt $QuietSeconds)
    $quietEnd = [DateTime]::Parse($sample.timestampUtc).ToUniversalTime()
    $wake = Launch-TestApp
    try {
        if (-not $wake.WaitForExit(10000)) { throw 'Second launch did not hand off to the existing isolated instance.' }
        if ($wake.ExitCode -ne 0) { throw "Second launch failed with exit code $($wake.ExitCode)." }
    } finally { $wake.Dispose() }
    $null = Drive-Dashboard 'ready'
    Discover-OwnedProcesses
    $reopened = Sample 'reopened'
    Assert-CoreAlive $reopened
    if ($reopened.aliveWebViews -lt 1) { throw 'Reopened dashboard has no owned WebView2 processes.' }
    $null = Drive-Dashboard 'quit'
    $exitTimer = [Diagnostics.Stopwatch]::StartNew()
    do {
        Discover-OwnedProcesses
        $last = Sample 'quitting'
        if ($last.aliveProcesses -eq 0) { $allExited = $true; break }
        if ($exitTimer.Elapsed.TotalSeconds -gt 35) { throw 'Quit left an owned shell, collector, or WebView process running.' }
        Start-Sleep -Seconds 1
    } while ($true)
} catch {
    $failure = $_.Exception.Message
    Write-Json 'failure-detail.json' @{ message = $_.Exception.Message; scriptStackTrace = $_.ScriptStackTrace }
} finally {
    if (-not $allExited) {
        # On failure terminate only identities proven descendants of this fresh
        # test instance. No process-name kills and no existing user data deletion.
        foreach ($entry in @($tracked.Values | Sort-Object pid -Descending)) {
            $process = Get-Process -Id $entry.pid -ErrorAction SilentlyContinue
            if ($null -eq $process) { continue }
            try {
                if ((Process-StartTicks $process) -eq $entry.startUtcTicks -and -not $process.HasExited) { Stop-Process -Id $entry.pid -Force -ErrorAction SilentlyContinue }
            } catch [InvalidOperationException] { } catch [ComponentModel.Win32Exception] { }
            finally { $process.Dispose() }
        }
    }
    $quietSamples = @($samples | Where-Object { $_.stage -eq 'quiet' })
    $measurement = $null
    if ($quietSamples.Count -gt 1) {
        $first = $quietSamples[0]; $last = $quietSamples[-1]
        $seconds = ([DateTime]::Parse($last.timestampUtc) - [DateTime]::Parse($first.timestampUtc)).TotalSeconds
        $privateIntegral = 0.0; $workingIntegral = 0.0
        for ($index = 1; $index -lt $quietSamples.Count; $index++) {
            $a = $quietSamples[$index - 1]; $b = $quietSamples[$index]
            $span = ([DateTime]::Parse($b.timestampUtc) - [DateTime]::Parse($a.timestampUtc)).TotalSeconds
            $privateIntegral += ($a.privateBytes + $b.privateBytes) / 2 * $span
            $workingIntegral += ($a.workingSetBytes + $b.workingSetBytes) / 2 * $span
        }
        $measurement = [ordered]@{
            wallSeconds = $seconds; cpuSeconds = $last.cpuSeconds - $first.cpuSeconds
            cpuPercentOfOneLogicalCore = 100 * ($last.cpuSeconds - $first.cpuSeconds) / $seconds
            averagePrivateBytes = [long]($privateIntegral / $seconds); peakPrivateBytes = ($quietSamples | Measure-Object privateBytes -Maximum).Maximum
            averageWorkingSetBytes = [long]($workingIntegral / $seconds); peakWorkingSetBytes = ($quietSamples | Measure-Object workingSetBytes -Maximum).Maximum
            sampleCount = $quietSamples.Count
        }
        $measurement.meetsIdleCpuTarget = $measurement.cpuPercentOfOneLogicalCore -lt 1
        if ($null -eq $failure -and -not $measurement.meetsIdleCpuTarget) { $failure = 'Closed quiet CPU exceeded the target of less than one percent of one logical core.' }
    }
    Write-Json 'samples.json' @($samples.ToArray())
    Write-Json 'summary.json' ([ordered]@{
        passed = ($null -eq $failure); error = $failure; executable = $Executable; sourceExecutable = $sourceExecutable
        executableSha256 = (Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash
        backendSha256 = (Get-FileHash -LiteralPath (Join-Path $packageDirectory 'mission-control-backend.exe') -Algorithm SHA256).Hash
        dataDirectory = $dataDirectory; rootPid = $(if ($root) { $root.Id } else { $null })
        quietMeasurement = $measurement; checks = @($checks.ToArray()); trackedProcesses = @($tracked.Values)
        closedWebViewExitVerified = $closedWebViews; allAppProcessExitsVerified = $allExited
        hardware = $hardware
        methodology = @(
            'Normal release shell plus its collector and all discovered owned WebView2 descendants; no app-side diagnostics or benchmark timer.',
            'CPU percent is CPU seconds / elapsed wall seconds * 100 against one logical core, without dividing by processor count.',
            'OS process counters are sampled about once per second; parent identities are discovered every five seconds. PID/start-time pairs prevent PID reuse attribution.',
            'Quiet measurement begins after all owned WebViews exit and a further ten-second settling period. The same shell and collector must remain alive throughout.',
            'Private bytes and working set are separate whole-app sums. Memory averages are time weighted; peaks are sampled peaks.',
            'External PowerShell, Node/CDP driver, and CIM sampler overhead are excluded. Initial CDP readiness and lifecycle automation are outside the quiet measurement.',
            'Very short-lived children between process inventories may be missed. Exited processes retain their last observed CPU counter, a lower bound for lifecycle transitions.',
            'Fresh workspace with synthetic sample history only. Existing app processes, credentials, and workspace directories are not used or deleted.'
        )
    })
    $env:MC_CDP_URL = $oldCdp; $env:MC_NATIVE_TEST_DATA_ROOT = $oldData
    if ($root) { $root.Dispose() }
}
if ($failure) { throw "$failure Evidence: $(Join-Path $artifactDirectory 'summary.json')" }
Write-Output ('Tauri lifecycle passed. Quiet CPU {0:N3}% of one core; private {1:N1} MiB average; working set {2:N1} MiB average.' -f $measurement.cpuPercentOfOneLogicalCore, ($measurement.averagePrivateBytes / 1MB), ($measurement.averageWorkingSetBytes / 1MB))
Write-Output "Summary: $(Join-Path $artifactDirectory 'summary.json')"

