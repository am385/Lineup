[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$RuntimeIdentifier,

    [switch]$SelfContained,

    [string]$Version,

    [string]$SourceRevisionId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props')
    $Version = [string]$buildProperties.Project.PropertyGroup.Version
}
$packagerArguments = @(
    'run',
    '--project',
    (Join-Path $PSScriptRoot 'Lineup.VirtualOutputWorkerPackager\Lineup.VirtualOutputWorkerPackager.csproj'),
    '--configuration',
    'Release',
    '--no-launch-profile',
    '--',
    '--repository-root',
    $repositoryRoot,
    '--output',
    $OutputDirectory,
    '--configuration',
    $Configuration,
    '--version',
    $Version
)
if (![string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    $packagerArguments += @('--runtime', $RuntimeIdentifier)
}
if ($SelfContained) {
    $packagerArguments += '--self-contained'
}
if (![string]::IsNullOrWhiteSpace($SourceRevisionId)) {
    $packagerArguments += @('--source-revision', $SourceRevisionId)
}

& dotnet @packagerArguments
if ($LASTEXITCODE -ne 0) {
    throw 'Virtual-output worker packaging failed.'
}
