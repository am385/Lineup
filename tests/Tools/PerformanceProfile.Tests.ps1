#requires -Version 5.1
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\scripts\Profile-Lineup.ps1')

function New-ProfileTestProcess {
    param([int]$Id, [int]$ParentId, [int]$Started)

    return [pscustomobject]@{
        ProcessId = $Id
        ParentProcessId = $ParentId
        CreationDate = ([DateTime]'2026-01-01T00:00:00Z').AddSeconds($Started)
    }
}

function Assert-ProfileProcessIds {
    param([object[]]$Actual, [int[]]$Expected)

    $ids = @($Actual | ForEach-Object { $_.ProcessId } | Sort-Object)
    if (($ids -join ',') -cne (($Expected | Sort-Object) -join ',')) {
        throw "Expected process IDs $($Expected -join ','); received $($ids -join ',')."
    }
}

# Arrange
$root = New-ProfileTestProcess 100 1 1
$external = New-ProfileTestProcess 200 1 2
$child = New-ProfileTestProcess 101 100 3
$leaf = New-ProfileTestProcess 102 101 4
$unrelated = New-ProfileTestProcess 300 1 3
$known = New-Object 'System.Collections.Generic.HashSet[string]'
[void]$known.Add((Get-LineupProfileProcessKey $root))
[void]$known.Add((Get-LineupProfileProcessKey $external))

# Act
$selected = @(Select-LineupProfileProcess @($leaf, $child, $unrelated, $external, $root) $known)

# Assert
Assert-ProfileProcessIds $selected @(100, 101, 102, 200)

# Arrange
$reusedRoot = New-ProfileTestProcess 100 1 8
$unrelatedChild = New-ProfileTestProcess 103 100 9

# Act
$selected = @(Select-LineupProfileProcess @($reusedRoot, $unrelatedChild, $leaf) $known)

# Assert
Assert-ProfileProcessIds $selected @(102)

# Arrange
$newKnown = New-Object 'System.Collections.Generic.HashSet[string]'
[void]$newKnown.Add((Get-LineupProfileProcessKey $reusedRoot))
$olderOrphan = New-ProfileTestProcess 104 100 5

# Act
$selected = @(Select-LineupProfileProcess @($olderOrphan, $unrelatedChild, $reusedRoot) $newKnown)

# Assert
Assert-ProfileProcessIds $selected @(100, 103)

# Arrange
$missingCreation = [pscustomobject]@{ ProcessId = 105; ParentProcessId = 100; CreationDate = $null }

# Act
$selected = @(Select-LineupProfileProcess @($missingCreation) $newKnown)
$empty = @(Select-LineupProfileProcess @() $newKnown)

# Assert
Assert-ProfileProcessIds $selected @()
Assert-ProfileProcessIds $empty @()

