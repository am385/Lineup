#requires -Version 5.1
<#
.SYNOPSIS
Starts a temporary fixed-purpose WPR collector after one explicit Windows UAC approval.
.DESCRIPTION
Run unelevated after building Release. Does not start a recording, application or hardware workload.
The collector expires after ten idle minutes or ninety minutes total, plus bounded save/export drain time.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') {
    throw 'The performance collector requires Windows.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this launcher unelevated. Only the fixed-purpose collector should receive the UAC approval.'
    }
    $userSid = $identity.User.Value
} finally {
    $identity.Dispose()
}
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tool = Join-Path $repository 'out\bin\Lineup.Tools.PerformanceCollector\release\Lineup.Tools.PerformanceCollector.exe'
if (!(Test-Path -LiteralPath $tool -PathType Leaf)) {
    throw 'Build Lineup.slnx in Release before starting the collector.'
}
$sessionId = [Guid]::NewGuid().ToString('N')
$spool = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) ('Lineup.Performance.' + $sessionId)
Write-Host 'Approve only the temporary Lineup.Tools.PerformanceCollector executable in the Windows UAC prompt.'
$process = Start-Process -FilePath $tool -Verb RunAs -ArgumentList @('serve', '--session', $sessionId, '--owner-sid', $userSid) -PassThru
try {
    $ready = Join-Path $spool 'session.json'
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (!(Test-Path -LiteralPath $ready -PathType Leaf)) {
        if ($process.HasExited) {
            throw 'Collector exited before readiness. Inspect its console; no recording was requested.'
        }
        if ([DateTime]::UtcNow -ge $deadline) {
            throw "Collector did not become ready in time. Session $sessionId may still be starting; inspect its console before retrying."
        }
        Start-Sleep -Milliseconds 100
    }
    $session = Get-Content -LiteralPath $ready -Raw | ConvertFrom-Json
    if ($session.sessionId -ne $sessionId -or $session.processId -ne $process.Id) {
        throw 'Collector readiness identity does not match the explicitly approved process.'
    }
    $status = @(& $tool status --session $sessionId)
    if ($LASTEXITCODE -ne 0) {
        throw 'Collector became ready but its authenticated local health check failed. Inspect its console.'
    }
    $health = ($status -join [Environment]::NewLine) | ConvertFrom-Json
    if (!$health.success -or $health.state -ne 'ready') {
        throw 'Collector did not report ready; no recording was requested.'
    }
    Write-Host "Collector ready. Session: $sessionId"
    $descriptor = [pscustomobject]@{
        SessionId = $sessionId
        ProcessId = $process.Id
        SpoolDirectory = $spool
    }
    $descriptorDirectory = Join-Path $repository 'out\performance-collector'
    [void][IO.Directory]::CreateDirectory($descriptorDirectory)
    $descriptor | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $descriptorDirectory ($sessionId + '.json')) -Encoding UTF8
    $descriptor
} finally {
    $process.Dispose()
}
