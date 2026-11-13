# Lineup

**Lineup** is a .NET live-TV routing and guide platform for composing physical tuner inputs, normalized guide sources, shared streaming pipelines, and consumer-facing virtual tuner outputs.

It includes a Blazor management UI, a terminal host, multi-device HDHomeRun support, transcoding, shared stream multiplexing, Web playback, virtual HDHomeRun outputs, Lineup-to-Lineup sharing, and independently published XMLTV or JSON guide outputs.

The built-in physical adapter currently supports [HDHomeRun](https://www.silicondust.com/) tuners such as the HDHomeRun FLEX 4K. Provider-neutral Core contracts keep device, guide, streaming, and virtual-output behavior outside the Web and TUI presentation layers and allow future adapters such as generic M3U sources or other network tuners.

## Features

- **Automatic EPG fetching** — downloads SiliconDust's complete gzip-compressed XMLTV guide on the required randomized 20-28 hour schedule
- **Plugin-based guide outputs** — publishes normalized guide data atomically as global or scoped XMLTV and JSON files
- **Typed JSON guide contract** — exposes versioned `JsonGuideDocument` models, compact key-based supplemental metadata without embedded XML strings, and shared serialization helpers for downstream consumers
- **Per-channel availability** — keeps disabled channels visible in Lineup while excluding them from published guides, virtual lineups, and physical tuner streams
- **Live TV streaming** — multi-track MPEG-TS proxy and shared-CMAF HLS/DASH Watch playback with selectable audio and subtitles
- **Device diagnostics** — connectivity checks across DNS, ping, HTTP API, TCP, and UDP discovery
- **Streaming flow monitoring** — the Dashboard shows configured physical devices, processing stages, shared multiplexed producers, virtual outputs, and consumer sessions with active and warm paths highlighted
- **Application diagnostics** — live in-app structured logs, optional rolling files, and opt-in external observability targets
- **Localized Web interface** — serves first-party UI text in English or Spanish from standard .NET `.resx` resources with a persisted language preference under Regional Settings
- **Native HDHomeRun protocol** — binary protocol implementation for UDP discovery, TCP control, and channel scanning
- **Aggregated virtual HDHomeRun devices** — each virtual device can subscribe to all or selected physical tuners and publishes their combined capacity and lineup
- **Selectable output hosting** — Web Player, virtual HDHomeRun, and Lineup Link outputs can reuse the management host or run in isolated dedicated listeners
- **Lineup Link sharing** — securely shares selected channels, optional guide data, and post-output-transcode streams with other Lineup instances through independently managed access grants
- **Shared stream graph** — identical physical acquisitions and transforms are reference-counted and multiplexed across virtual devices and viewers
- **First-class guide sources** — named guide sources subscribe to all or selected compatible physical devices and provide authoritative channel bindings
- **Inherited transcoding profiles** — global physical and virtual defaults with sparse per-device overrides
- **Guide-output endpoints** — stable `/api/xmltv` and `/api/guide-outputs/{identifier}` routes serve global or scoped XMLTV/JSON documents; append `?download=false` to view a document inline instead of downloading it
- **Versioned status API** — `/api/v1/status` provides one stable guide, device, tuner, and stream snapshot for Home Assistant and other dashboards
- **Authoritative SQLite storage** — physical channels, enabled preferences, normalized EPG data, and configurable guide history are stored locally

The interactive Guide uses fixed 30-minute elapsed-time slots in the configured timezone. Spring daylight-saving gaps are skipped;
repeated fall times include their UTC offsets, including program tooltips and details. Program widths and durations use elapsed time.
Navigation stays within the selected local date, including 23- and 25-hour days. Rapid navigation and resizing cancel obsolete reads;
only the latest request may display results or end its loading indicator. Failed reads show an explicit retry action instead of old-window rows.

## Lineup 3.0 reset boundary

Lineup 3.0 is an intentional configuration and storage reset. It does not migrate persisted state created by earlier releases. On startup, Lineup requires the exact application-settings, streaming-topology, database, and Web-settings schema versions used by the running release; an older, newer, invalid, or unversioned artifact enters the explicit factory-reset-required state.

Record the device addresses, guide choices, output subscriptions, and deployment-specific networking values you intend to recreate before replacing an earlier installation. A Factory Reset removes Lineup-owned settings, guide data, logs, Data Protection keys, and transient stream files, then returns the application to first-run setup.

## Installing Lineup on Windows

Lineup can run directly as a Windows application or inside Docker Desktop. Neither method requires the .NET SDK, a separate FFmpeg installation, Jellyfin, or Plex.

| Method | Best for |
|---|---|
| **[Docker Desktop](#recommended-run-lineup-with-docker-desktop) — Recommended** | An isolated, automatically restarted container with easier updates and log management |
| [Direct Windows application](#alternative-run-lineup-directly-on-windows) | The fewest installation steps and a visible console; Lineup runs while that console is open |

> **Docker Desktop is the preferred Windows installation method for now.** It provides the most reliable unattended operation, updates, restart behavior, and log access. Use the direct Windows application when you specifically want to avoid Docker and are comfortable leaving its console window open.

### Alternative: run Lineup directly on Windows

1. Open the [latest Lineup release](https://github.com/am385/Lineup/releases/latest) and expand **Assets**.
2. Download the ZIP matching the Windows **System type** shown under **Settings → System → About**:
   - Choose `Lineup-<version>-win-x64.zip` for an x64-based PC. This is the correct download for most Windows computers.
   - Choose `Lineup-<version>-win-arm64.zip` only for a Windows PC with an ARM-based processor.
3. Right-click the downloaded ZIP, select **Extract All**, and extract it to a stable folder such as `C:\Users\YourName\Applications`. Do not run Lineup from inside the ZIP.
4. Open the extracted `Lineup-<version>-win-<architecture>` folder and double-click `Start-Lineup.cmd`. Always use this launcher rather than opening `app\Lineup.App.Web.exe` directly; the launcher configures the user-local data locations.
5. If Microsoft Defender SmartScreen appears, confirm that the file came from the official Lineup GitHub release, select **More info**, and then select **Run anyway**. Published Windows packages are not currently code-signed.
6. If Windows Firewall asks for access, allow Lineup on **Private networks**. Public-network access is not required.
7. Keep the Lineup console window open. The launcher opens <http://localhost:8080>; refresh the page if the browser opens before Lineup finishes starting.
8. Complete the first-run setup by adding the HDHomeRun under **Settings → Physical Devices**, adding its SiliconDust source under **Guide Sources**, and saving.

Press `Ctrl+C` in the Lineup console or close that window to stop the application. Lineup stores mutable files outside the extracted program folder:

| Data | Windows location |
|---|---|
| Settings, guide database, logs, and other application state | `%LOCALAPPDATA%\Lineup\data` |
| Generated XMLTV guide | `%LOCALAPPDATA%\Lineup\guide\epg.xml` |
| Disposable CMAF fragments and subtitle sidecars | `%LOCALAPPDATA%\Lineup\transient` |

Enter `%LOCALAPPDATA%\Lineup` in File Explorer's address bar to open these folders. They remain in place when you replace the application during an update.

#### Update the direct Windows application

1. Stop the running Lineup console.
2. Download and extract the new ZIP for the same architecture.
3. Run `Start-Lineup.cmd` from the new folder.
4. If you configured automatic startup, replace the old Startup-folder shortcut with one that targets the new `Start-Lineup.cmd`.
5. After confirming that your settings are present, delete the old extracted program folder.

Do not delete `%LOCALAPPDATA%\Lineup` unless you intentionally want to remove the persisted installation. You can verify a download with `SHA256SUMS.txt` from the release assets:

```powershell
Get-FileHash .\Lineup-3.0.0-win-x64.zip -Algorithm SHA256
```

Compare the displayed hash with the line for that ZIP in `SHA256SUMS.txt`.

#### Optional: start Lineup when you sign in

1. Right-click `Start-Lineup.cmd`, select **Show more options → Send to → Desktop (create shortcut)**.
2. Press `Windows key + R`, enter `shell:startup`, and select **OK**.
3. Move the new shortcut from the Desktop into the Startup folder.

This starts Lineup only after that Windows user signs in. It is not a Windows service, and the Lineup console must remain open.

After a Factory Reset shuts down the direct Windows application, run `Start-Lineup.cmd` again so Lineup can apply the reset and return to first-run setup.

### Recommended: run Lineup with Docker Desktop

[Docker Desktop](https://docs.docker.com/desktop/setup/install/windows-install/) installs the Linux-container engine and Docker Compose that Lineup needs, provides a graphical interface for managing the container and viewing its logs, and keeps the application isolated from Windows. The Lineup image includes Jellyfin FFmpeg.

Before continuing:

1. Install Docker Desktop using its recommended per-user installation and WSL 2 backend. Docker Desktop requires a supported Windows version, hardware virtualization, and at least 8 GB of system memory. Review [Docker Desktop's current license terms](https://www.docker.com/legal/docker-subscription-service-agreement/) if using it for work or in a larger organization.
2. Open Docker Desktop and wait until it reports that the engine is running. Lineup uses a Linux container; if Docker Desktop offers a choice, select **Linux containers**.
3. Make sure the Windows computer can reach the HDHomeRun on the local network. Knowing the tuner's LAN IP address is helpful because names such as `hdhomerun.local` do not resolve in every Docker Desktop network.

#### Recommended: install with Docker Compose

Docker Compose is the easiest method to maintain because Lineup's ports, persistent storage, and restart behavior are already defined. Open **PowerShell** and run:

```powershell
New-Item -ItemType Directory -Force "$HOME\Lineup\certs" | Out-Null
Set-Location "$HOME\Lineup"
Invoke-WebRequest `
    -Uri "https://raw.githubusercontent.com/am385/Lineup/main/docker-compose.prod.yml" `
    -OutFile "docker-compose.yml"
docker compose up -d
Start-Sleep -Seconds 5
Start-Process "http://localhost:8080"
```

The first download can take a few minutes. Refresh the browser if Lineup is still starting. When Lineup opens, add the HDHomeRun under **Settings → Physical Devices**, add its SiliconDust source under **Guide Sources**, and save. If `hdhomerun.local` does not work, use the numeric LAN address shown in the official HDHomeRun application or your router, such as `192.168.1.50`.

Docker stores Lineup's settings, guide database, and XMLTV output in persistent named volumes. Replacing or updating the container does not delete those volumes.

Use these commands later from the same `$HOME\Lineup` directory:

```powershell
# View live logs. Press Ctrl+C to stop viewing without stopping Lineup.
docker compose logs --follow lineup

# Stop and start the existing container.
docker compose stop
docker compose start

# Download the newest published image and recreate the container without deleting data.
docker compose pull
docker compose up -d

# Remove the container and network while preserving Lineup's named volumes.
docker compose down
```

Do not add `--volumes` to `docker compose down` unless you intentionally want to delete Lineup's persisted data and begin with a new installation.

#### Alternative: install through the Docker Desktop interface

The graphical workflow avoids Compose commands, but every mapping must be entered manually:

1. Create `Lineup\data`, `Lineup\guide`, and `Lineup\transient` folders inside your Windows user folder. In File Explorer, enter `%USERPROFILE%\Lineup` in the address bar, create the three folders there, and note the full path shown by File Explorer.
2. In Docker Desktop, open **Images**, search Docker Hub for `am385/lineup`, and pull the `latest` tag. Sign in to Docker Hub if Docker Desktop requests it.
3. Find `am385/lineup:latest` under **Images**, select **Run**, and expand **Optional settings**.
4. Set the container name to `lineup`.
5. Map the following ports. Keep the UDP host ports at their defaults if you plan to enable virtual HDHomeRun discovery:

   | Host port | Container port | Protocol | Purpose |
   |---:|---:|---|---|
   | `8080` | `8080` | TCP | Lineup web interface |
   | `8443` | `8443` | TCP | Optional HTTPS |
   | `8600-8615` | `8600-8615` | TCP | Optional dedicated virtual-output listeners |
   | `65001` | `65001` | UDP | HDHomeRun discovery |
   | `1900` | `1900` | UDP | SSDP discovery |

6. Add these volume mappings:

   | Windows host folder | Container path |
   |---|---|
   | `C:\Users\YourName\Lineup\data` | `/appdata` |
   | `C:\Users\YourName\Lineup\guide` | `/guide` |
   | `C:\Users\YourName\Lineup\transient` | `/transient` |

   Replace `YourName` with the Windows user-folder name shown in File Explorer.

7. Select **Run**, wait for the container to start, then open <http://localhost:8080> and complete the first-run Physical Device and Guide Source setup.

No environment variables are required for a normal HTTP installation. The dedicated-output range is optional when every virtual output uses the management host; when using dedicated listeners, publish each configured listener port. HTTPS is optional; see [HTTP and optional HTTPS](#http-and-optional-https) before adding a certificate mount or password. Open the `lineup` container in Docker Desktop to start, stop, restart, delete, inspect, or view its **Logs**. If it does not start automatically after Windows restarts, open **Containers** and select **Start**.

To update a GUI installation, pull the newest `am385/lineup:latest` image, record or copy the existing container's settings, remove the old `lineup` container, and run the new image with the same ports and host-folder mappings. Removing the container does not delete the three Windows folders or their Lineup data. Compose is recommended because it remembers these settings and performs this replacement automatically.

### Windows networking help

Docker Desktop runs Lineup behind a virtualized network. The web interface normally works through <http://localhost:8080>, but local broadcast and multicast discovery can vary with the Windows, Docker Desktop, firewall, and router configuration.

- Configure the physical HDHomeRun by its LAN IP address if its hostname does not resolve or automatic discovery fails.
- Keep host UDP ports `65001` and `1900` mapped to the same container ports. Using different host ports can prevent Jellyfin, Plex, and other clients from discovering Lineup as a virtual tuner.
- Allow Docker Desktop through Windows Firewall on the private network.
- If Lineup and the tuner are on different VLANs or subnets, allow routing between them and configure the tuner address manually.
- You can still add Lineup to Jellyfin or Plex with the virtual device URL shown in Lineup even when UDP discovery is unavailable.

Running Docker directly inside a WSL distribution, Podman Desktop, and Rancher Desktop can also run Linux containers, but they require more manual administration or may behave differently from the documented Docker workflow. They are advanced alternatives rather than supported beginner installation paths.

## Architecture

Lineup models live TV as provider-neutral inputs, shared Core-owned processing, and independently configured outputs:

```mermaid
flowchart LR
    subgraph Inputs
        P1[Physical tuner 1]
        P2[Physical tuner 2]
        GS[Guide sources]
    end

    subgraph Core
        ACQ[Physical acquisition]
        PT[Physical-stage transcoding]
        MUX[Shared stream graph]
        GUIDE[(Normalized guide store)]
    end

    subgraph Outputs
        VH[Virtual HDHomeRun]
        LL[Lineup Link]
        WP[Web Player]
        GO[XMLTV / JSON guide outputs]
    end

    P1 --> ACQ
    P2 --> ACQ
    ACQ --> PT --> MUX
    GS --> GUIDE
    P1 -. subscription .-> GS
    P2 -. subscription .-> GS
    MUX --> VH
    MUX --> LL
    MUX --> WP
    GUIDE --> GO
    GUIDE --> VH
    GUIDE --> LL
```

Physical and virtual devices inherit their stage's global transcoding defaults and may override individual settings. Each virtual device subscribes to all eligible physical devices by default or to an explicit subset. The shared stream graph reuses byte-identical acquisitions and transforms across outputs, then branches only where effective output settings differ.

Projects are organized by dependency role:

- `src/Apps` contains executable presentation hosts.
- `src/Core` contains the dependency-free shared model, stable plugin API, and provider-neutral application and streaming foundations.
- `src/Plugins` contains independently composable adapters and support libraries grouped by provider or product family (`SiliconDust`, `LineupLink`, and `WebPlayer`). These plugins are statically composed today and are structured for future dynamic loading.
- `tests` mirrors the same architecture.

`Lineup.Core.Common` is the dependency floor and contains only shared data types. `Lineup.Core.Extensibility` references `Lineup.Core.Common` and adds plugin/runtime interfaces and boundary validation. Adapters may consume designated support libraries from the same provider family, but they do not reference `Lineup.Core` application behavior.
Plugin family folders are organizational only; adapters and support libraries remain independently referenced assemblies.

See [Project Dependency Map](docs/project-dependencies.md) for direct-reference diagrams and a purpose/usage catalog covering every production and test project in the solution.

### Apps

| Project | Description |
|---|---|
| `Lineup.App.Web` | Blazor Server presentation and HTTP transport composition |
| `Lineup.App.Tui` | Spectre.Console presentation and host composition |

### Core

| Project | Description |
|---|---|
| `Lineup.Core.Common` | Dependency-free identifiers, configuration records, normalized guide models, streaming DTOs, and serialized metadata types |
| `Lineup.Core` | Provider-neutral device, guide, topology, capacity, and shared-stream orchestration with authoritative SQLite storage |
| `Lineup.Core.Hosting` | Trusted topology/guide workers, stream shutdown, and graph/settings/storage adapters for portable media execution |
| `Lineup.Core.Extensibility` | Plugin/runtime interfaces, portable validation, and narrow host capabilities built on `Lineup.Core.Common` |
| `Lineup.Core.Streaming.FFmpeg` | Reusable FFmpeg/FFprobe process and streaming transforms |
| `Lineup.Core.VirtualOutputs.Protocol` | Versioned dependency-light local IPC contracts and bounded framing for isolated output workers |
| `Lineup.Core.VirtualOutputs.Hosting` | Worker-side bootstrap, authenticated broker client, and fail-closed lease lifecycle |
| `Lineup.Core.VirtualOutputs.Runtime` | Trusted worker provenance, activation, supervision, local IPC, and capability broker |

### Plugins

| Family | Project | Description |
|---|---|---|
| Guide Outputs | `Lineup.Plugin.GuideOutputs.Json` | Public versioned JSON guide document and serialization/deserialization API |
| Guide Outputs | `Lineup.Plugin.GuideOutputs.Json.Plugin` | JSON Guide Output driver, publisher adapter, settings, and host registration |
| Guide Outputs | `Lineup.Plugin.GuideOutputs.Xmltv` | Public serializer for normalized Lineup guide snapshots to XMLTV |
| Guide Outputs | `Lineup.Plugin.GuideOutputs.Xmltv.Plugin` | XMLTV Guide Output driver, global-output normalization, publisher adapter, and host registration |
| Lineup Link | `Lineup.Plugin.LineupLink` | Versioned remote-Lineup protocol plus physical input, virtual output, and optional guide-source adapters |
| Lineup Link | `Lineup.Plugin.LineupLink.VirtualOutputHost` | Dedicated per-device Lineup Link HTTP/HTTPS listener worker |
| SiliconDust | `Lineup.Plugin.SiliconDust.GuideSource` | Multi-device SiliconDust guide source adapter |
| SiliconDust | `Lineup.Plugin.SiliconDust.HDHomeRun.Api` | Remote API client — downloads XMLTV guide data from `api.hdhomerun.com` |
| SiliconDust | `Lineup.Plugin.SiliconDust.HDHomeRun.Device` | Local device communication — discovery, channel lineup, native binary protocol |
| SiliconDust | `Lineup.Plugin.SiliconDust.HDHomeRun.PhysicalTuner` | Multi-instance physical HDHomeRun adapter |
| SiliconDust | `Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner` | Aggregated virtual HDHomeRun identity, discovery, lineup, and capacity behavior |
| SiliconDust | `Lineup.Plugin.SiliconDust.HDHomeRun.VirtualOutputHost` | Dedicated per-device HDHomeRun HTTP/HTTPS listener worker |
| SiliconDust | `Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryRuntime` | Trusted snapshot reconciler for the shared isolated discovery process |
| SiliconDust | `Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryHost` | Shared isolated UDP 65001 and SSDP 1900 discovery worker |
| Web Player | `Lineup.Plugin.WebPlayer` | Built-in Web Player contracts, browser capability policy, and shared CMAF output lifecycle |
| Web Player | `Lineup.Plugin.WebPlayer.FFmpeg` | FFmpeg CMAF planning, HLS/DASH manifest processing, and browser playback-test generation |
| Web Player | `Lineup.Plugin.WebPlayer.VirtualOutputHost` | Dedicated CMAF/HLS/DASH media listener and FFmpeg worker |

## Building from source

The following instructions are for developers who want to build or modify Lineup. Windows users who only want to run the published application should use [Installing Lineup on Windows](#installing-lineup-on-windows).
All project build outputs and intermediate files are centralized beneath the repository-level `out` directory.

### Development prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An HDHomeRun device on your local network
- [Jellyfin FFmpeg](https://github.com/jellyfin/jellyfin-ffmpeg) (required for live TV transcoding and AC-4 decoding; included in the Docker image and available as `ffmpeg` on `PATH`)

On Windows, install the pinned portable Jellyfin FFmpeg build for local development:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-JellyfinFfmpeg.ps1
```

The script verifies the official release checksum and installs `ffmpeg.exe` and `ffprobe.exe` into the ignored repo-root `.ffmpeg` directory. Source builds add that directory to the Lineup process `PATH` automatically. Packaged Windows builds continue to use the `.ffmpeg` directory bundled beside the application. Restart the app after installation.

### Running the Web App

```bash
cd src/Apps/Lineup.App.Web
dotnet run
```

The app starts on `http://localhost:8080` by default. Persistent application data defaults to `/appdata`, XMLTV output defaults to `/guide/epg.xml`, and transient stream artifacts default to `/transient`. The development launch profiles keep local runtime state in the ignored repository-root `.lineup` directory, separated into `appdata`, `guide`, and `transient` subdirectories. Configure the HDHomeRun device address through the Settings UI.

### Running the TUI

```bash
cd src/Apps/Lineup.App.Tui
dotnet run
```

Normal Web and TUI builds install the shipped isolated virtual-output workers into each host's `virtual-output-workers` directory, including Visual Studio builds and debugger launches.
Packaging uses a shared, locked cache under `out\virtual-output-worker-packages` and separate worker compilation outputs.
`build\Lineup.VirtualOutputWorkers.props` is the authoritative worker inventory for MSBuild and the existing .NET packager. Docker uses normal Web publishing; Windows packaging invokes that same packager through its PowerShell wrapper. Manifests, executable selection, hashes and protocol metadata are generated in one place.
Unchanged source/build inputs reuse verified packages; missing or modified installed files are repaired on the next build without rewriting unchanged files.
A normal `dotnet publish` of either host includes the same worker packages in the published distribution.
Publishing packages workers once, then copies only the verified `packaging.files` inventory through the standard publish-file pipeline, including `--no-build` publishes. Stale or unverified output files are not included. Worker apphosts and dependencies stay outside a single-file host bundle.
Design-time builds do not package workers. Set the MSBuild property `SkipVirtualOutputWorkerPublish=true` only when a specialized packaging flow, such as the Windows release build, supplies those packages separately.
Visual Studio's up-to-date check tracks worker source inputs, manifests, and primary runtime files so missing packages or worker changes request a real build.

Configure the TUI via environment variables:

- `Lineup__TuiAppDataPath` — persistent TUI data directory; defaults to `.lineup/tui` beneath the working directory
- `Lineup__DatabasePath` — path to the authoritative SQLite channel and guide database

The TUI stores topology and guide-retention settings in `settings.json` beneath its app-data directory; it does not read a standalone `streaming.json`.
On first launch, use **Configure Sources** to add physical devices, guide sources, and virtual outputs before explicitly refreshing channels or guide data.
Missing settings start with defaults and no physical inputs; incompatible or unreadable persisted settings enter the explicit factory-reset recovery flow, with valid-backup recovery when available.
Web and TUI honor the same configured guide-history retention policy. The TUI retains manual channel/guide refresh commands; it does not automatically enable the Web host's periodic refresh workers.

### Building and running with Docker

```bash
docker compose up -d
```

Or using the production image:

```bash
docker compose -f docker-compose.prod.yml up -d
```

These commands run from a source checkout. The Docker image includes Jellyfin FFmpeg for live TV transcoding, ATSC 3.0 AC-4 audio decoding, and the isolated Lineup Link and HDHomeRun worker packages. Data is persisted via the `appdata` named volume mounted at `/appdata`. CMAF fragments and subtitle sidecars use the separate `transient` volume mounted at `/transient`. Lineup uses fixed container ports for its management host HTTP (`8080`), HTTPS (`8443`), HDHomeRun discovery (`65001/udp`), and SSDP (`1900/udp`). Dedicated output listener ports are configured per virtual device. Both Compose files publish container ports `8600-8615` by default, supporting up to 16 dedicated listeners.

Set `VIRTUAL_OUTPUT_PORTS` to an equal-sized host-to-container range mapping when a deployment needs different external ports or a different number of published listeners. The internal range must include every port assigned to a virtual device. For example, `VIRTUAL_OUTPUT_PORTS=9600-9607:8600-8607` publishes eight internal listeners on host ports `9600-9607`. In each virtual device's dedicated **Output Hosting** settings, select its internal listener port and set the optional published-port override to the corresponding host port; Lineup uses the published port in discovery responses and connection bundles. Docker requires both sides of a range mapping to contain the same number of ports. The Compose `HTTP_PORT`, `HTTPS_PORT`, `HDHOMERUN_DISCOVERY_PORT`, and `SSDP_PORT` variables change only the corresponding host-facing management or discovery ports. Keep the UDP ports at their defaults for standards-based automatic discovery. Native Linux deployments can use `network_mode: host` if the HDHomeRun device requires local network discovery.

### Publishing a Development Build

Maintainers can open **Actions → Publish Lineup Dev → Run workflow**, select any branch, and start a development publication. The selected revision is tested before the workflow publishes the moving development images to Docker Hub and GitHub Container Registry:

```text
am385/lineup:dev
ghcr.io/am385/lineup:dev
```

Each run also publishes a traceable Docker tag in the form `dev-<run-number>-<short-sha>`. Development binaries use a version derived from `Directory.Build.props`, such as `3.0.0-beta-dev.42+abc1234`, without changing the release version in source control.

The workflow creates or updates the **Lineup Dev** GitHub prerelease under the moving `dev` tag. Its `Lineup-dev-win-x64.zip`, `Lineup-dev-win-arm64.zip`, and checksum assets are replaced on each successful publication. The workflow run retains versioned Windows packages and checksums as a 30-day Actions artifact, and the prerelease notes identify the exact source ref, commit, version, run, and traceable Docker tag.

Starting a newer development publication cancels an older in-progress development run so the older revision cannot overwrite the newer requested `dev` channel. Development builds are mutable and intended for testing; use a stable versioned release for production installations. The workflow requires the same `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN` repository secrets as stable publication, while GHCR uses the workflow's built-in GitHub token.

### Publishing a Release

`Directory.Build.props` is the single source for Lineup's release version. Before publishing, update its `<Version>` value to the next stable semantic version.

Maintainers can then open **Actions → Publish Lineup Release → Run workflow** and select the commit or branch to publish. The workflow reads the application version, tests the selected revision, and publishes both the full version and major/minor tags to Docker Hub and GitHub Container Registry:

```text
am385/lineup:3.0.0
am385/lineup:3.0
ghcr.io/am385/lineup:3.0.0
ghcr.io/am385/lineup:3.0
```

The workflow also builds self-contained x64 and ARM64 Windows ZIPs with the matching pinned Jellyfin FFmpeg distribution. A manual run makes the ZIPs and `SHA256SUMS.txt` available as a workflow artifact but does not create a Git tag or GitHub Release. A pushed stable tag such as `v3.0.0` creates or updates the matching GitHub Release and attaches those Windows assets. Tag publication stops with an explicit error if the tag does not exactly match `v` followed by the version in `Directory.Build.props`.

Select **Also update the latest tag** only for the current stable release. Manual publication requires the `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN` repository secrets; GHCR uses the workflow's built-in GitHub token. Manual publication does not create a Git tag or GitHub Release.

## Streaming and playback

### MPEG-TS virtual outputs

The MPEG-TS proxy preserves every audio track and every standalone subtitle codec the MPEG-TS muxer can represent
(`dvb_subtitle` and `dvb_teletext`). It copies video and non-AC-4 audio while converting each AC-4 track to AC-3.
Language, title, and disposition metadata are retained where the container supports them. Embedded CEA-608/708 data is
retained with copied video and passed to the H.264 encoder when video conversion is enabled. Text/WebVTT, ASS, PGS, DVD,
and other subtitle formats that MPEG-TS cannot carry are reported in diagnostics rather than mapped.

### Web Player and Watch

**Watch** at `/watch` creates one two-second fragmented-MP4 presentation and exposes the same initialization and media
fragments through both a DASH MPD and HLS playlists. With a
completed browser playback profile, Auto protocol selects a measured protocol and Auto video/audio chooses a measured
source-copy path first. Without a current profile, Watch uses its configured DASH-first and user-selected behavior. Explicit
DASH/HLS, Source, and Fallback selections remain overrides. Shaka Player provides the initial cross-browser playback
implementation. At App Default quality, eligible H.264 or HEVC source video is copied when source playback was measured or
Source video is explicitly preferred, without simultaneously encoding H.264. If source video unexpectedly fails in the
browser, Watch clears the contradicted profile and restarts that browser's stream once with H.264. Explicit H.264 Fallback,
scaled quality settings, and bitmap subtitle burn-in produce only H.264. Every source audio track produces one copy-first
or fallback rendition; Lineup does not continuously encode a duplicate fallback beside a supported source. If real
playback contradicts the saved profile or FFmpeg cannot package a source codec, the session performs one bounded restart
using the configured fallback. Fallback Audio choices are ordered from highest
quality/lower browser support to broadest browser support: EAC3 up to 5.1, AC3 up to 5.1, AAC up to 7.1, AAC up to 5.1, and
AAC Stereo. AAC Stereo is the default. FFmpeg encodes channel-based E-AC-3 but cannot create E-AC-3 JOC/Atmos; existing
E-AC-3 Atmos metadata can be retained only when the Source rendition is copied successfully.
Dedicated Web Player hosting also retries a recognized source-audio MP4 muxer failure once with fallback audio, within the original
20-second media-readiness budget. The retry retains the brokered source, clears partial media/caption output, and reports the final audio
plan; unrelated failures and cancellation do not trigger recovery.

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
`dotnet run --project scripts/Lineup.Tools.BrowserAssets -- build/browser-assets.json out/browser-assets --verify`.
Omit `--verify` to restore it explicitly; ordinary Web builds and publishes perform that restoration automatically.

The HEVC-to-H.264 topology default applies only when the effective video entering the CMAF output stage is HEVC.
Progressive H.264, including video already converted by the physical stage, remains copy-first unless another output policy
requires encoding. Explicit video overrides retain precedence in both management and dedicated hosting.

HTTPS Watch selects the first dedicated binding with an HTTPS public URL, even when an HTTP binding appears first.
This uses the public URL rather than internal listener transport, supporting HTTPS reverse-proxy termination.
If no HTTPS public URL is configured, startup reports a configuration error without issuing a media request; HTTP loopback is
not an exception. HTTP Watch retains the first valid HTTP(S) public binding in configuration order.

Startup observes upstream input failures as well as FFmpeg readiness. Source and processor failures return sanitized
HTTP 502 responses (`media_source_failed` or `media_process_failed`) rather than waiting for a generic startup timeout.
Retained logs correlate the media session, broker request, stream, startup stage, exit codes, and bounded stderr tails
(at most 50 non-empty lines and 16,384 characters per tail); these details are not included in public error responses.
Audio probing refreshes provisional video-only observations when audio is advertised or its availability is unknown.
Stage inventories retain `AudioDiscovery`: `Detected`, `DeclaredAbsent`, `ExpectedMissing`, or `Unknown`.
Advertised-but-undiscovered audio, or audio lost between raw and effective metadata, fails CMAF planning explicitly with
`media_audio_missing`; a declared video-only source remains supported. Unknown absence remains provisional, not proof of
a video-only broadcast. Startup waits for initialization resources and two fragments for each planned representation;
this verifies resource readiness, not audible sound, browser decoding, or lip-sync.

Media responses acquire their backing file before returning it, so removal cannot invalidate an already-open response.
Incomplete DASH publication snapshots return HTTP 425 `media_manifest_not_ready` instead of malformed successful manifests.
Dedicated-session inactivity uses a monotonic clock and a minimum 30-second lifetime, covering the ten advertised
two-second segments plus five extra retained segments. This does not retain closed sessions indefinitely or guarantee
new requests after an explicit stop or terminal producer cleanup.

In both management-hosted and dedicated modes, Watch viewers with identical effective playback requests (apart from client ID), profiles, and exact physical
source generations share one CMAF producer within their output host. Each viewer retains its own manifest/subtitle URLs, client diagnostics,
inactivity lifetime, and admission-limit slot. Stopping or expiring one viewer leaves the others attached; the final viewer's
release follows the configured keep-warm policy. Different media plans, bitmap-subtitle selections, exact physical tune/runtime generations, and output hosts remain isolated.
Warm retention is best-effort: tuner or media-resource pressure can retire a producer only after its final viewer leaves.
Retirement waits for owned process, input, and capacity cleanup before admitting replacement work; active shared prefixes are never preempted.
An idle warm producer has no active viewer admission slots. Dedicated mode retains its canonical broker input separately from the initiating viewer;
additional viewers still have independently admitted, drained broker inputs, not additional FFmpeg producers. These active and warm input leases remain
subject to the worker's outstanding broker-call budget. Worker `/health` and passive `/media/sessions` inventories distinguish viewers from producers;
inventory and broker-status polling do not refresh media inactivity.

Watch hides manual stream policy controls behind **Override stream settings**. Each protocol, quality, video, audio, and
fallback-audio setting can independently use **Browser profile** or a manual value. Overrides are applied to a request-scoped
effective capability matrix, so an audio-only override preserves measured video, protocol, and subtitle decisions and never
modifies the browser's saved Watch Test profile. Shaka's in-player menu switches active audio renditions and text subtitles
without restarting the shared session; the bitmap-subtitle selector remains available independently of the override panel.
Every standalone text subtitle and detected embedded-caption track is prepared as a transient WebVTT sidecar so Lineup's Shaka
captions menu can offer all of them without restarting the shared session. The page-level
subtitle selector lists only bitmap burn-in choices. Text conversion is inexpensive; embedded-caption extraction adds one moderate
fixed decode/demux workload per session. Bitmap subtitles remain explicit burn-in choices because each simultaneous bitmap rendition
would require another video encode.
Dedicated Watch metadata and remote stop notifications are associated with the returned worker media-session ID, not the broker stream ID.
Management-hosted playback retains client-ID association. Old or unrelated worker sessions cannot displace current track information or
stop replacement playback; dedicated broker ownership is also released if browser-side server shutdown fails.
Dedicated Web Player listeners expose the incremental subtitle offset header to cross-origin Watch clients, so captions continue beyond
the first 64 KiB. Each subtitle track advances by the received byte count; a missing or inconsistent offset header produces one browser
console warning per track without resetting its read position.
Captions that the tuner and FFmpeg do not expose or detect remain unavailable. Shared DASH and HLS presentations are served through the
canonical `/api/stream/cmaf` API.

Encoded video schedules keyframes relative to the preceding forced keyframe, keeping segment cuts at least
two seconds apart even at fractional frame rates such as 29.97 fps. This avoids skipped DASH cut points and
unexpected roughly four-second segments; source-copy renditions retain their original keyframes.
The opt-in streaming sweep verifies retained MP4 timestamps against DASH timelines for both DASH and HLS.
Independent audio/video segment numbers and buffered endpoints are diagnostics, not lip-sync measurements.

Watch and Watch Test stop their owned server viewer before awaiting browser-player destruction, so a delayed
or failed Shaka teardown cannot skip the server stop. Stop requests retain `keepalive` for navigation and
check HTTP responses; HTTP 404 means the viewer is already absent, while other failures are surfaced.
Failed server stops retain their captured ownership for a subsequent stop/disposal attempt. Cleanup remains
viewer-scoped and does not stop another browser's shared-output viewer.

### Watch Test

**Watch Test** at `/watch-test` keeps its manual, tuner-free exact-presentation player and adds an explicit **Run All**
playback validation suite. The suite measures DASH and HLS, H.264, HEVC Main and Main 10, AAC through 7.1, AC-3/E-AC-3 through 5.1,
WebVTT sidecars, and subtitle burn-in as independent cases rather than an impractical full cross-product. Every generated
presentation contains one video and one audio rendition and never retries another protocol or codec. Its audio contains a
low-volume repeating tone sequence that activates one channel at a time. Manual Exact Test Selection
starts with audio enabled for speaker-order verification; if the browser blocks audible autoplay, use the native Play control.
WebVTT and burn-in subtitle modes identify the active channel in sync with its tone; None omits the label. Automated full-suite
and row tests remain muted. Each synthetic presentation is bounded to ten minutes so its repeating audio, WebVTT timeline, and
burned-in labels share the same lifetime.
Browser Media Source claims are stored separately from observed Shaka playback and do not authorize source copy by themselves. A completed,
versioned profile is saved in browser-local storage and sent only with that browser's tune requests; canceled or incomplete
runs keep the previous completed profile. AC-4 remains claim-only because bundled Jellyfin FFmpeg has an AC-4 decoder but no
AC-4 encoder. Auto planning copies measured-compatible source media and otherwise emits one best-supported transcode,
prioritizing channel preservation before codec quality. Profiles with an old schema or invalid catalog data are ignored or
rejected, and a runtime contradiction clears the profile so it can be rerun. Watch Test never modifies global
transcoder settings.

Future CMAF work includes an adaptive video bitrate ladder, LL-HLS and low-latency DASH evaluation, and a native Safari HLS
path. Native Safari, Dolby audio, AirPlay, background playback, and power behavior require validation on real macOS and iOS
hardware.

WebVTT sidecars live only beneath the application-owned `lineup-subtitles` temporary directory. They are removed on
disconnect, stop, application startup, and Factory Reset. If captions do not appear, inspect the active-stream details and
logs: captions embedded in video but not exposed by the tuner or FFmpeg as an addressable subtitle stream can be preserved
in MPEG-TS but cannot be synthesized as a Watch text track.

### Transcoding and shared stream graph

Virtual Tuner Video can optionally transcode HEVC to H.264 for clients that cannot process HEVC Live TV, at the cost of CPU and generation loss. Virtual Tuner Audio can preserve non-AC-4 codecs or transcode those tracks to AC-3/E-AC-3. AC-4 can be preserved for compatible receivers or transcoded separately. Audio above 5.1 channels is capped at 5.1 when transcoded. Transcoding is configured through inherited physical and virtual device profiles; topology routes reject unsupported per-request HDHomeRun hardware profile parameters.

The Physical Devices and Virtual Devices tabs provide inherited Video and Audio transcoder profiles. Physical deinterlacing normalizes confirmed interlaced input once before the shared multiplexer, while virtual-device settings create consumer-specific output targets afterward. Source-frame-rate mode converts 29.97-frame interlaced video to 29.97p (or 25 to 25p) with lower CPU use; source-field-rate mode preserves temporal motion as 59.94p (or 50p) at higher CPU cost. Progressive sources retain the copy-first path unless another enabled conversion requires transcoding.

Lineup builds each live stream as a reference-counted graph: exact physical acquisition, optional physical normalization, and virtual output packaging. Consumers share the longest byte-identical prefix, including across different virtual devices, and branch only where their effective output settings differ. Physical failover is resolved to an exact device before its profile is applied, so a fallback source shares with other consumers of that source and uses its own overrides. FFprobe metadata is also shared for the lifetime of the acquisition. The Dashboard visualizes the full configured topology and highlights active, shared, transcoding, and warm paths above the detailed Active Streams table. The status API exposes active graph nodes, stable node and parent identities, processing stages, receiver counts, warm state, subscription totals, and physical-capacity use.

Stream admission reserves both a logical viewer slot and a pending-startup slot **before** source acquisition, probing, or
transcoding. Overload is rejected immediately with HTTP 429 (`stream_limit` for Lineup Link); dedicated workers receive the
existing `ResourceLimit` broker rejection. Existing viewers are not displaced. Producer sharing is unchanged: each admitted
viewer still needs its own slot, even when joining an existing producer. Pending startup reservations count toward
**Maximum Concurrent Streams**, and that setting's zero/unlimited behavior and physical tuner limits are unchanged.
Startup slots are released on readiness, failure, cancellation, or cleanup; dedicated Web Player slots remain held through
worker media planning and readiness, not just the broker's physical-stream response.

The host also enforces these finite, startup-scoped defaults:

| Host budget | Default | Configuration key |
|---|---|---|
| Concurrent pending stream startups | 8 | `StreamingResources:MaximumPendingStarts` |
| Queued payload per graph subscription | 8 MiB | `StreamingResources:SubscriberBufferMib` |
| Distinct queued payload across the graph | 128 MiB | `StreamingResources:GraphBufferMib` |
| Maximum unread media age | 5 seconds | `StreamingResources:MaximumBufferAgeSeconds` |
| Overall physical-source acquisition | 20 seconds | `StreamingResources:AcquisitionTimeoutSeconds` |

These are host configuration, **not** persisted `settings.json` fields or Settings-page controls. Set them in the host's
`appsettings.json` and restart the host, or use equivalent environment variables such as
`StreamingResources__MaximumPendingStarts=8`. For example:

```json
{
  "StreamingResources": {
    "MaximumPendingStarts": 8,
    "SubscriberBufferMib": 8,
    "GraphBufferMib": 128,
    "MaximumBufferAgeSeconds": 5,
    "AcquisitionTimeoutSeconds": 20
  }
}
```

Zero does not disable these safety budgets: invalid configuration fails explicitly. Pending startups support 1-1024,
buffer budgets 1-1,048,576 MiB, and age/acquisition budgets 1-300 seconds. A partially read array remains accounted until its
last byte is consumed or its owner is released; sharing the same array across readers counts it only once globally.
The existing 2048-entry ceiling remains as an additional metadata safeguard. Slow readers exceeding their byte, entry,
or age allowance are disconnected with a logged stream failure; aggregate pressure evicts the largest queued backlogs
instead of blocking unrelated producers. Age expiry runs even when a producer stops sending data. Ordinary EOF preserves
unread data within the budgets; errors, shutdown, and disposal release it immediately.
Temporary startup reservations are actively drained during probing until the replacement output is subscribed, preventing
long probes from turning an intentionally held reservation into a slow reader and unnecessarily reopening the source.

Additional deployment-wide media admission limits are **disabled by default** and use the same restart-scoped host
configuration (including `StreamingResources__MaximumProducers` environment-variable syntax):

| Media admission | Default | Configuration key |
|---|---|---|
| Retained acquisitions, graph transforms, and CMAF outputs | 0 (unlimited) | `StreamingResources:MaximumProducers` |
| Running FFmpeg/FFprobe jobs | 0 (unlimited) | `StreamingResources:MaximumProcessingOperations` |
| Required free space on each actual media workspace volume | 0 (disabled) | `StreamingResources:MinimumMediaFreeSpaceMib` |

Counts are shared by management and dedicated workers, not per listener. Each independently retained graph acquisition,
transform, or CMAF output consumes one producer reservation, including warm and synthetic Watch Test outputs. Every actual
FFmpeg/FFprobe process consumes one processing reservation, including remuxing, probing, caption extraction, fallback
slates, and compatibility tests. A pipeline may therefore need several producer and processing reservations; these limits
are neither viewer limits nor weighted CPU estimates. Equivalent joins reuse existing reservations but retain independent
viewer admission. Count pressure first asks owners to retire zero-viewer warm work and awaits cleanup, then explicitly
rejects if capacity remains unavailable. Processing admission has a ten-second deadline.

Dedicated media reservations use authenticated ownership channels. Remote retirement checks the worker's authoritative
viewer count; revocation or control-channel loss stops dedicated playback rather than continuing with an unaccounted local
fallback. Reservations remain charged through actual producer cleanup or OS process exit. Additional viewers still retain
their independently admitted full-rate broker input and drain it; this change does not introduce a viewer-only IPC lease.

Free space is checked on the actual management/worker output volume before new media allocation, not on a configured
path supplied by a worker to the trusted host. Insufficient space or an unreadable volume rejects allocation explicitly.
This is an admission watermark, **not** a disk quota, a reservation of future bytes, ongoing enforcement on active writers,
or permission to delete active output files. Concurrent writers can still exhaust disk after admission. The status API's
`mediaResources` section exposes current producer/processing usage, effective limits, the free-space watermark, and
`enforcementScope: "deployment"`. Negative media limits are invalid.

Graph diagnostics expose `BufferedBytes` and `BufferEvictions`. The `Lineup.Streaming` .NET meter additionally publishes
`lineup.streaming.buffered_bytes` and `lineup.streaming.buffer_evictions`; eviction reasons are the bounded values `bytes`,
`entries`, `aggregate`, and `age`. These limits measure **queued payload**, not process RSS, queue metadata, HTTP/socket buffers,
CMAF files, FFmpeg memory, or total deployment memory. Worker process policies are a separate, opt-in boundary.

Changing a physical device's adapter or connection configuration, disabling it, or removing it disconnects all viewers using that device,
including shared transforms and warm streams. Other devices keep running. Previously resolved routes cannot reopen the retired adapter.
The replacement is published after old graph resources finish cleanup; physical capacity stays reserved until its owned source is actually disposed.
New acquisitions, derived transforms and probe metadata use the replacement runtime's identity, while capacity remains aggregated by configured device.
Once viewer retirement begins, cleanup and replacement publication finish even if the configuration caller cancels; cancellation before that point preserves the prior catalog.
Name, ordering and nonconnection profile edits do not replace the physical adapter or disconnect its viewers. Status reports the current configured name.

Jellyfin FFmpeg's AC-4 support is decoder-only and does not support every object-based or Dolby Atmos AC-4 presentation. Unsupported presentations are reported as stream errors rather than silently copied or discarded. Watch copies eligible source audio and starts a configured fallback rendition only if source packaging or playback fails.

When an HDHomeRun reports DRM error 811, API streams return a protected-content error by default. The Settings page's Streaming tab can instead enable a synthetic **Content Protected** slate. The fallback is generated locally as H.264 video with silent AAC audio in the requested MPEG-TS or CMAF format; protected programming is never decrypted. Synthetic DRM and disabled-channel slates count toward **Maximum Concurrent Streams** but do not consume physical tuner capacity. Web-only refresh settings for active-stream information are configured on the Dashboard tab.

The slate policy also applies when protection is discovered during acquisition, even if cached DRM metadata is missing or stale.
Equivalent sources are tried first: a healthy source wins, and only an all-protected acquisition failure becomes a slate.
Mixed protection and availability failures remain explicit errors rather than being presented as protected content.

## Configuration

Runtime application settings are configurable through the web UI's settings page and persisted to `settings.json`.
Startup-only host budgets are documented separately above.

| Setting | Description |
|---|---|
| Physical Devices | One or more named HDHomeRun or Lineup Link inputs, each with ordering and sparse transcoding overrides |
| Physical Transcoding Defaults | Global input-stage settings inherited by every physical device unless overridden |
| Guide Sources | Named SiliconDust or optional Lineup Link sources subscribed to all or selected compatible physical devices, plus refresh scheduling and guide-history retention |
| Guide Outputs | Built-in global XMLTV plus additional scoped XMLTV or JSON publications selected from enabled guide sources and virtual devices |
| Virtual Devices | Built-in Web Player plus virtual HDHomeRun and Lineup Link outputs, each subscribed to all or selected compatible physical devices |
| Virtual Transcoding Defaults | Global output-stage settings inherited by every virtual device unless overridden |
| XMLTV Output Path | Under Guide Outputs, controls where the built-in global XMLTV document is published. The default is `/guide/epg.xml`. Enter a directory directly or use the server directory browser. `Lineup:GuideOutputDir` optionally changes the initial default directory and remains available as the reset value; the saved output path is authoritative afterward. |
| Automatic Guide Refresh | Independent source schedules: SiliconDust uses randomized 20-28-hour intervals; Lineup Link uses `AutoFetchInterval` (24 hours by default). Failures retry after one hour |
| Virtual Tuner Audio | Preserve non-AC-4 audio codecs, or transcode those tracks to AC-3/E-AC-3 |
| AC-4 Audio | Preserve AC-4, or transcode it to AC-3 (default) or E-AC-3 |
| Virtual Tuner Video | Preserve the video codec (default), or transcode HEVC to H.264 for clients that do not support HEVC Live TV |
| General Interlaced Video | Preserve the source (default), or deinterlace each confirmed interlaced tuner source once to progressive H.264 at its source frame or field rate |
| Web Player Interlaced Video | Preserve the effective source (default), or apply per-session fallback deinterlacing in Watch |
| DRM-Protected Content | Return an explicit error (default), or stream a synthetic Content Protected slate |
| Disabled Channels | Return an explicit error (default), or stream a synthetic Disabled Channel slate without opening a physical tuner |
| Maximum Concurrent Streams | Limits logical consumers, not shared producers; multiple consumers sharing one pipeline each count toward the limit |
| Keep Warm Period | Keeps an unused shared output reusable for 0-300 seconds; the default of 0 releases it immediately, while nonzero values retain tuner capacity and transforms until expiry |
| Active Stream Refresh | Dashboard refresh interval for hosted stream details; defaults to 5 seconds and 0 disables auto-refresh |
| Virtual HDHomeRun Devices | Friendly name, optional tuner cap, physical subscriptions, and a reachable advertised URL when served through the management host |
| Output Hosting | Per-output choice between the management UI host and an isolated dedicated listener; dedicated mode derives its client-facing URL from transport, bind address, and port, with advanced overrides for proxies, NAT, Docker, TLS, network, and worker controls |
| Network Discovery | Opt-in SiliconDust UDP (65001) and SSDP (1900) advertisement of enabled virtual HDHomeRun devices |
| File Logging | Opt-in rolling files with configurable minimum level, retention period, and per-file size limit; changes apply immediately when saved |
| Status API Privacy | Independently redact active-stream client addresses, device addresses, and URLs from `/api/v1/status` |

The built-in XMLTV output accepts relative filenames and absolute filesystem paths; the Web editor presents its containing directory. Lineup's web settings are unauthenticated and are intended for a trusted local network; do not expose the application to untrusted clients when arbitrary output paths are enabled.

Each Lineup Link virtual device can issue multiple named access grants so separate remote consumers do not share one bearer token. Grants can be independently rotated, expired, revoked, and permanently purged while sharing the virtual device's channels, transcoding, listener, and security policy. New outputs contain no grants and therefore deny every remote request until the first grant is created. Create separate Lineup Link virtual devices when consumers need different output behavior or network isolation. The remote output stores only SHA-256 token hashes; each consuming physical device stores its usable token in adapter configuration. Connection bundles reveal the raw token only when a grant is created or rotated and must be handled as secrets.

A management-hosted Lineup Link output may export a bundle before **Public Lineup URL** is known. The bundle then contains a null address, and the consuming instance leaves its physical-device URL empty for later entry. Any supplied URL must be an absolute HTTP or HTTPS address that matches the output's transport policy. HTTPS uses normal operating-system certificate validation and can add an exact SHA-256 certificate pin. HTTP remains authenticated but unencrypted and should be limited to trusted networks.

Dedicated Lineup Link connection bundles use the same effective client-facing URL as the saved listener: the automatically derived address unless a manual override is enabled, with any published-port override applied. Export, copy, and display use this URL, including redacted display after reloading saved grants.

Lineup Link manifest, lineup, and guide requests have a 30-second total timeout covering response headers and body, including error responses. Timeouts, malformed JSON, and unsupported remote protocol versions are reported as recoverable remote request failures so other configured physical devices can still activate; caller cancellation remains cancellation. Live MPEG-TS streams do not have this timeout. If a remote stream rejects a revoked, expired, or otherwise invalid access grant with HTTP 401 or 403, Lineup reports a non-retryable failure for that source and can continue to the next equivalent source. Device probing still reports rejected credentials explicitly.

HDHomeRun discovery and lineup HTTP deadlines are also reported as recoverable device failures. A stalled tuner does not prevent healthy devices from activating or refreshing their lineups, and a failed refresh preserves the tuner's committed channels and identity overrides. Explicit caller cancellation still cancels the operation.

Published virtual-channel protection considers all enabled, subscribed equivalent sources: a channel is not marked protected merely because its preferred source is protected when a clear or unknown equivalent remains. Other published metadata retains preferred-source precedence. Management and dedicated Lineup Link outputs use this same projection; protected-only outputs retain the configured error/slate policy.

Physical source selection has one configurable overall acquisition allowance (20 seconds by default), including remote stream
headers and error-body parsing. Each candidate receives a share of the remaining allowance, capped at ten seconds, so stalled
preferred sources leave time for healthy fallbacks. Exhausting the overall deadline prevents starting later sources and reports
`source_acquisition_timeout`. A candidate timeout or invalid stream response releases that candidate's subscription; other
subscribers to the same pending acquisition are not canceled. Source adapters must honor cancellation; cleanup retains
physical-capacity ownership until the source actually stops rather than abandoning a noncooperative adapter. Once acquired,
a live stream has no acquisition time limit. Explicit caller cancellation stops selection instead of trying another source.
The separate CMAF presentation-startup/readiness timeout remains in force.

Network-facing Lineup Link, virtual HDHomeRun, and Web Player media outputs can be served through the management UI host or through one isolated worker process per virtual device. Management-hosted mode reuses the existing Lineup HTTP/HTTPS ports for simpler local deployments. A management-hosted virtual HDHomeRun needs its externally reachable management URL in **Advertised base URL** when network discovery is enabled. Dedicated mode gives the output its own sockets, firewall boundary, TLS and network policy, and process lifecycle. Dedicated workers receive instance-scoped capabilities over authenticated named-pipe or Unix-domain-socket IPC. A separate shared HDHomeRun discovery worker owns UDP 65001 and SSDP 1900 and receives only bounded advertisement snapshots for enabled virtual devices. Web Player presentation remains in Blazor in both modes; its selected host owns CMAF/HLS/DASH generation and delivery. Worker packages are hash-verified before launch, bootstrap credentials are one-time and generation-bound, leases fail closed, and TLS certificate bytes are delivered through activation stdin rather than exposing trusted-host certificate paths. The Web and TUI hosts both run the same trusted supervisor for outputs configured in dedicated mode; `Lineup:VirtualOutputs:WorkerPath` overrides the packaged `virtual-output-workers` directory.

Managed workers receive a unique temporary directory beneath the trusted host's operating-system temporary path through their sanitized environment. This keeps dedicated Web Player media artifacts writable for ordinary Windows users without inheriting the host's user profile or unrelated environment variables. The launcher removes the directory after terminating and disposing the worker, including failed launches, and logs cleanup failures with the affected path. These worker directories are separate from the management host's `Lineup:TransientPath` setting.

Nonzero worker CPU/memory policies are preflighted before saving changed topology and before launching a worker. Zero remains
opt-in/unlimited and requires no resource-controller privileges. Unsupported enforcement fails closed with an actionable
configuration/runtime error; it never silently launches an unlimited worker or repeatedly spends its restart budget.
An existing unsupported deployment can still load its settings and perform unrelated timestamp updates; correcting the output
configuration permits activation again without a factory reset.

On Linux, use cgroup v2 with a writable delegated parent, enable the requested `cpu`/`memory` controllers for its children,
and set `LINEUP_CGROUP_ROOT` to that parent when `/sys/fs/cgroup` is not the delegated location. Lineup probes a temporary child
and requires writable kernel-created controller and process-membership files; it does not enable controllers or make the
container privileged. Actual worker attachment remains fail-closed if permissions change after preflight. Linux memory
limits cover the worker cgroup and descendants; its CPU quota is a percentage of one CPU. Windows uses a job CPU-rate cap
and **per-process** memory limits, not one aggregate deployment-memory cap. Job setup is probed first, and a failed process
attachment terminates the worker and reports a permanent deployment-policy error.
Externally launched workers must enforce limits in their external supervisor and leave Lineup's host CPU/memory policy at
zero, because the host does not own those processes. None of these worker policies constrain management-host FFmpeg or
the shared graph; use deployment/container limits for a deployment-wide CPU or RSS ceiling.

**Streaming > Deployment-wide plugin connections** sets `Streaming.MaximumPluginConnections` for all management-hosted and dedicated plugin outputs in one Lineup deployment. Zero is unlimited.
One TCP connection consumes one slot from its first eligible plugin request until disconnect, including idle keep-alive time; concurrent HTTP/2 requests on that connection share the slot.
Management UI/settings/status/enrollment, playback stop/session inspection and synthetic Watch Test routes are exempt, even on a connection whose plugin request was refused.
Unadmitted idle sockets and TLS handshakes do not consume this budget; it is not transport-level denial-of-service protection.
Saturation returns HTTP 429; an unavailable admission control plane returns HTTP 503. Lowering a limit does not disconnect existing clients: new connections are refused until usage falls below it.
The status API's `pluginConnections` reports authoritative usage, management/worker attribution and `isDraining` while usage exceeds a reduced limit.
Dedicated workers retain one authenticated IPC ownership channel per admitted TCP connection, outside broker-call and media-stream quotas.
Losing ownership aborts the public connection; revocation retains its slot until ownership cleanup completes. Workers never fall back to independent local limits.
Independent Lineup control planes are separate deployments, not a distributed quota cluster. Streaming schema 1 and worker IPC protocol 1 are the initial Lineup 3.0 contracts; unreleased 3.0 development changes do not introduce additional version levels. The intentional Lineup 2.0-to-3.0 factory-reset boundary remains; no migration is provided.

Worker resolution isolates malformed, invalid, unreadable, or version-incompatible package manifests and logs each rejection with
the manifest path and failure reason, allowing independent healthy workers to start. An unresolved adapter reports the rejected-package
diagnostics explicitly; duplicate valid matches and unsupported platforms remain errors. Strict full-catalog validation still fails
on any invalid package. Repair or remove rejected packages rather than treating a healthy worker's startup as proof that every package is valid.

Output reconciliation cleans up only identities owned by the configured-output supervisor, including failed activations and externally supervised outputs. Removing, disabling, or returning an output to management-hosted mode revokes that output's credentials and removes its enrollment and status without revoking the independent shared HDHomeRun discovery worker.

Dedicated-output transitions run up to four at a time, with each output serialized independently. Queued transitions use the latest
saved settings; slow starts or drains do not hold a deployment-wide liveness lock. Shutdown drains all owned outputs concurrently
within one shared 30-second grace period, or a shorter host shutdown deadline, then forces remaining owned-process cleanup.
Shorter configured per-worker drain deadlines still apply. The grace period bounds graceful waiting, not operating-system process
termination or final resource disposal; cleanup is attempted for every worker even if another worker fails to stop.

In **Settings > Virtual Devices**, a failed dedicated output exposes **Revalidate and retry worker** through the existing management
interface. It uses saved settings, never saves the editor's draft, and refuses running workers. The action reruns package, provenance,
resource-policy, and certificate checks, resets only the selected failed output's restart allowance, and preserves its prior failure
and recovery-attempt time in runtime status. Subsequent failures follow the unchanged automatic backoff and quarantine policy.
Use **Refresh** to check reported readiness and current diagnostics. External-process recovery issues a fresh enrollment; it never
launches or terminates an externally owned process. Once accepted, recovery belongs to the host and continues if the requesting
settings page is closed. Healthy siblings and the separately owned shared discovery worker are not restarted.

Managed child disposal remains mandatory when host shutdown cancels an in-progress drain or forced termination reports a failure. Cleanup terminates any remaining child before the interrupted stop finishes, without relying on later worker lease expiry; caller cancellation and stop failures still propagate after cleanup.

Orphaned broker streams are cleaned independently: a disposal failure is logged with its stream, request and virtual-device identity
without disabling later sweeps. Final broker shutdown attempts every remaining stream and reports incomplete disposals together.
Unexpected orphan-maintenance faults surface through the IPC hosted service instead of remaining hidden until shutdown.

Broker stream acquisition and media copying are canceled when their owning IPC connection disconnects, including before acceptance
or while an upstream read is idle. Cleanup releases the request's broker-call budget; a stream returned after cancellation is still disposed.

Malformed certificate references or unreadable fingerprint metadata fail only the affected output, retire its obsolete worker/enrollment,
and report a safe configuration error without exposing certificate paths in public status. Correcting configuration permits reconciliation;
independent outputs keep running.

Worker lease renewal holds the session-authority lock from authorization through lease mutation, so concurrent revocation or
replacement cannot update another activation or cause a missing-activation lookup. Old, expired, stale-generation, or
capability-denied requests retain the explicit rejection/drain response; accepted renewals extend only their owning policy lease.

External worker lease expiry is checked periodically without requiring an enrollment download. An expired registered worker becomes
failed/unavailable until re-enrollment and readiness restore it; Lineup does not launch, terminate or automatically restart the externally
owned process. Delayed registration, renewal or health status from an older generation cannot overwrite a newer generation's status.

Management-hosted Watch Test startup owns its encoder and transient files until the manifests, fragments, and codec metadata are ready and its viewer is registered. Cancellation, readiness timeout, encoder exit, and startup errors terminate the encoder and remove its files even before registration. Successful presentations remain available until their viewer stops or expires.

Both management-hosted and dedicated shared Watch startup own readiness and their bounded deadline at the producer, not at the initiating HTTP request.
Canceling or stopping one pending viewer releases only that viewer; remaining viewers still receive the ready presentation or the producer's terminal failure.
Dedicated startup preserves a known producer failure when cleanup cancels its viewers, retaining the specific process-failure or timeout response; explicit request cancellation remains cancellation.
Releasing the final viewer cancels pending work and cleans its encoder, input, and files when keep-warm is zero. Dedicated host-initiated stops are
acknowledged through worker status polling, normally within one second, without closing another viewer's shared input.
Management-hosted Dashboard and Watch stop requests dispatch observed asynchronous cleanup without blocking the interactive circuit;
service disposal joins that cleanup. Graceful CMAF shutdown releases every viewer before disposing its shared producer, including
independently admitted viewer inputs and status monitors.

Long-lived management playback ownership lives in a typed application service, not an MVC controller; dedicated playback uses a typed worker runtime.
Both reuse the same host-neutral viewer/producer coordinator and portable CMAF planning primitives. HTTP adapters only map requests, results, media files,
and public URLs. Portable FFmpeg and Web Player worker dependencies do not include Core application services, EF, or SQLite; trusted graph, settings,
guide, and subtitle-storage integration stays in `Lineup.Core.Hosting.Streaming`.

The TUI validates the complete topology before saving hosting changes. Conflicting listener bindings or invalid security and worker settings are reported without changing live settings, the settings file, or its backup. If saved topology is already invalid, the TUI shows the validation errors and offers **Configure Sources** and **Exit** instead of applying it to physical devices and guide sources. Correct the errors and return from configuration to resume the normal menu.

TUI hosting edits preserve the existing listener's client-certificate mode, forwarded-header setting, and trusted proxies, which the terminal editor does not expose. Editing a port or other hosting field does not remove these requirements. Switching to HTTP is rejected if the preserved policy requires or accepts client certificates; change that policy explicitly before selecting HTTP.

For management-hosted Lineup Link outputs, **Require HTTPS** selects which management-host transport is accepted and exported. A dedicated Lineup Link output derives this requirement from its client-facing public base URL rather than its internal listener transport. This permits an HTTPS public URL to terminate TLS at a trusted reverse proxy while the dedicated worker listens over private HTTP. The optional connection-bundle certificate pin is independent of the dedicated listener's server-certificate reference.

Configured worker CPU and memory limits use Windows Job Objects or Linux cgroup v2. Linux deployments requesting limits must grant Lineup permission to create child cgroups; activation fails explicitly rather than silently running without the requested boundary.

Managed mode starts verified packaged workers automatically. External mode creates a one-time enrollment descriptor for a local service manager; retrieve it once from the management host with `POST /api/virtual-outputs/{virtualDeviceId}/enrollment` over a loopback connection and deliver the JSON through the worker's standard input. The descriptor contains a short-lived secret and must not be logged, stored in source control, or sent over the public output network.

### Versioned status API

`GET /api/v1/status` returns schema `3.0`, a stable JSON snapshot for remote diagnostics and external dashboards. It includes the application version and runtime
identity; every non-secret Core, Web, topology, adapter, transcoding, subscription, hosting, security, worker, logging, discovery, and privacy setting; every active
physical channel with global overrides, provider metadata, guide binding, candidates, and each virtual device's resolved channel projection and conflicts; guide
coverage, per-source import metadata, automatic-fetch state, and every configured publication output; physical-device and receiver runtime state; virtual-output
profiles and desired-versus-applied worker state; active streams with source/output tracks; shared graph nodes and capacity; and the complete configured and active
end-to-end streaming-flow projection used by the Dashboard.
The endpoint is passive: polling it does not fetch guide data, discover devices, refresh tuners, retune channels, or interrupt streams. Responses use
`Cache-Control: no-store`; unavailable sections report their own status and error without hiding the remaining state.

Operational status is **eventually consistent**, not one atomic system-wide snapshot. `observation` identifies the captured Core, Web and streaming configuration
revisions, reports configuration drift during the request, and bounds each section's read with UTC start/completion times. These intervals describe observation,
not freshness of cached source data. Physical devices separately expose the last attempt and last successful status/information observations.
`guide.generation` identifies the coherent statistics read, its contributing-import revision and its streaming configuration revision.
The independently read source-import inventory has its own observation time and revision; its scope includes all stored imports, so its revision is not comparable
to the contributing-import revision. Worker, stream, graph and flow sections may change between observations; collecting status never takes a global streaming lock.

`operations` is a reason-coded summary of those existing observations plus cached topology/source-refresh outcomes; building the summary performs no extra storage
queries, program scans, disk probes or provider calls. It reports `healthy`, `pending`, `degraded` or `unknown`, with configured resource IDs/names and UTC observation
times. `topology` includes desired, attempted and last fully applied revisions. Reasons identify failed/quarantined or pending workers, excluded/failed/missing/expired
guide sources, pending/unavailable publications and reached producer/processing/plugin-connection limits. Guide expiry means committed coverage has ended,
not an invented age threshold. Pending publication means a durable retry is required, not proof the old file is current; publication time is last successful
file-write time, not a per-file guide generation. Named refresh attempts are process-local completed observations tied to the source definition revision;
after restart or definition changes, unknown attempt history is not reported as success. Observation times are not invented worker/topology transition times.
No exception text, adapter payloads, endpoints or archive contents are added to this summary. Recent unexpected stream terminal reasons remain in
`streamingGraph.recentTerminations`. None of these operational conditions change `/health/live` or `/health/ready`.

Guide publications and statistics resolve topology, bindings, authoritative programs and contributing import metadata in one deferred SQLite read transaction.
Statistics use per-authoritative-channel SQL count/min/max aggregates, preserving equivalence, manual-unavailable and empty-channel coverage semantics without
loading program JSON. Guide/watch channel lists and playback protection checks use channel-only projections; interactive program reads remain time-windowed.

The endpoint is unauthenticated and follows Lineup's trusted-network model. Open **Settings → API** to redact active-stream client addresses, device addresses,
and URLs independently. Client addresses are redacted by default; device addresses and URLs are visible by default. Secret adapter values are represented only
by their field name and whether a value is configured. Plaintext credentials, token hashes, certificate passwords, enrollment secrets, and server filesystem paths
are never exposed. Use HTTPS and authentication at a reverse proxy before allowing access from an untrusted network.

Client-address redaction also covers consumer labels and endpoint details in streaming-flow nodes and path stages. Redacted labels use `Consumer`;
session and client IDs, channel information, media diagnostics, and non-consumer labels remain available. These API privacy flags do not alter the Dashboard's flow snapshot.

```bash
curl --fail --silent http://lineup.local:8080/api/v1/status
```

A minimal Home Assistant REST sensor can poll the document without downloading the full guide:

```yaml
sensor:
  - platform: rest
    name: Lineup Active Streams
    resource: http://lineup.local:8080/api/v1/status
    value_template: "{{ value_json.streams.count }}"
    scan_interval: 30
```

### Saving and applying settings

The Settings page keeps one **Save** operation, but Core (`settings.json`) and Web (`web-settings.json`) settings commit independently.
If Core saves and Web fails, the page reports that partial outcome without rolling back committed changes. Retrying retains the committed Core baseline;
newer edits by another operator produce a conflict instead of being overwritten. Use **Undo** to reload saved settings before resolving a conflict.
Scheduling timestamps do not invalidate an open draft, and header theme changes remain synchronized without discarding other unsaved edits.
TUI topology replacement, adapter editing, and access-grant changes also reject stale configuration.
Draft revisions compare the running host's live snapshots; they do not add cross-process file locking.

The Settings page stays quiet during normal configuration application. Failed or partial application displays a **Configuration status** warning
with the affected devices or outputs and **Retry**; revision hashes are not displayed in the normal settings view.
This operation covers physical lineup refresh and cached guide publication; dedicated output-worker health remains a separate runtime concern.
Timestamp, schedule, retention, and other non-topology updates do not refresh lineups or republish guides. **Retry** reapplies the desired topology
without rewriting settings. Outcomes are retained for the process lifetime; startup reapplies the persisted desired topology.
Failed publication preserves the previous output file, and initial setup reports success only after its required connectivity and application checks finish.

The Settings **Reset** tab can stage default settings for review or perform a Factory Reset. Factory Reset removes all files and directories beneath Lineup's application-data and transient-data roots, along with the configured XMLTV output, during a graceful restart. The browser displays a blocking restart screen and returns to first-run setup after detecting the new Lineup instance. XMLTV files configured outside Lineup-owned storage are preserved. A service supervisor such as Docker's `restart: unless-stopped` must restart the process after reset.

Lineup 3.0 uses application-settings schema `1`, streaming-topology schema `1`, database schema `1`, and Web-settings schema `1` as an intentional factory-reset cutover. Persisted state must match these versions exactly; an older, newer, invalid, or unversioned artifact enters the explicit factory-reset-required startup state before current state is created. No settings, topology, database, browser-profile, XMLTV, or Lineup Link token migration runs. Worker packages require IPC protocol `1`; update managed and external worker packages together with the trusted host.

Web startup validates the explicitly declared schema in `web-settings.json` before applying its preferences, including logging settings.
If the primary Web document cannot be loaded, a valid `web-settings.json.bak` can recover it; the first successful save preserves that backup rather than replacing it with the rejected primary.
If neither existing Web document is valid, normal workflows and Web-settings writes remain blocked until explicit recovery; the existing files are not silently replaced with defaults.

The Web `SettingsSaveCommand` accepts raw settings drafts and expected Core/Web edit revisions independently of the Blazor editor.
It owns topology/field validation, conflict checks inside serialized updates, independent Core-then-Web commits, and retryable logging application outcomes.
Results explicitly retain a committed Core revision when the Web write fails; this is not a two-file transaction. Setup connectivity/topology verification,
one-time secret display, notifications, browser theme application and navigation remain presentation-specific steps after persistence.
Independently synchronized header-theme changes are preserved during a queued save. This boundary does not introduce a command bus or a new remote mutation endpoint.

For a new or empty SQLite database, Lineup commits the channel and guide tables together with the required schema-version metadata in one transaction. Failed, canceled, or interrupted first-time initialization rolls back for a later startup to retry. Existing populated databases with missing or incompatible metadata remain rejected; initialization does not repair, migrate, or delete them.

Lineup-owned persistent data defaults to `/appdata`. The optional `Lineup:AppDataPath` setting overrides that root for custom deployments. This data includes settings and backups, the authoritative SQLite channel and guide database, rolling logs, ASP.NET Core Data Protection keys, and the restart-safe Factory Reset marker.

Transient CMAF fragments and subtitle sidecars default to `/transient`. When `Lineup:AppDataPath` is configured but `Lineup:TransientPath` is not, Lineup uses a `lineup` directory beneath the operating system's temporary path to keep disposable stream artifacts outside persistent application data. The optional `Lineup:TransientPath` setting (environment variable `Lineup__TransientPath`) overrides either default. The Compose files mount a `transient` named volume at `/transient`, matching the `appdata` and `guide` volume conventions. Custom deployments can replace that named-volume mount with a memory-backed bind mount or Compose `tmpfs`, or with disk-backed storage when transient data must not consume system memory.

Lineup routes application-owned files through explicit storage boundaries. `AppDataStore` owns persistent settings, database, reset, and log artifacts; `TransientDataStore` owns disposable CMAF and subtitle workspaces; and `XmltvPublicationStore` owns the configured public XMLTV destination, including destinations outside app-data. Browser preferences remain client-local through `BrowserDataStore`. Framework and process infrastructure such as SQLite, Serilog sinks, Data Protection, and FFmpeg continue to receive managed paths and perform their own I/O.

ASP.NET Core Data Protection keys are stored in the `data-protection-keys` directory beneath the app-data root. Persisting this directory through the Docker `/appdata` volume keeps antiforgery tokens and other protected browser state readable after a container recreation. Factory Reset deletes the key ring so previously protected state is intentionally invalidated.

### Operational health and stream recovery

`GET /health/live` returns HTTP 200 when the management HTTP pipeline is alive, including factory-reset recovery mode.
It does not query SQLite, build a status document, or contact tuner hardware. Both Compose deployments use this endpoint for their container health check;
an unhealthy Docker health status alone does not restart a container.

`GET /health/ready` returns HTTP 200 when management is usable, including first-run setup and external tuner outages.
It returns HTTP 503 while starting, stopping, or awaiting a required factory reset. Its small, non-cacheable response reports cached device-failure count
and age of the last successful guide fetch when known. Unknown guide age is null, not zero. Cached failures are reported as `degraded` without making the process
unready: do not restart Lineup because an external device is offline. Use readiness for traffic routing and liveness for process supervision.

Established streams are not spliced onto arbitrary alternate sources. Unexpected source EOF or failure terminates the current producer and its subscribers;
MPEG-TS clients receive the remaining media followed by EOF/disconnection, and browser playback can become unavailable or report a playback error.
Live Watch automatically reconnects after explicitly classified source EOF/failure or recoverable dedicated-worker loss, but only after playback has initialized.
It creates at most three fresh, independently owned sessions, re-resolving current routes after delays of 1, 2, and 4 seconds within one 60-second recovery deadline.
Brief recoveries do not replenish the budget: playback must remain healthy for 60 uninterrupted seconds, excluding paused/waiting/stalled playback.
The page displays **Reconnecting** and retains Stop, stream/profile overrides, audio/subtitle selections, volume and mute.
Stop, operator stop, navigation, disposal and a newer tune cancel recovery; late sessions are released rather than accepted.
Unknown sessions, arbitrary HTTP/network/player errors, protected/disabled/invalid requests, permanent configuration failures, and quarantined workers are not automatically retried.
Recovery can follow normal worker supervision but never explicitly restarts a failed worker or bypasses its restart policy. A failed retry continues only for explicitly transient source failures.
Passive per-viewer status does not renew inactivity leases; source-loss outcomes retain at most 2,048 aliases for two minutes and are forgotten on shutdown.
Watch Test and external MPEG-TS clients retain manual reconnect behavior. Seamless codec/timestamp/track continuity is not promised.
Stopping one viewer still leaves a shared producer running for other viewers.
The status API's `streamingGraph.recentTerminations` retains the last 64 unexpected producer completions with `source_eof` or `source_failure`,
producer identity, observation time and originating correlation ID. Intentional viewer stops and shutdown cancellation are not source-loss events.

Lineup exposes standard .NET meters `Lineup`, `Lineup.Streaming`, and `Lineup.Host`, plus activity source `Lineup`.
Measurements include operation duration/count by fixed stage and outcome, candidate acquisition/fallback, pending admission and rejection,
active/warm graph and CMAF producers, unexpected producer completion, worker restart attempts, retained buffering/eviction and cached guide age.
Graph stages and CMAF outputs are distinct resources, not interchangeable viewer counts. Unknown guide age emits no measurement.
Meter labels never contain viewer/channel IDs, addresses, tokens, URLs or raw failure text.
Use a .NET diagnostic collector or an explicitly configured metrics/tracing integration to subscribe; the existing OTLP **logging** configuration
does not automatically enable metrics or trace export.

API responses include `X-Correlation-ID`. Request log scopes, acquisition/media-readiness/producer activities and local broker request envelopes
preserve W3C trace identity; IPC carries only `traceparent`, not baggage or credentials. Active-stream status includes its correlation ID alongside existing
broker request, worker media-session and producer identities. Worker activation activities include the generation as a trace attribute, never as a metric label.
A reused producer retains its original trace; later viewers have independent request traces and are joined through producer/output identities, not falsely treated
as new producer starts.

### Offline configuration export and recovery

Both Web and TUI executables accept `--recovery backup|export|preview|preflight|restore <path>` **before** normal application startup.
Stop all Lineup processes and supervisors using the data directory first. Use the exact same release/build for backup and restore, and configure the data root
through `Lineup__AppDataPath` (Web) or `Lineup__TuiAppDataPath` (TUI). Recovery does not support a TUI database override outside that root.
The updated hosts and recovery commands retain a sibling `.NAME.lineup.lock` file to exclude concurrent owners.
The parent directory must be writable and paths must not use symbolic links/reparse points. The lock is outside factory-reset-owned contents;
do not remove it while a process owns it. Older versions, other containers/hosts with different filesystem views, and external database tools are not coordinated;
all writers must be stopped.

For example, from a stopped Windows Web installation (use `Lineup.App.Tui.dll` and its data-root variable for the TUI):

```powershell
$env:Lineup__AppDataPath = 'E:\Lineup\data'
dotnet Lineup.App.Web.dll --recovery export 'E:\Backups\lineup-portable.json'
dotnet Lineup.App.Web.dll --recovery backup 'E:\Backups\lineup-sensitive.zip'
dotnet Lineup.App.Web.dll --recovery preview 'E:\Backups\lineup-sensitive.zip'
$env:Lineup__AppDataPath = 'E:\Lineup\restored'
dotnet Lineup.App.Web.dll --recovery preflight 'E:\Backups\lineup-sensitive.zip'
dotnet Lineup.App.Web.dll --recovery restore 'E:\Backups\lineup-sensitive.zip'
```

Create the destination's parent directory first. Existing export/backup files are never overwritten.
Portable export is an allowlisted configuration starter: it retains topology identities, subscriptions and media preferences but clears all opaque adapter payloads,
hosting/security policies and grants, disables adapters, and omits keys, database contents, proxy addresses and deployment-specific destinations.
It is **not** a recovery archive or an automatic import workflow. Recreate adapter connections/authorization and review enabled outputs through normal setup;
operator-assigned names and topology identifiers remain present, so it is not an anonymized support bundle.

Sensitive recovery archives include primary Core/Web settings, saved adapter credentials/grants, SQLite channel identities/bindings/guide data,
and Data Protection XML keys. They are **not encrypted**: store them with operator-only access and encrypt/protect transport and storage outside Lineup.
Unix archive/staging files are owner-only; Windows uses inherited filesystem ACLs, so select a private directory.
Logs, settings fallback files, reset markers, transient media, generated guide files, external certificates and environment/platform secrets are excluded.
Certificate references remain in sensitive settings; provision external certificate files/passwords and host configuration separately.
Platform-protected Data Protection keys can still require the original OS account/machine; the archive cannot defeat external key encryption.
Save artifacts outside application-data, transient and generated-output/reset-owned directories, including their parent directories;
the command rejects paths inside its own app-data root, but cannot infer every externally managed cleanup location.

Backup uses SQLite's online backup API (including committed WAL data), not a live database-file copy; offline ownership keeps settings and database stable together.
Preview validates the entire archive's release, inventory, SHA-256 contents, paths, expansion limits, schemas and SQLite integrity before returning file names.
Hashes detect corruption, not authenticity: restore only trusted archives. The supported limit is two GiB of expanded data and 1,024 entries.
Incomplete writes use unique `.pending` names and are published only after closing/flushing the complete archive.

Restore validates in a private sibling staging directory and installs it by directory rename only into an absent or empty target.
It never merges with or replaces populated state; a failed preview/restore does not install partial application files.
Restore into a **new directory**, then point the stopped deployment at it after review. Mounted volume roots cannot be replaced atomically:
for containers, restore into a new subdirectory such as `/appdata/restored` and update `Lineup__AppDataPath` accordingly.
Do not delete the old deployment or perform Factory Reset until recovery has been verified.
The existing exact-schema/reset boundary remains unchanged; no Lineup 2.0 migration is introduced.

Preflight runs the same complete archive validation and verifies that the selected target is absent or empty, without installing files there.
It uses and removes private sibling staging, retains the ownership lock file, and requires a writable parent with enough space for the expanded archive.
Its JSON reports the exact producing informational release string and file inventory, plus **unverified** external deployment requirements.
Exit 0 means these archive/target checks passed, not that certificates, OS-protected keys, environment, disk capacity at a later time, or network outputs are ready.
Invalid archives, nonempty targets and ownership failures return exit 2. Cancellation is not reported as success.
Preflight is not a reservation or authorization to replace existing data: restore reacquires ownership and repeats validation and the empty-target checks.

For a container deployment, stop the existing container and its automatic restart policy first. Pin the **same producing image/build**, not a moving tag.
For example, from PowerShell, replace the image digest and the private host directories before running:

```powershell
$image = 'ghcr.io/am385/lineup@sha256:<exact-producing-image-digest>'
docker run --rm --network none --mount 'type=bind,source=E:\Lineup\data,target=/appdata' --mount 'type=bind,source=E:\Backups,target=/backups,readonly' `
  -e Lineup__AppDataPath=/appdata/restored $image --recovery preflight /backups/lineup-sensitive.zip
docker run --rm --network none --mount 'type=bind,source=E:\Lineup\data,target=/appdata' --mount 'type=bind,source=E:\Backups,target=/backups,readonly' `
  -e Lineup__AppDataPath=/appdata/restored $image --recovery restore /backups/lineup-sensitive.zip
```

After either Windows or container restore, review private ACLs/volume ownership, restore external certificates/passwords and deployment configuration,
and configure the stopped deployment to use the new root (`/appdata/restored` in this example). Start only that deployment and verify setup, source connectivity,
channel identities/bindings, guide coverage/publications and each enabled output through normal controls and `/api/v1/status`.
Check management liveness/readiness separately from operational conditions. Keep the original root and protected backup until that verification succeeds;
rollback means stopping the restored deployment before pointing the original producing build back to the untouched original root.

### Reverse proxy TLS termination

A reverse proxy can terminate public HTTPS and forward requests to Lineup over private HTTP. Lineup ignores forwarded request headers by default. Add the immediate proxy IP address or CIDR network under **Settings → General → Network Settings → Known Proxies** only when the shared management listener is behind that proxy. The persisted list applies to the Management UI, APIs, and management-hosted virtual outputs. An empty list disables forwarded request metadata.

Changing Known Proxies gracefully restarts Lineup so the management request pipeline can apply the new trust boundary. Docker images and prebuilt packages use the same persisted setting in `/appdata/settings.json` or `%LOCALAPPDATA%\Lineup\data\settings.json`; no environment-specific proxy configuration is required.

The proxy must preserve the original `Host` header and provide `X-Forwarded-For` and `X-Forwarded-Proto`. Lineup processes one trusted proxy hop and ignores `X-Forwarded-Host`. For Nginx:

```nginx
location / {
    proxy_pass http://lineup:8080;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
}
```

Caddy, Traefik, and other proxies normally preserve or forward the equivalent metadata automatically, but their immediate connection address must still match a configured Known Proxies entry. Do not trust an unrestricted network or expose the internal Lineup listener directly when forwarded headers are enabled. Trusted metadata lets Lineup enforce Lineup Link HTTPS requirements, advertise correct public HDHomeRun URLs, attribute streams to the original client, and preserve loopback-only worker enrollment.

Dedicated-listener virtual outputs are separate HTTP servers and do not inherit the management listener's Known Proxies. Configure **Honor Forwarded Headers** and **Trusted Proxies** in that virtual output's advanced listener security settings only when the dedicated listener is itself behind a reverse proxy. For proxy TLS termination, select HTTP as the internal listener transport, override the client-facing base URL with the public HTTPS root URL, and trust the immediate proxy address observed by the worker. Lineup Link then requires HTTPS from clients while accepting the proxy's private HTTP connection only when its trusted `X-Forwarded-Proto` value is `https`; direct unproxied HTTP remains rejected. Saving a dedicated policy recycles only the affected isolated worker; the main Lineup host and other outputs remain running. Direct HTTP or HTTPS listeners should leave forwarded headers disabled.

### HTTP and optional HTTPS

Lineup always listens for HTTP on `Lineup:HttpPort` (8080 by default). HTTPS is optional and is enabled only when `Lineup:Https:CertificatePath` points to a readable, currently valid PFX containing a private key. Certificate provisioning remains outside the unauthenticated Settings UI so private keys and passwords are not persisted to `settings.json` or exposed through the browser.

For Docker Compose, name the certificate `lineup.pfx`, place it in the local `./certs` directory, and provide its password at startup:

```bash
CERT_PASSWORD=****** docker compose -f docker-compose.prod.yml up -d
```

Use deployment-platform secrets rather than source-controlled environment files for the password. The production Compose file mounts `./certs` at `/app/certs:ro` and configures Lineup to load `/app/certs/lineup.pfx`. It publishes container port `8443` on the host-facing `HTTPS_PORT` (8443 by default). Similarly, `HTTP_PORT` selects the host-facing port mapped to fixed container port `8080`. `HDHOMERUN_DISCOVERY_PORT` and `SSDP_PORT` select the host-facing UDP ports mapped to fixed container ports `65001/udp` and `1900/udp`; changing these defaults can prevent clients from discovering Lineup automatically. The normal `docker-compose.yml` remains HTTP-only.

Outside the production Compose deployment, HTTPS remains optional and is enabled by setting `Lineup:Https:CertificatePath`. If the configured certificate is missing, inaccessible, invalid, missing its private key, expired, or not yet valid, Lineup logs a warning without exposing the password and continues with HTTP only. The corresponding environment variables are `Lineup__Https__CertificatePath` and `Lineup__Https__CertificatePassword`. Certificate and endpoint changes require a Lineup restart. HTTP remains intentionally available when HTTPS is active; Lineup does not force HTTPS redirection.

### Logging

Console logging is always enabled and remains available through `docker logs lineup` or the host service manager. After initial setup, the **Logs** page shows a bounded, redacted view of recent structured events from the current process. It supports severity, category, and text filters plus pause and manual refresh controls. Because Lineup currently assumes a trusted local network, the Logs page is unauthenticated and may expose operational details such as device addresses; do not expose it to untrusted clients.

Guide refresh failures retain the underlying exception in source-correlated warning logs while returning a sanitized failure result to callers. Topology application also logs each failed guide-output publication by output identity; physical streaming remains available when guide publication fails.

Lineup uses the standard .NET `Logging:LogLevel` configuration for its startup default and category-prefix filters. **Settings → Logging → Override Logging Defaults** can replace that complete filter set at runtime. The editor starts with categories discovered from the effective startup configuration, including environment overrides, and supports additional custom prefixes. Exact categories and dotted descendants match, with the longest prefix taking precedence. Disabling the override immediately restores the startup filter set. The application filter applies to every output before any sink-specific minimum.

Open **Settings → Logging** to enable rolling files. Files are written in the application-owned `logs` directory beneath `Lineup:AppDataPath`. The minimum level, retention period, and per-file size limit are configurable. Enabling, disabling, or changing these options rebuilds only the managed file sink and takes effect immediately when settings are saved; console, in-memory, and external logging continue uninterrupted. **Delete Logs** removes all existing rolling files and safely starts a fresh file when logging is active. Factory Reset deletes this application-owned directory. The Logs page lists and downloads only retained `lineup-*.log` files from that directory.

OTLP export is optional and configured only through startup configuration or environment variables. Authentication headers are never persisted to `settings.json` or returned to the browser. The Logging Settings tab reports only whether the exporter is enabled, disabled, or invalid.

```bash
# OTLP collector (HTTP/protobuf is the default; use "grpc" when required)
Lineup__Logging__OpenTelemetry__Endpoint=http://otel-collector:4318
Lineup__Logging__OpenTelemetry__Protocol=httpProtobuf
Lineup__Logging__OpenTelemetry__Headers__Authorization=Bearer your-token
```

Route OTLP from Lineup directly to a compatible backend or through an OpenTelemetry Collector that exports to services such as Seq or Azure Monitor. External delivery uses background transport so an unavailable collector does not block tuner streaming or background refreshes.

To test OTLP locally without an observability backend, start Lineup with the included debug collector override:

```bash
docker compose -f docker-compose.yml -f docker-compose.otel.yml up -d
docker logs -f lineup-otel-collector
```

The collector prints decoded Lineup log records to its console. Stop the test collector and return to the standard local Compose configuration with:

```bash
docker compose -f docker-compose.yml -f docker-compose.otel.yml down
docker compose up -d
```

### Physical and virtual tuner topology

Open **Settings → Physical Devices**, **Guide Sources**, and **Virtual Devices** to configure the input, guide, and output topology. A virtual HDHomeRun device
subscribes to every enabled physical device by default, or can select a subset. Its advertised tuner count is the sum of subscribed finite physical
capacities, subject to its optional cap.

Dedicated HDHomeRun listeners retrieve the current manifest for each `/discover.json` request, so changes to subscribed physical capacity are reflected without restarting the output worker. Clearing the optional tuner-count cap restores aggregate advertised capacity, bounded by the protocol maximum of 255. Clearing an explicit Device ID restores the stable checksum-valid identity derived from that virtual device.

The shared UDP/SSDP discovery supervisor also checks current physical-capacity snapshots every five seconds, including after initial activation and later capacity changes without a settings edit. It replaces the discovery worker only when the effective advertisements or worker policy change; unchanged or unsubscribed capacity does not cause a restart.

Lineup retries incomplete physical-lineup initialization on a later tune for a missing source, without requiring a settings save, including when status polling has already recovered device activation. Already-active sources with committed channels remain routable without awaiting a stalled sibling retry, including a retry running for another viewer. Background device refresh continues recovery attempts. Unchanged healthy runtime devices are retained across partial-activation retries; changed, disabled, or removed definitions are still reconciled. Fully successful initialization is reused.

The Dashboard and runtime status API report every enabled physical device, including failed activations. An inactive device has a safe activation error and no receiver-control or restart actions. Its last successful status and capacity are retained when the adapter kind is unchanged; without a prior snapshot, capacity is zero and no observations are fabricated. Successful refresh clears the error, and disabled or removed devices are excluded.

Hosted physical activation and lineup publication are fenced against successfully saved settings. A tune or refresh that captured older
settings cannot replace a newer catalog or retire its active streams; obsolete tune resolutions restart against current settings.
Settings saves wait for an already admitted catalog or lineup publication to finish before superseding its configuration.

HDHomeRun status and control initialization reuse the successful activation discovery result instead of fetching device discovery a second time.
Explicit device-information refreshes and fresh guide authorization requests still query the device; failed activations remain eligible for retry.

Physical-device activation, lineup, status and information requests share a bounded host-wide network allowance. The defaults are four concurrent operations and
a ten-second deadline per admitted operation, configured at startup using `PhysicalDeviceRefresh:MaximumConcurrentOperations` and
`PhysicalDeviceRefresh:OperationTimeoutSeconds` (environment variables use `__` instead of `:`). Valid ranges are 1-1,024 concurrent operations and 1-300 seconds;
invalid startup values are rejected. Waiting for admission does not consume the network deadline.
Adapters must honor cancellation; an uncooperative adapter retains its slot until it exits rather than permitting unbounded abandoned work.
Equivalent in-flight refresh requests coalesce; canceling one caller does not cancel remaining callers. Healthy lineup/status results publish independently,
failed attempts retain prior successful observations, and obsolete configuration/runtime results cannot overwrite current state.
Catalog replacement and retirement remain atomic; initial activation therefore completes before refresh publication starts, and retirement cleanup retains its
existing non-cancelable ownership boundary.

For management-hosted virtual HDHomeRun devices, the first enabled device is available at the root routes:

- `/discover.json`
- `/lineup.json`
- `/lineup.xml`
- `/lineup.m3u`
- `/device.xml`
- `/auto/v{channel}`

Every management-hosted device also has stable scoped routes under `/hdhomerun/{virtualDeviceId}/`. A dedicated virtual HDHomeRun listener exposes only its
device at the listener root. Device IDs are stable checksum-valid HDHomeRun identifiers. The Settings page displays copyable manual setup URLs and the built-in XMLTV guide URL at `/api/xmltv`.

Each named SiliconDust guide source subscribes to every compatible enabled HDHomeRun by default, or to a selected subset. For every request, Lineup retrieves
fresh `DeviceAuth` values in deterministic physical-device order and concatenates them without separators for one combined SiliconDust XMLTV request.
Authorization is never persisted or logged. Imports are isolated and transactional per guide source. A physical source channel binds to at most one
authoritative guide channel from a source currently subscribed to its device. Lineup automatically uses a unique provider-key match, then a unique original
provider-number match; global channel number overrides do not affect guide matching. Distinct XMLTV station identities that share a number remain separate
candidates, so number conflicts require an explicit selection on the Channels page instead of being silently merged. Manual selections override automatic
matching and remain unresolved if the selected guide channel disappears or its source stops subscribing to that device until the selection is changed or cleared. Use
**Refresh Channels** on the Dashboard to update the persisted tuner lineup independently, or enable the guide-fetch option that refreshes channels first.
A guide fetch refreshes only the guide sources enabled in the saved streaming topology; disabled sources are not queried. It applies the saved
channel snapshot without otherwise querying the tuners. Past programs are retained for 24 hours by default, configurable under
**Settings → Guide Sources**. Channels disabled on the Channels page remain visible in Lineup's Channels and Guide
pages, but are excluded from published XMLTV and virtual JSON, XML, and M3U lineups. Their MPEG-TS and Watch URLs return an error by default or a
synthetic slate when configured. Global channel number/name overrides resolve conflicts once at the physical source, so every subscribed virtual output,
guide view, and XMLTV publication uses the same canonical identity. Equivalent source groups provide ordered physical failover.

On **Channels**, open **Same-channel sources** to preview saved number conflicts, preferred metadata, authoritative guide selection, and ordered
source candidates for the global Guide/Watch projection and each enabled virtual output. Output previews apply adapter compatibility, subscriptions,
and enabled states; they are not live-health checks. Matching numbers or names never automatically establish equivalent content.
Explicitly confirm **Treat as the same channel** to create a group, or open a group member to add ungrouped sources with the same saved global number.
Numbers must be changed and saved separately; remove a source from its group before renumbering. Existing groups cannot be merged.
Groups are labeled with effective channel identities, not editable GUIDs. Membership changes are atomic, reject stale previews, and preserve
device-wide priority and individual guide bindings. Removing a member requires confirmation and leaves the other members unchanged.
Failover is attempted at stream startup, not by splicing an active stream. All enabled guide outputs are republished through the normal publication path;
publication failure is reported separately from the already committed edit, while the previous successful publication remains available.

Successful guide imports update matching programs and remove missing current or future programs within the source's reported coverage window.
Retained ended programs and future coverage outside that window survive narrower refreshes. Expired history is pruned according to the configured
retention policy, including expired programs sent again by the source. Channel changes, program merging, pruning, and last-import metadata commit
together for that source without changing another source's data.

The Web host also prunes expired ended programs across **all** cached sources at startup and hourly, even with automatic fetching disabled or failing.
This independent maintenance never removes future programs, guide channels, manual bindings, or import metadata, including data belonging to disabled or removed sources.
It republishes enabled guide outputs from cache without contacting providers. The TUI retains import-time pruning and manual refresh; it does not start this Web maintenance worker.

An enabled guide source that cannot activate still contributes its committed cache to virtual guides when its configured subscription is compatible.
The refresh reports the activation failure; disabling or removing the source, or changing its subscription, immediately restricts that cached projection.
Recovering the source updates its committed cache on the next successful refresh.

Publishing cached XMLTV or JSON outputs, including on-demand guide downloads and topology-change publication, does not construct or refresh guide-source runtimes.
It validates the current topology and applies the same source eligibility and output selections used by guide queries. Explicit and scheduled refreshes still activate sources;
refresh-and-publish refreshes once before publishing the resulting committed cache.

Channel identity, enablement, and manual guide-binding changes invalidate and republish every enabled guide output, including custom-only JSON or XMLTV outputs
that have never been published. Invalidation is persisted before committing the channel change. Failed publications preserve the previous file and retry from
committed cache after one hour, across restarts and even when automatic source fetching is disabled. Partial publications also remain pending.
An independent retry worker checks due publications every 30 seconds, so a stalled source download cannot delay cached-output recovery.
Cached publication is not blocked by a source's network refresh, and refresh completion publishes the latest saved topology rather than an obsolete captured configuration.

Guide queries isolate sources with unavailable adapters, invalid physical-device subscriptions, or no compatible enabled devices.
They return the remaining eligible data with structured source diagnostics (`AdapterUnavailable`, `InvalidSubscription`, or `NoCompatibleDevices`),
including when every source is excluded. Ineligible cached imports remain stored but are not projected. Disabled and unselected sources do not degrade an output.
The Guide and Dashboard display excluded sources and actionable warnings; `/api/status` reports the guide as `degraded` with `sourceDiagnostics`
alongside the remaining counts and coverage. Publication results distinguish successfully written partial guides from complete guides, and the TUI warns accordingly.
XMLTV and JSON guide document formats are unchanged. Cancellation, storage failures, and unexpected provider errors remain failures rather than partial-query success.

Automatic guide refresh persists an independent schedule for each source. Successful SiliconDust refreshes retain the required randomized 20-28-hour cadence;
Lineup Link uses the configured positive `AutoFetchInterval` (24 hours by default), which unrelated Settings saves preserve.
Only due sources are fetched, with at most four concurrent source refreshes. Each failed source retries after one hour without shortening successful sources' schedules.
Source schedules are saved before output publication, so a failed publication retries cached data rather than refetching healthy providers.
Failed or partial publications remain observable failures and preserve the last fully successful automatic-refresh timestamp.
Operational source schedules and output retries do not invalidate open Settings drafts or increment persisted schema versions.

### Lineup Link

A provider instance can add a **Lineup Link** virtual device and export its published lineup, optional guide, and post-output-transcode MPEG-TS streams through `/api/lineup-link/v2/virtual-devices/{virtualDeviceId}` when management-hosted or the equivalent path on its dedicated listener. A consuming instance imports the generated connection bundle into a **Lineup Link** physical device and may add a separately managed Lineup Link guide source. The virtual output uses Lineup's existing shared acquisition and MPEG-TS transformation graph, so consumers requesting the same effective source and output profile reuse the physical stream, physical normalization, and virtual transform.

A Lineup Link guide source subscribed to multiple remote outputs merges their root supplemental metadata into one envelope before import. The first subscribed output wins conflicting root attributes with the same qualified name; all preserved root elements remain in subscription order. Channel and program metadata retain their remote-output-qualified identities. Invalid root envelopes fail the refresh without replacing the previously committed guide, rather than deferring failure until XMLTV or JSON publication.

Management and dedicated Lineup Link guide exports use the same mapping and merge contributing root metadata into one valid envelope. The first contributing source wins conflicting qualified root attributes, all preserved root elements retain source order, and channel/program metadata remains intact. Zero envelopes produce no root metadata; multi-source exports can be imported by another Lineup instance without concatenated XML roots.

Guide exchange supports at most **2,000 channels, 100,000 programs, and 128 MiB of UTF-8 JSON per response**.
The byte limit includes the IPC envelope where applicable; metadata size and transport representation determine how much coverage fits.
These are transfer safeguards, not limits on the local guide database or file publications. Management and dedicated Lineup Link exports
preflight the complete response and return HTTP 503 with `guide_limit` rather than truncate an oversized schedule.
Dedicated guide IPC retains its existing length-prefixed JSON shape but serializes and reads directly from the stream instead of
building a second JSON document and full byte buffer. Each response uses one coherent database/configuration generation; there is no paging
or mixing of generations. Original snapshot objects and imported guide objects still consume memory proportional to the accepted guide.

Lineup Link ingestion checks declared and actual body bytes, including chunked responses, and counts channels/programs incrementally
before accepting the guide. Oversized, malformed, truncated, or canceled responses fail the refresh without replacing the committed guide.
Manifest/lineup bodies are limited to 1 MiB and error bodies to 64 KiB; the existing 30-second control deadline and live-stream behavior are unchanged.
Reduce the output's selected channels, source guide coverage, or metadata size when a guide exceeds the transfer envelope.

Lineup Link virtual devices cannot subscribe to Lineup Link physical devices, preventing recursive links. Under the Web host, each output can reuse the management host's ports or run in its own dedicated worker. The TUI can configure and supervise dedicated output workers but does not expose the Web management routes. New outputs start with no access grants, so every remote request is denied until a user explicitly creates one named grant per remote consumer and copies or exports its one-time connection bundle. Rotate only the affected grant when its raw token is unavailable or may have been exposed, or revoke it without interrupting other consumers on the same virtual device. Revoked grants may be permanently purged after confirmation when their retained identity and lifecycle history are no longer needed, including the final grant to return the output to its deny-all state.

Managed dedicated-output workers must complete bootstrap delivery and register before their one-time credential expires (one minute by default).
The supervisor checks managed-worker liveness every second. A missed registration deadline or an inactive/expired worker session revokes the
worker's access and forcibly stops the process, even when it has not exited on its own. Recovery uses the configured restart window, backoff,
and maximum restart count; an exhausted worker remains failed until its configuration changes, it is disabled and re-enabled, or
an operator explicitly revalidates and retries that failed output from Settings.
Healthy lease renewals do not recycle the worker. External-process outputs remain the responsibility of their external process supervisor.

Lineup stores the fields it queries as normalized SQLite values and preserves additional root, channel, and program XMLTV metadata alongside them.
Published XMLTV retains provider source provenance, localized and repeated values, official optional fields, and provider extensions while identifying
Lineup as the document generator. The Guide program-details dialog projects recognized official program metadata into typed ratings, credits,
languages, identifiers, status, and advanced technical details. Raw supplemental XML and uninterpreted provider extensions remain internal.
XMLTV publications are indented to preserve program artwork in Jellyfin readers that incorrectly skip the node following language metadata.

Lineup's Core stream graph atomically limits each physical device to its discovered capacity. Receivers of an identical physical acquisition share one
tuner slot; equivalent physical and virtual transforms are also reference-counted. Different acquisitions consume separate slots. CMAF viewers receive
independent aliases to a shared output, so stopping one browser releases only its reference and final release stops the process and removes artifacts.
The host-wide stream limit also applies to synthetic DRM and disabled-channel slates even though those slates do not consume physical tuner capacity.

A reconnect to a draining shared stream waits for its physical and transform cleanup before creating a replacement; canceling that wait
does not interrupt the owned cleanup. Draining acquisitions retain their tuner slots until source disposal completes, so rapid stop/reconnect
cannot overlap same-channel physical opens or bypass capacity limits.

Graph shutdown normally drains transforms before stopping physical acquisitions. Cancellation bounds that wait but still signals
every acquisition to stop; graph-owned cleanup continues and retains capacity until the owned source is disposed.

Shared FFmpeg transforms, MPEG-TS slates, and management/dedicated caption processes continuously drain diagnostics while retaining only the last 16,384 characters and at most fifty non-empty lines. Long warning lines cannot accumulate lifetime stderr in the trusted host; nonzero exits retain their exit code and recent diagnostic tail.

Network discovery is disabled by default to avoid UDP port conflicts. To enable it, turn on **Enable UDP and SSDP network discovery**. A management-hosted
virtual HDHomeRun requires an absolute HTTP or HTTPS **Advertised base URL** reachable by media servers. A dedicated listener advertises its effective
**Client-facing base URL**, which Lineup derives from transport, bind address, listener port, and optional published-port mapping unless an advanced manual
override is configured. Request-derived and container-internal addresses are not substituted automatically.

The first enabled management-hosted virtual HDHomeRun owns the management listener's root protocol URLs. Other management-hosted devices
advertise `hdhomerun/{DeviceID}/` beneath their configured public root, so UDP discovery, SSDP device descriptions, and HTTP lineups identify
the same device. Dedicated outputs advertise their own listener base URLs and do not change which management-hosted device owns the root.

Jellyfin commonly identifies auto-discovered tuners by the source network address. Advertising several virtual devices from one process may therefore
require a distinct host IP for each advertised device; otherwise add the displayed tuner URLs manually. Docker bridge networks frequently do not pass
UDP broadcast or multicast traffic. Use host networking where the platform supports it, or use manual setup.

SSDP uses a shared IPv4 binding on UDP port 1900 and joins the default interface's SSDP multicast group, permitting coexistence with other shared
listeners such as the Windows **SSDP Discovery** service (`SSDPSRV`); disabling that service is not required by Lineup's policy. On Windows,
multicast loopback is enabled so clients on the same machine can discover Lineup. Notifications, responses, malformed searches, and unsupported
search targets are ignored before consuming the response-rate budget. Shared binding does not guarantee delivery of unicast searches to every
listener, and a successful bind alone does not certify remote-LAN delivery, firewall rules, or all network interfaces. Use multicast discovery or
the manual profile URLs shown in Settings. Startup logs identify the bound SSDP socket and multicast-loopback policy; socket failures identify
UDP port 1900, the socket error, and relevant ownership, permissions, or multicast-interface checks.

An SSDP socket failure during binding, multicast setup, notification delivery, or receive processing disables SSDP for that discovery-worker
generation, reports degraded readiness, and logs the unavailable protocol. SiliconDust UDP discovery and control-plane lease renewal continue.
If UDP discovery or the lease ends, the worker cancels and cleans up all remaining discovery tasks. SSDP receive and alive-notification loops
also cancel and observe one another before their shared socket is disposed.

## Tech Stack

- **.NET 10** — target framework for all projects
- **Blazor Server** — interactive web UI
- **Entity Framework Core + SQLite** — EPG data caching
- **Spectre.Console** — terminal UI rendering
- **Serilog** — structured logging
- **Jellyfin FFmpeg** — live TV stream transcoding with AC-4 decoding

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE).