& {
    $profileScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\scripts\Profile-Lineup.ps1'))
    $testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('lineup-profile-tests-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($testDirectory)
    $testContext = [pscustomobject]@{
        Mode = ''
        Queries = 0
        Calls = New-Object 'System.Collections.Generic.List[object]'
        Root = [pscustomobject]@{
            ProcessId = 100
            ParentProcessId = 1
            Name = 'owned-test.exe'
            CreationDate = [DateTime]::UtcNow.AddSeconds(-1)
            KernelModeTime = 10000000
            UserModeTime = 10000000
            WorkingSetSize = 4096
            ReadTransferCount = 0
            WriteTransferCount = 0
        }
    }

    function New-Object {
        param([string]$TypeName, [object[]]$ArgumentList)

        if ($TypeName -eq 'Security.Principal.WindowsPrincipal') {
            $principal = [pscustomobject]@{ Elevated = $testContext.Mode -ne 'no-elevation' }
            $principal | Add-Member -MemberType ScriptMethod -Name IsInRole -Value { return $this.Elevated }
            return $principal
        }
        return Microsoft.PowerShell.Utility\New-Object @PSBoundParameters
    }

    function Get-Command {
        param([string]$Name, [string]$CommandType, [string]$ErrorAction)

        if ($Name -ne 'wpr.exe') {
            throw "Unexpected command lookup: $Name"
        }
        return [pscustomobject]@{ Source = 'Invoke-ProfileTestWpr' }
    }

    function Get-CimInstance {
        param([string]$ClassName, [string[]]$Property, [int]$OperationTimeoutSec)

        if ($ClassName -eq 'Win32_VideoController') {
            return [pscustomobject]@{ Name = 'Test GPU'; DriverVersion = 'test' }
        }
        if ($ClassName -eq 'Win32_ComputerSystem') {
            return [pscustomobject]@{ NumberOfLogicalProcessors = 8 }
        }
        $testContext.Queries++
        if ($testContext.Mode -eq 'census-failure' -and $testContext.Queries -gt 1) {
            throw 'Test census unavailable.'
        }
        return $testContext.Root
    }

    function Invoke-ProfileTestWpr {
        $call = @($args)
        [void]$testContext.Calls.Add($call)
        $global:LASTEXITCODE = 0
        if ($call[0] -eq '-start' -and $testContext.Mode -eq 'start-failure') {
            $global:LASTEXITCODE = 23
        }
        if ($call[0] -eq '-stop') {
            if ($testContext.Mode -eq 'stop-failure') {
                $global:LASTEXITCODE = 24
            }
            elseif ($testContext.Mode -ne 'missing-etl') {
                $contents = 'Test fixture, not an ETW recording.'
                if ($testContext.Mode -eq 'empty-etl') {
                    $contents = ''
                }
                [IO.File]::WriteAllText($call[1], $contents)
            }
        }
        return 'Stub WPR output'
    }

    try {
        foreach ($mode in @('success', 'missing-cpu', 'start-failure', 'census-failure', 'stop-failure', 'missing-etl', 'empty-etl', 'no-elevation', 'missing-root')) {
            # Arrange
            $testContext.Mode = $mode
            $testContext.Queries = 0
            $testContext.Calls.Clear()
            $testContext.Root.KernelModeTime = 10000000
            if ($mode -eq 'missing-cpu') {
                $testContext.Root.KernelModeTime = $null
            }
            $outputDirectory = Join-Path $testDirectory $mode
            $rootId = 100
            if ($mode -eq 'missing-root') {
                $rootId = 999
            }
            $failure = $null

            # Act
            try {
                & $profileScript -RootProcessId $rootId -Scenario test -DurationSeconds 1 -OutputDirectory $outputDirectory -IncludeGpu -IncludeDotNet -IncludeIo
            }
            catch {
                $failure = $_
            }

            # Assert
            if (($null -eq $failure) -ne ($mode -in @('success', 'missing-cpu'))) {
                throw "Unexpected result for $mode`: $failure"
            }
            if ($mode -in @('no-elevation', 'missing-root')) {
                if ($testContext.Calls.Count -ne 0 -or (Test-Path -LiteralPath $outputDirectory)) {
                    throw "Preflight failure $mode created a capture or called WPR."
                }
                continue
            }
            $manifest = Get-Content -LiteralPath (Join-Path $outputDirectory 'capture.json') -Raw | ConvertFrom-Json
            $starts = @($testContext.Calls | Where-Object { $_[0] -eq '-start' })
            $stops = @($testContext.Calls | Where-Object { $_[0] -eq '-stop' })
            if ($starts.Count -ne 1 -or $starts[0][-2] -ne '-instancename' -or $starts[0][-1] -ne $manifest.wprInstance) {
                throw "Capture $mode did not start its own named instance."
            }
            if ($mode -eq 'start-failure') {
                if ($stops.Count -ne 0 -or $manifest.traceSaved -or $manifest.state -ne 'failed') {
                    throw 'A failed start must not stop another recording or claim a saved trace.'
                }
                continue
            }
            if ($stops.Count -ne 1 -or $stops[0][-2] -ne '-instancename' -or $stops[0][-1] -ne $manifest.wprInstance) {
                throw "Capture $mode did not stop only its own instance."
            }
            $expectedState = 'captured'
            if ($mode -eq 'census-failure') {
                $expectedState = 'failed'
            }
            elseif ($mode -in @('stop-failure', 'missing-etl', 'empty-etl')) {
                $expectedState = 'cleanup-failed'
            }
            if ($manifest.state -ne $expectedState -or $manifest.traceSaved -ne ($mode -in @('success', 'missing-cpu', 'census-failure'))) {
                throw "Capture $mode did not preserve its actual capture/save state."
            }
            if ($mode -in @('success', 'missing-cpu')) {
                $census = @(Import-Csv -LiteralPath (Join-Path $outputDirectory 'process-census.csv'))
                $expectedCpu = '2'
                if ($mode -eq 'missing-cpu') {
                    $expectedCpu = ''
                }
                if ($census.Count -eq 0 -or $census[0].lifetimeCpuSeconds -ne $expectedCpu) {
                    throw 'The supplementary census did not preserve cumulative CPU units.'
                }
            }
        }
    }
    finally {
        [IO.Directory]::Delete($testDirectory, $true)
    }
}

Write-Host 'Performance profile identity, ownership and failure-path regressions passed (stub WPR; no recording).'
