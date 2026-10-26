[CmdletBinding()]
param(
    [string]$CliDll = '',
    [string]$ArtifactRoot = '',
    [ValidateRange(5, 120)][int]$StepTimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scriptDirectory = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) { $scriptDirectory = [System.IO.Path]::GetDirectoryName($MyInvocation.MyCommand.Path) }
if ([string]::IsNullOrWhiteSpace($CliDll)) { $CliDll = Join-Path $scriptDirectory '../src/MissionControl.Cli/bin/Release/net10.0/MissionControl.Cli.dll' }
if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) { $ArtifactRoot = Join-Path $scriptDirectory '../artifacts' }

function Write-JsonFile([string]$Path, $Value) {
    [System.IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 60), [System.Text.UTF8Encoding]::new($false))
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Quote-Path([string]$Value) {
    if ($Value.Contains('"')) { throw 'A native argument cannot contain a quotation mark.' }
    return '"' + $Value.TrimEnd('\') + '"'
}

$CliDll = [System.IO.Path]::GetFullPath($CliDll)
Assert-True (Test-Path -LiteralPath $CliDll -PathType Leaf) "Release CLI is missing: $CliDll. Build src/MissionControl.Cli -c Release first."
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$artifactDirectory = Join-Path ([System.IO.Path]::GetFullPath($ArtifactRoot)) ('replay-restart-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
Assert-True (-not (Test-Path -LiteralPath $artifactDirectory)) "Refusing to reuse an existing directory: $artifactDirectory"
$null = New-Item -ItemType Directory -Path $artifactDirectory
$dataDirectory = Join-Path $artifactDirectory 'data'
$steps = [System.Collections.Generic.List[object]]::new()
$failure = $null

function Invoke-RestartStep([string]$Name, [string]$Command) {
    # Own the native process handle from Start through ExitCode. Start-Process
    # -PassThru can lose the exit code of a very short-lived process on Windows PS.
    $stdout = Join-Path $artifactDirectory ($Name + '.stdout.json')
    $stderr = Join-Path $artifactDirectory ($Name + '.stderr.txt')
    $arguments = @((Quote-Path $CliDll), $Command, '--data-dir', (Quote-Path $dataDirectory))
    $startedUtc = [DateTime]::UtcNow
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $dotnet
    $startInfo.Arguments = $arguments -join ' '
    $startInfo.WorkingDirectory = [System.IO.Path]::GetDirectoryName($CliDll)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $child = [System.Diagnostics.Process]::new()
    $child.StartInfo = $startInfo
    $stdoutTask = $null; $stderrTask = $null
    try {
        Assert-True ($child.Start()) "Could not start the fresh CLI process for '$Name'."
        $childStartTicks = $child.StartTime.ToUniversalTime().Ticks
        # Drain both pipes concurrently so a full output pipe cannot deadlock exit.
        $stdoutTask = $child.StandardOutput.ReadToEndAsync()
        $stderrTask = $child.StandardError.ReadToEndAsync()
        $timer = [System.Diagnostics.Stopwatch]::StartNew()
        $nextProgress = 30.0
        while (-not $child.WaitForExit(100)) {
            if ($timer.Elapsed.TotalSeconds -gt $StepTimeoutSeconds) {
                $child.Kill()
                if (-not $child.WaitForExit(10000)) { throw "CLI step '$Name' exceeded its timeout and did not exit after Kill." }
                throw "CLI step '$Name' exceeded $StepTimeoutSeconds seconds; the dedicated test process was stopped."
            }
            if ($timer.Elapsed.TotalSeconds -ge $nextProgress) {
                Write-Host "Restart replay: $Name is still running ($([Math]::Round($timer.Elapsed.TotalSeconds)) seconds)."
                $nextProgress += 30
            }
        }
        $child.WaitForExit()
        $exitCode = $child.ExitCode
        $raw = $stdoutTask.GetAwaiter().GetResult()
        $errorText = $stderrTask.GetAwaiter().GetResult()
        [System.IO.File]::WriteAllText($stdout, $raw, [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText($stderr, $errorText, [System.Text.UTF8Encoding]::new($false))
        $endedUtc = [DateTime]::UtcNow
        $sameProcessStillAlive = $false
        $candidate = Get-Process -Id $child.Id -ErrorAction SilentlyContinue
        if ($null -ne $candidate) {
            try { $sameProcessStillAlive = $candidate.StartTime.ToUniversalTime().Ticks -eq $childStartTicks -and -not $candidate.HasExited }
            catch [System.InvalidOperationException] { }
            finally { $candidate.Dispose() }
        }
        Assert-True (-not $sameProcessStillAlive) "Step '$Name' did not completely exit before the next restart."
        $step = [pscustomobject]@{
            name = $Name; command = $Command; pid = $child.Id; processStartUtcTicks = $childStartTicks
            startedUtc = $startedUtc.ToString('O'); exitedUtc = $endedUtc.ToString('O')
            exitCode = $exitCode; completeProcessExitVerified = $true
            stdoutFile = [System.IO.Path]::GetFileName($stdout); stderrFile = [System.IO.Path]::GetFileName($stderr)
        }
        $steps.Add($step)
        Assert-True ($null -ne $exitCode) "CLI step '$Name' did not expose an exit code despite owning its process handle. See $stderr."
        Assert-True ($exitCode -eq 0) "CLI step '$Name' exited $exitCode. See $stderr."
        Assert-True (-not [string]::IsNullOrWhiteSpace($raw)) "CLI step '$Name' returned no JSON."
        try { $response = $raw | ConvertFrom-Json }
        catch { throw "CLI step '$Name' did not return one JSON document. See $stdout." }
        return $response
    }
    finally {
        if ($null -ne $stdoutTask -and $stdoutTask.IsCompleted -and -not $stdoutTask.IsFaulted -and -not (Test-Path -LiteralPath $stdout)) { [System.IO.File]::WriteAllText($stdout, $stdoutTask.GetAwaiter().GetResult(), [System.Text.UTF8Encoding]::new($false)) }
        if ($null -ne $stderrTask -and $stderrTask.IsCompleted -and -not $stderrTask.IsFaulted -and -not (Test-Path -LiteralPath $stderr)) { [System.IO.File]::WriteAllText($stderr, $stderrTask.GetAwaiter().GetResult(), [System.Text.UTF8Encoding]::new($false)) }
        $child.Dispose()
    }
}

try {
    Write-Output "Restart replay artifacts: $artifactDirectory"
    $initial = Invoke-RestartStep '01-initial' 'snapshot'
    Assert-True ($initial.mode -eq 'demo' -and $initial.totalRecords -eq 30) 'Fresh demo must contain exactly 30 records.'
    $null = Invoke-RestartStep '02-pause' 'pause'
    $null = Invoke-RestartStep '03-append-while-stopped' 'append'
    $paused = Invoke-RestartStep '04-paused-restart' 'snapshot'
    Assert-True ($paused.monitoring -eq 'paused') 'Pause must persist across a complete process restart.'
    Assert-True ($paused.totalRecords -eq 30) 'Restarting while paused must not ingest the appended events.'
    $resumed = Invoke-RestartStep '05-resume-after-restart' 'resume'
    Assert-True ($resumed.totalRecords -eq 34) 'Resume must recover 34 unique records, including offline updates.'
    $resumedAgain = Invoke-RestartStep '06-resume-again-after-restart' 'resume'
    Assert-True ($resumedAgain.totalRecords -eq 34) 'A second resume after another process restart must remain at 34 records.'

    $verifiedStates = @()
    Assert-True (@($resumedAgain.records | Where-Object { $_.sourceKind -notin @('claude','codex') }).Count -eq 0) 'Only local coding history should be present.'
    $processIdentities = @($steps | ForEach-Object { '{0}:{1}' -f $_.pid, $_.processStartUtcTicks } | Select-Object -Unique)
    Assert-True ($processIdentities.Count -eq 6) 'All six replay steps must use distinct OS process identities.'
    $result = [ordered]@{
        passed = $true; scenario = 'offline-demo-replay-with-complete-process-restarts'
        cliDll = $CliDll; dataDirectory = $dataDirectory; noExistingDataDeleted = $true
        counts = @(30, 30, 34, 34); initialCount = $initial.totalRecords
        pausedRestartCount = $paused.totalRecords; resumedCount = $resumed.totalRecords; secondResumeCount = $resumedAgain.totalRecords
        pausedRestartMonitoring = $paused.monitoring; finalMonitoring = $resumedAgain.monitoring
        completeProcessRestartBetweenEveryStep = $true; steps = @($steps.ToArray()); finalStates = @($verifiedStates)
        fixtureNotice = 'Synthetic demo only. No external account or API was used.'
    }
    Write-JsonFile (Join-Path $artifactDirectory 'summary.json') $result
    Write-Output 'Restart replay passed: 30 initial -> 30 after paused restart -> 34 recovered -> 34 after second restarted resume.'
    Write-Output "Summary: $(Join-Path $artifactDirectory 'summary.json')"
}
catch {
    $failure = $_.Exception.Message
    Write-JsonFile (Join-Path $artifactDirectory 'summary.json') ([ordered]@{ passed = $false; scenario = 'offline-demo-replay-with-complete-process-restarts'; error = $failure; dataDirectory = $dataDirectory; steps = @($steps.ToArray()) })
    throw
}
