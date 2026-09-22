[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier,

    [string]$Version,

    [string]$SourceRevisionId,

    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$hasVersionOverride = ![string]::IsNullOrWhiteSpace($Version)
if ([string]::IsNullOrWhiteSpace($Version)) {
    $buildPropertiesPath = Join-Path $repositoryRoot 'Directory.Build.props'
    [xml]$buildProperties = Get-Content -LiteralPath $buildPropertiesPath
    $Version = [string]$buildProperties.Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*)?$') {
    throw "Version must use semantic version format, for example 1.2.3 or 1.2.3-dev.4+abc1234. Received '$Version'."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts\windows'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

if ([string]::IsNullOrWhiteSpace($SourceRevisionId)) {
    $SourceRevisionId = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($SourceRevisionId)) {
        throw 'Unable to resolve the source revision. Pass -SourceRevisionId explicitly.'
    }
}

$architecture = switch ($RuntimeIdentifier) {
    'win-x64' { 'X64' }
    'win-arm64' { 'Arm64' }
}
$packageName = "Lineup-$Version-$RuntimeIdentifier"
$stagingDirectory = Join-Path $OutputDirectory 'staging'
$packageDirectory = Join-Path $stagingDirectory $packageName
$applicationDirectory = Join-Path $packageDirectory 'app'
$archivePath = Join-Path $OutputDirectory "$packageName.zip"

foreach ($path in @($packageDirectory, $archivePath)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
New-Item -ItemType Directory -Force -Path $packageDirectory | Out-Null

$publishArguments = @(
    'publish',
    (Join-Path $repositoryRoot 'src\Lineup.Web\Lineup.Web.csproj'),
    '--configuration',
    'Release',
    '--runtime',
    $RuntimeIdentifier,
    '--self-contained',
    'true',
    '--output',
    $applicationDirectory,
    "-p:Version=$Version",
    "-p:SourceRevisionId=$SourceRevisionId"
)
if ($hasVersionOverride) {
    $publishArguments += '-p:IncludeSourceRevisionInInformationalVersion=false'
}

& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for $RuntimeIdentifier."
}

& (Join-Path $repositoryRoot 'scripts\Install-JellyfinFfmpeg.ps1') `
    -InstallDirectory (Join-Path $applicationDirectory '.ffmpeg') `
    -Architecture $architecture

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'packaging\windows\Start-Lineup.cmd') -Destination $packageDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'packaging\windows\THIRD-PARTY-NOTICES.txt') -Destination $packageDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $packageDirectory 'LICENSE.txt')

$readmeTemplate = Get-Content -LiteralPath (Join-Path $repositoryRoot 'packaging\windows\WINDOWS-README.txt') -Raw
$readme = $readmeTemplate.Replace('__VERSION__', $Version).Replace('__ARCHITECTURE__', $RuntimeIdentifier)
[System.IO.File]::WriteAllText((Join-Path $packageDirectory 'README-WINDOWS.txt'), $readme)

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $packageDirectory,
    $archivePath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $true)
Write-Host "Created $archivePath"
Write-Output $archivePath
