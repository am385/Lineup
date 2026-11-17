#requires -Version 5.1
<#
.SYNOPSIS
Records a bounded, explicitly rooted Windows performance capture without starting or stopping Lineup.
.DESCRIPTION
Run elevated for direct capture, or unelevated with -CollectorSession after starting the temporary collector.
ETW captures system-wide CPU stacks and process lifetimes, including short-lived media children.
The process CSV is only a supplementary census, not complete CPU accounting. Raw traces contain system-wide metadata.
.EXAMPLE
.\scripts\Profile-Lineup.ps1 -RootProcessId 1234,5678 -Scenario gpu-captions -DurationSeconds 120 -IncludeGpu -IncludeDotNet
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 2147483647)]
    [int[]]$RootProcessId,

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$Scenario = 'baseline',

    [ValidateRange(1, 3600)]
    [int]$DurationSeconds = 120,

    [ValidateRange(1, 60)]
    [int]$SampleIntervalSeconds = 2,

    [string]$OutputDirectory,

    [switch]$IncludeIo,

    [switch]$IncludeGpu,

    [switch]$IncludeDotNet,

    [ValidatePattern('^[a-fA-F0-9]{32}$')]
    [string]$CollectorSession
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-LineupProfileProcessKey {
    param([Parameter(Mandatory)]$Process)

    return '{0}:{1}' -f $Process.ProcessId, $Process.CreationDate.ToUniversalTime().Ticks
}

function Select-LineupProfileProcess {
    param(
        [AllowEmptyCollection()]
        [object[]]$Processes,
        [Parameter(Mandatory)]
        [System.Collections.Generic.HashSet[string]]$KnownIdentities
    )

    $byId = @{}
    $selected = @{}
    foreach ($process in $Processes) {
        if ($null -eq $process.CreationDate) {
            continue
        }
        $byId[[int]$process.ProcessId] = $process
        $key = Get-LineupProfileProcessKey $process
        if ($KnownIdentities.Contains($key)) {
            $selected[[int]$process.ProcessId] = $process
        }
    }

    do {
        $added = $false
        foreach ($process in $byId.Values) {
            $id = [int]$process.ProcessId
            $parentId = [int]$process.ParentProcessId
            if (!$selected.ContainsKey($id) -and $selected.ContainsKey($parentId) -and
                $selected[$parentId].CreationDate -le $process.CreationDate) {
                $selected[$id] = $process
                [void]$KnownIdentities.Add((Get-LineupProfileProcessKey $process))
                $added = $true
            }
        }
    } while ($added)

    return $selected.Values | Sort-Object ProcessId
}

function Invoke-LineupProfileWpr {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    $output = @(& $Executable @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "WPR failed ($LASTEXITCODE): $($output -join [Environment]::NewLine)"
    }
    return $output
}

function Write-LineupProfileJson {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Value)

    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding UTF8
}

# Dot-sourcing exposes the pure identity/selection helpers to the dependency-free regression tests.
if ($MyInvocation.InvocationName -eq '.') {
    return
}

