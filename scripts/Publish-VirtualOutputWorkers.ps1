[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [string]$RuntimeIdentifier,

    [switch]$SelfContained,

    [string]$Version,

    [string]$SourceRevisionId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props')
    $Version = [string]$buildProperties.Project.PropertyGroup.Version
}
$workers = @(
    @{
        Id = 'lineup-link'
        AdapterKind = 'lineup-link'
        Project = 'src\Plugins\LineupLink\Lineup.LineupLink.VirtualOutputHost\Lineup.LineupLink.VirtualOutputHost.csproj'
        Assembly = 'Lineup.LineupLink.VirtualOutputHost'
    },
    @{
        Id = 'hdhomerun'
        AdapterKind = 'hdhomerun'
        Project = 'src\Plugins\SiliconDust\Lineup.SiliconDust.HDHomeRun.VirtualOutputHost\Lineup.SiliconDust.HDHomeRun.VirtualOutputHost.csproj'
        Assembly = 'Lineup.SiliconDust.HDHomeRun.VirtualOutputHost'
    },
    @{
        Id = 'hdhomerun-discovery'
        AdapterKind = 'hdhomerun-discovery'
        Project = 'src\Plugins\SiliconDust\Lineup.SiliconDust.HDHomeRun.DiscoveryHost\Lineup.SiliconDust.HDHomeRun.DiscoveryHost.csproj'
        Assembly = 'Lineup.SiliconDust.HDHomeRun.DiscoveryHost'
    },
    @{
        Id = 'web-player'
        AdapterKind = 'web-player'
        Project = 'src\Plugins\WebPlayer\Lineup.WebPlayer.VirtualOutputHost\Lineup.WebPlayer.VirtualOutputHost.csproj'
        Assembly = 'Lineup.WebPlayer.VirtualOutputHost'
    }
)

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
foreach ($worker in $workers) {
    $workerOutput = Join-Path $outputRoot $worker.Id
    if (Test-Path -LiteralPath $workerOutput) {
        Remove-Item -LiteralPath $workerOutput -Recurse -Force
    }

    $publishArguments = @(
        'publish',
        (Join-Path $repositoryRoot $worker.Project),
        '--configuration',
        'Release',
        '--self-contained',
        $SelfContained.ToString().ToLowerInvariant(),
        '--output',
        $workerOutput
    )
    if (![string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
        $publishArguments += @('--runtime', $RuntimeIdentifier)
    }
    if (![string]::IsNullOrWhiteSpace($Version)) {
        $publishArguments += "-p:Version=$Version"
        $publishArguments += '-p:IncludeSourceRevisionInInformationalVersion=false'
    }
    if (![string]::IsNullOrWhiteSpace($SourceRevisionId)) {
        $publishArguments += "-p:SourceRevisionId=$SourceRevisionId"
    }

    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for virtual-output worker '$($worker.Id)'."
    }

    $executableName = if ($SelfContained -and $RuntimeIdentifier -like 'win-*') {
        "$($worker.Assembly).exe"
    } elseif ($SelfContained) {
        $worker.Assembly
    } else {
        "$($worker.Assembly).dll"
    }
    $executablePath = Join-Path $workerOutput $executableName
    if (!(Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        throw "Published virtual-output executable '$executablePath' was not found."
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        workerId = $worker.Id
        version = $Version
        publisher = 'Lineup'
        executablePath = $executableName
        executableSha256 = (Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash
        adapterKinds = @($worker.AdapterKind)
        platforms = @()
        minimumProtocolVersion = 1
        maximumProtocolVersion = 1
        minimumLineupVersion = $Version
        settingsDescriptor = [ordered]@{
            adapterKind = $worker.AdapterKind
            fields = @()
        }
    }
    $manifestPath = Join-Path $workerOutput "$($worker.Id).virtual-output.json"
    $manifestJson = $manifest | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($manifestPath, $manifestJson, [System.Text.UTF8Encoding]::new($false))
}
