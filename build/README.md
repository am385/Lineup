# Building and packaging Lineup

Source prerequisites, offline browser assets and the shared isolated-worker packaging pipeline. Development commands run from the repository root unless stated otherwise.

- [Building from source](#building-from-source)
- [Development prerequisites](#development-prerequisites)
- [Isolated-worker packaging](#isolated-worker-packaging)
- [Offline browser assets](#offline-browser-assets)
- [Browser-asset build requirements](#browser-asset-build-requirements)

## Building from source

The following instructions are for developers who want to build or modify Lineup. Windows users who only want to run the published application should use [Installing Lineup on Windows](../README.md#installing-lineup-on-windows).
All project build outputs and intermediate files are centralized beneath the repository-level `out` directory.

## Development prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An HDHomeRun device on your local network for live streaming or hardware integration checks
- [Jellyfin FFmpeg](https://github.com/jellyfin/jellyfin-ffmpeg) (required for live TV transcoding and AC-4 decoding; included in the Docker image and available as `ffmpeg` on `PATH`)

On Windows, install the pinned portable Jellyfin FFmpeg build for local development:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-JellyfinFfmpeg.ps1
```

The script verifies the official release checksum and installs `ffmpeg.exe` and `ffprobe.exe` into the ignored repo-root `.ffmpeg` directory. Source builds add that directory to the Lineup process `PATH` automatically. Packaged Windows builds continue to use the `.ffmpeg` directory bundled beside the application. Restart the app after installation.

AC-4 audio transcodes use an explicit 48 kHz output rate in both MPEG-TS stages and Web Player CMAF output. AC-4 decoding
can legitimately expose intermediate rates such as 46,034 Hz; explicit resampling avoids automatic 44.1 kHz selection.
Source metadata retains the reported rate; encoded output metadata reports 48 kHz.

**AC-4 timestamp compensation (experimental)** is a separate, default-off Audio Transcoding setting, available globally
and as an independent per-device override for either stage. When enabled for an AC-4 transcode, it adds per-output
`aresample=async=1`, allowing timestamp-aware sample filling/trimming. A nonstandard source rate alone does not establish
timestamp disagreement or justify enabling compensation. Copy mode and unrelated audio codecs remain unchanged.
This only affects Lineup's own transcodes, not downstream Jellyfin conversion or FFmpeg's AC-4 probe/decoder.
Save and start a new playback session when comparing on/off behavior; existing playback is not rewritten.

## Isolated-worker packaging

Normal Web and TUI builds install the shipped isolated virtual-output workers into each host's `virtual-output-workers` directory, including Visual Studio builds and debugger launches.
Packaging uses a shared, locked cache under `out\virtual-output-worker-packages` and separate worker compilation outputs.
`build\Lineup.VirtualOutputWorkers.props` is the authoritative worker inventory for MSBuild and the existing .NET packager. Docker uses normal Web publishing; Windows packaging invokes that same packager through its PowerShell wrapper. Manifests, executable selection, hashes and protocol metadata are generated in one place.
Unchanged source/build inputs reuse verified packages; missing or modified installed files are repaired on the next build without rewriting unchanged files.
A normal `dotnet publish` of either host includes the same worker packages in the published distribution.
Publishing packages workers once, then copies only the verified `packaging.files` inventory through the standard publish-file pipeline, including `--no-build` publishes. Stale or unverified output files are not included. Worker apphosts and dependencies stay outside a single-file host bundle.
Design-time builds do not package workers. Set the MSBuild property `SkipVirtualOutputWorkerPublish=true` only when a specialized packaging flow, such as the Windows release build, supplies those packages separately.
Visual Studio's up-to-date check tracks worker source inputs, manifests, and primary runtime files so missing packages or worker changes request a real build.

See the [testing guide](../tests/README.md#running-tests) for real-SDK publishing invariants, portable/RID deployment properties and installed-worker validation.

## Offline browser assets

The browser player, controls, icons, and fonts ship with Lineup; a fresh browser does not need Internet access to play a
local stream or run browser compatibility tests. Guide providers and remote sources may still require Internet access.
Shaka Player remains pinned to **5.2.11** and Bootstrap Icons to **1.11.3**. Build and publish restore these libraries,
fonts, and their redistribution licenses/notices into the ignored `out/browser-assets/<manifest-sha256>` cache.
The Web SDK links only the manifest's files into `wwwroot/lib` in the published application; none of these downloaded
files is stored under the source tree or committed to Git. The build tool requires only the .NET SDK, including in Docker.
`build/browser-assets.json` pins source URLs, SHA-256 checksums, and the stylesheet's local-font rewrite and modification notice.
Every build verifies cached bytes and downloads only missing or corrupt assets, validating both original and transformed files
before atomic replacement. A complete cache needs no asset-download network access; an unavailable download or checksum
mismatch fails the build explicitly. CI caches the assets by OS and manifest hash. Dependency updates require reviewing
the manifest, licenses, and playback/compatibility behavior.

To check the cache without downloading assets, run
`dotnet run --project tools/Lineup.Tools.BrowserAssets -- build/browser-assets.json out/browser-assets --verify`.
Omit `--verify` to restore it explicitly; ordinary Web builds and publishes perform that restoration automatically.

## Browser-asset build requirements

Web builds also restore hash-pinned browser dependencies into `out/browser-assets`, reusing verified cached files offline.
An empty or corrupt cache requires network access. The .NET-only restore tool and `build/browser-assets.json` are checked in;
downloaded libraries, fonts, licenses, and notices are not. Standard publishing includes these assets for offline playback.
Browser-asset targets must evaluate with both `dotnet` MSBuild and Visual Studio's .NET Framework MSBuild; use
MSBuild's cross-runtime path helpers rather than framework-specific overloads. Project-load evaluation does not download assets.
Cached content must register its physical content root so development/F5 serves the same files as publishing.
Browser-asset regressions exercise original and fingerprinted asset URLs over HTTP in both Debug and Release development hosts.

## Related documentation

[Project overview](../README.md) | [Contributor standards](../CONTRIBUTING.md)