if ($env:OS -ne 'Windows_NT') {
    throw 'Performance capture requires Windows. Use WPA on Windows to analyze its ETL output.'
}
if (!$RootProcessId -or $RootProcessId.Count -eq 0) {
    throw 'Specify -RootProcessId for the management host and any independently launched external listeners.'
}
if ($CollectorSession) {
    if ($DurationSeconds -gt 300) {
        throw 'Temporary collector observations are limited to 300 seconds.'
    }
    if ($PSBoundParameters.ContainsKey('SampleIntervalSeconds')) {
        throw 'Collector mode uses authoritative ETW lifetimes and does not produce a supplementary process census.'
    }
    $tool = Join-Path $PSScriptRoot '..\out\bin\Lineup.Tools.PerformanceCollector\release\Lineup.Tools.PerformanceCollector.exe'
    if (!(Test-Path -LiteralPath $tool -PathType Leaf)) {
        throw 'Build Lineup.slnx in Release before using the temporary collector.'
    }
    $arguments = @('capture', '--session', $CollectorSession, '--duration', [string]$DurationSeconds, '--scenario', $Scenario)
    foreach ($rootId in $RootProcessId) {
        $arguments += @('--root', [string]$rootId)
    }
    if ($IncludeGpu) { $arguments += '--gpu' }
    if ($IncludeDotNet) { $arguments += '--dotnet' }
    if ($IncludeIo) { $arguments += '--io' }
    if ($OutputDirectory) {
        $output = [IO.Path]::GetFullPath($OutputDirectory)
        $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\') + '\'
        if ($output.StartsWith($repository, [StringComparison]::OrdinalIgnoreCase) -and
            !$output.StartsWith((Join-Path $repository 'out\'), [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Repository-local exports must be beneath out.'
        }
        $arguments += @('--output', $output)
    } else {
        $arguments += @('--output', (Join-Path $PSScriptRoot (
            '..\out\performance\{0}-{1}-{2}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), $Scenario, [Guid]::NewGuid().ToString('N'))))
    }
    & $tool @arguments
    if ($LASTEXITCODE -ne 0) {
        throw 'Collector capture/export failed. Inspect the reported output and elevated collector console; do not treat partial output as a successful profile.'
    }
    return
}
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'WPR capture requires an elevated PowerShell session. No trace was started.'
}
$wpr = (Get-Command wpr.exe -CommandType Application -ErrorAction Stop).Source
$logicalProcessors = (Get-CimInstance Win32_ComputerSystem -Property NumberOfLogicalProcessors -OperationTimeoutSec 10).NumberOfLogicalProcessors
if ($null -eq $logicalProcessors -or $logicalProcessors -lt 1) {
    throw 'The machine logical-processor count is unavailable. No trace was started.'
}
$properties = @('ProcessId', 'ParentProcessId', 'Name', 'CreationDate', 'KernelModeTime', 'UserModeTime', 'WorkingSetSize', 'ReadTransferCount', 'WriteTransferCount')
$processes = @(Get-CimInstance Win32_Process -Property $properties -OperationTimeoutSec 10)
$known = New-Object 'System.Collections.Generic.HashSet[string]'
$roots = @(
    foreach ($id in ($RootProcessId | Select-Object -Unique)) {
        $root = @($processes | Where-Object { $_.ProcessId -eq $id -and $null -ne $_.CreationDate })
        if ($root.Count -ne 1) {
            throw "Root process $id is missing or its creation time is unavailable. No trace was started."
        }
        [void]$known.Add((Get-LineupProfileProcessKey $root[0]))
        [ordered]@{
            processId = $id
            startedUtc = $root[0].CreationDate.ToUniversalTime().ToString('o')
            name = $root[0].Name
        }
    }
)

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path (Join-Path $PSScriptRoot '..\out\performance') (
        '{0}-{1}-{2}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), $Scenario, [Guid]::NewGuid().ToString('N'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) {
    throw 'Choose a new output directory; capture artifacts are never overwritten.'
}
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\') + '\'
$allowedRoot = Join-Path $repositoryRoot 'out\'
if ($OutputDirectory.StartsWith($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -and
    !$OutputDirectory.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Repository-local captures must be beneath out\performance or another out subdirectory.'
}
$drive = New-Object IO.DriveInfo([IO.Path]::GetPathRoot($OutputDirectory))
if ($drive.AvailableFreeSpace -lt 2GB) {
    throw 'At least 2 GiB free space is required on the output volume. No trace was started.'
}
[void](New-Item -ItemType Directory -Path $OutputDirectory -ErrorAction Stop)
$temporaryDirectory = Join-Path $OutputDirectory 'wpr-temporary'
[void][IO.Directory]::CreateDirectory($temporaryDirectory)
$instance = 'LineupProfile-' + [Guid]::NewGuid().ToString('N')
$profiles = @('CPU')
if ($IncludeIo) {
    $profiles += @('DiskIO', 'FileIO', 'Network')
}
if ($IncludeGpu) {
    $profiles += 'GPU'
}
if ($IncludeDotNet) {
    $profiles += 'DotNET'
}
$manifest = [ordered]@{
    schema = 1
    scenario = $Scenario
    state = 'preflight'
    requestedDurationSeconds = $DurationSeconds
    sampleIntervalSeconds = $SampleIntervalSeconds
    roots = $roots
    wprInstance = $instance
    profiles = $profiles
    logicalProcessors = $logicalProcessors
    osVersion = [Environment]::OSVersion.VersionString
    gpu = @(Get-CimInstance Win32_VideoController -Property Name, DriverVersion -OperationTimeoutSec 10 | Select-Object Name, DriverVersion)
    startedUtc = $null
    measurementFinishedUtc = $null
    finishedUtc = $null
    traceSaved = $false
    failure = $null
    cleanupFailure = $null
    censusSamples = 0
    limitations = @(
        'ETL is system-wide and may contain private paths, command lines and network metadata. Keep it local.',
        'Census rows can miss short-lived children and orphan ancestry. Use ETW lifetimes for authoritative accounting.',
        'Cumulative process CPU is lifetime CPU, not CPU attributable to this capture. Do not sum census rows.',
        'Existing-process ancestry before capture and independently restarted roots may need manual WPA correlation.',
        'Inspect ETW lost events and symbol resolution in WPA before accepting results.',
        'Application builds, effective plans, viewer/producer counts and playback health require separate scenario evidence.'
    )
}
$manifestPath = Join-Path $OutputDirectory 'capture.json'
$journalPath = Join-Path $OutputDirectory 'markers.jsonl'
$censusPath = Join-Path $OutputDirectory 'process-census.csv'
$recording = $false
$stopFailure = $null
$stopwatch = New-Object Diagnostics.Stopwatch

try {
    Invoke-LineupProfileWpr $wpr @('-status') | Set-Content -LiteralPath (Join-Path $OutputDirectory 'wpr-preflight.txt') -Encoding UTF8
    $arguments = @()
    foreach ($profile in $profiles) {
        $arguments += @('-start', $profile)
    }
    $arguments += @('-filemode', '-recordtempto', $temporaryDirectory, '-instancename', $instance)
    Write-LineupProfileJson $manifestPath $manifest
    $startOutput = Invoke-LineupProfileWpr $wpr $arguments
    $recording = $true
    $startOutput | Set-Content -LiteralPath (Join-Path $OutputDirectory 'wpr-start.txt') -Encoding UTF8
    $manifest.state = 'recording'
    $manifest.startedUtc = [DateTime]::UtcNow.ToString('o')
    $stopwatch.Start()
    [ordered]@{ event = 'start'; scenario = $Scenario; utc = $manifest.startedUtc } |
        ConvertTo-Json -Compress | Add-Content -LiteralPath $journalPath -Encoding UTF8
    Write-LineupProfileJson $manifestPath $manifest
    Write-Host "Recording $Scenario to $OutputDirectory. Exercise only your approved workload; Ctrl+C saves a partial trace."

    while ($stopwatch.Elapsed.TotalSeconds -lt $DurationSeconds) {
        if ($drive.AvailableFreeSpace -lt 512MB) {
            throw 'Capture stopped because the output volume has less than 512 MiB free space.'
        }
        $observedUtc = [DateTime]::UtcNow.ToString('o')
        $current = @(Get-CimInstance Win32_Process -Property $properties -OperationTimeoutSec 10)
        $selected = @(Select-LineupProfileProcess $current $known)
        if ($selected.Count -gt 0) {
            $selected | ForEach-Object {
                $cpuSeconds = $null
                if ($null -ne $_.KernelModeTime -and $null -ne $_.UserModeTime) {
                    $cpuSeconds = (([double]$_.KernelModeTime + [double]$_.UserModeTime) / 10000000).ToString('R', [Globalization.CultureInfo]::InvariantCulture)
                }
                [pscustomobject][ordered]@{
                    observedUtc = $observedUtc
                    processId = $_.ProcessId
                    parentProcessId = $_.ParentProcessId
                    startedUtc = $_.CreationDate.ToUniversalTime().ToString('o')
                    name = $_.Name
                    lifetimeCpuSeconds = $cpuSeconds
                    workingSetBytes = $_.WorkingSetSize
                    readTransferBytes = $_.ReadTransferCount
                    writeTransferBytes = $_.WriteTransferCount
                }
            } | Export-Csv -LiteralPath $censusPath -Append -NoTypeInformation -Encoding UTF8
        }
        $manifest.censusSamples++
        $remainingMilliseconds = [Math]::Max(0, ($DurationSeconds - $stopwatch.Elapsed.TotalSeconds) * 1000)
        Start-Sleep -Milliseconds ([int][Math]::Min($SampleIntervalSeconds * 1000, $remainingMilliseconds))
    }
    $manifest.state = 'captured'
}
catch {
    $manifest.state = 'failed'
    $manifest.failure = $_.Exception.Message
    throw
}
finally {
    $stopwatch.Stop()
    $manifest.measurementFinishedUtc = [DateTime]::UtcNow.ToString('o')
    if ($recording) {
        if ($manifest.state -eq 'recording') {
            $manifest.state = 'interrupted'
        }
        try {
            Invoke-LineupProfileWpr $wpr @('-stop', (Join-Path $OutputDirectory 'system.etl'), '-instancename', $instance) |
                Set-Content -LiteralPath (Join-Path $OutputDirectory 'wpr-stop.txt') -Encoding UTF8
            $manifest.traceSaved = Test-Path -LiteralPath (Join-Path $OutputDirectory 'system.etl') -PathType Leaf
            if (!$manifest.traceSaved) {
                throw 'WPR reported success without producing system.etl.'
            }
            if ((Get-Item -LiteralPath (Join-Path $OutputDirectory 'system.etl')).Length -eq 0) {
                $manifest.traceSaved = $false
                throw 'WPR produced an empty system.etl.'
            }
        }
        catch {
            $stopFailure = $_
            $manifest.state = 'cleanup-failed'
            $manifest.cleanupFailure = $_.Exception.Message
            Write-Warning "Could not save/stop owned WPR instance $instance. Preserve $temporaryDirectory and inspect that instance; do not cancel another recording."
        }
    }
    [ordered]@{ event = 'measurement-end'; scenario = $Scenario; utc = $manifest.measurementFinishedUtc } |
        ConvertTo-Json -Compress | Add-Content -LiteralPath $journalPath -Encoding UTF8
    $manifest.finishedUtc = [DateTime]::UtcNow.ToString('o')
    [ordered]@{ event = 'finish'; scenario = $Scenario; utc = $manifest.finishedUtc; state = $manifest.state } |
        ConvertTo-Json -Compress | Add-Content -LiteralPath $journalPath -Encoding UTF8
    Write-LineupProfileJson $manifestPath $manifest
    if ($null -ne $stopFailure) {
        throw $stopFailure
    }
}

Write-Host "Saved $OutputDirectory. Analyze system.etl in WPA; census polling is not a complete CPU profile."
