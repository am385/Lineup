[CmdletBinding()]
param(
    [string]$InstallDirectory,

    [ValidateSet('X64', 'Arm64')]
    [string]$Architecture
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $InstallDirectory = Join-Path $PSScriptRoot '..\.ffmpeg'
}

$version = '8.1.3-2'
if ([string]::IsNullOrWhiteSpace($Architecture)) {
    $Architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
}

switch ($Architecture) {
    'X64' {
        $assetName = "jellyfin-ffmpeg_${version}_portable_win64-clang-gpl.zip"
        $expectedHash = 'b65dd5011a80b5abec08bd559489cf188217238c8e38b5bf553504c974f5694d'
    }
    'Arm64' {
        $assetName = "jellyfin-ffmpeg_${version}_portable_winarm64-clang-gpl.zip"
        $expectedHash = 'e1e0373cb989440590ce2d124f40988e3165ed338971ae14a97476cd61284785'
    }
    default {
        throw "Jellyfin FFmpeg does not publish a supported portable Windows archive for $Architecture."
    }
}

$downloadUrl = "https://github.com/jellyfin/jellyfin-ffmpeg/releases/download/v$version/$assetName"
$archivePath = Join-Path ([System.IO.Path]::GetTempPath()) "$([Guid]::NewGuid().ToString('N'))-$assetName"
$resolvedInstallDirectory = [System.IO.Path]::GetFullPath($InstallDirectory)

try {
    Write-Host "Downloading Jellyfin FFmpeg $version for $Architecture..."
    Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath -UseBasicParsing

    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "Checksum mismatch for $assetName. Expected $expectedHash but received $actualHash."
    }

    New-Item -ItemType Directory -Path $resolvedInstallDirectory -Force | Out-Null
    Expand-Archive -LiteralPath $archivePath -DestinationPath $resolvedInstallDirectory -Force

    $ffmpegPath = Join-Path $resolvedInstallDirectory 'ffmpeg.exe'
    $ffprobePath = Join-Path $resolvedInstallDirectory 'ffprobe.exe'
    if (-not (Test-Path -LiteralPath $ffmpegPath) -or -not (Test-Path -LiteralPath $ffprobePath)) {
        throw 'The Jellyfin FFmpeg archive did not contain ffmpeg.exe and ffprobe.exe.'
    }

    Write-Host "Installed Jellyfin FFmpeg to $resolvedInstallDirectory"
    Write-Host 'Restart Lineup or Visual Studio before streaming.'
}
finally {
    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }
}
