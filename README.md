# Lineup

**Lineup** is a .NET application that downloads official HDHomeRun [XMLTV](http://wiki.xmltv.org/index.php/XMLTVFormat) guide data and proxies network TV tuner streams for media servers like **Jellyfin** and **Plex**.

It includes a web-based dashboard, a terminal UI, live TV streaming with transcoding, and automatic XMLTV refreshes.

> Currently this is only supporting the [HDHomeRun](https://www.silicondust.com/) Tuners like the HDHomeRun FLEX 4K. Hopefully more can be added in the future.

## Features

- **Automatic EPG fetching** — downloads SiliconDust's complete gzip-compressed XMLTV guide on the required randomized 20-28 hour schedule
- **Canonical XMLTV output** — preserves SiliconDust metadata, filters out channels unavailable from the configured tuners, and publishes updates atomically
- **Per-channel availability** — keeps disabled channels visible in Lineup while excluding them from published guides, virtual lineups, and physical tuner streams
- **Live TV streaming** — multi-track MPEG-TS proxy plus selectable Watch audio and subtitles, with Jellyfin FFmpeg AC-4 decoding
- **Device diagnostics** — connectivity checks across DNS, ping, HTTP API, TCP, and UDP discovery
- **Active stream monitoring** — Dashboard visibility into hosted MPEG-TS, fMP4, and HLS sessions with source/output codec and bitrate details
- **Application diagnostics** — live in-app structured logs, optional rolling files, and opt-in external observability targets
- **Native HDHomeRun protocol** — binary protocol implementation for UDP discovery, TCP control, and channel scanning
- **Multi-device HDHomeRun proxy** — one isolated virtual profile per physical tuner, with optional SiliconDust and SSDP discovery
- **XMLTV endpoint** — `/api/xmltv` for external media servers to pull the guide file directly
- **Versioned status API** — `/api/v1/status` provides one stable guide, device, tuner, and stream snapshot for Home Assistant and other dashboards
- **SQLite caching** — EPG data stored locally with automatic cleanup of expired entries

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
4. Open the extracted `Lineup-<version>-win-<architecture>` folder and double-click `Start-Lineup.cmd`. Always use this launcher rather than opening `app\Lineup.Web.exe` directly; the launcher configures the user-local data locations.
5. If Microsoft Defender SmartScreen appears, confirm that the file came from the official Lineup GitHub release, select **More info**, and then select **Run anyway**. Published Windows packages are not currently code-signed.
6. If Windows Firewall asks for access, allow Lineup on **Private networks**. Public-network access is not required.
7. Keep the Lineup console window open. The launcher opens <http://localhost:8080>; refresh the page if the browser opens before Lineup finishes starting.
8. Complete **Settings → Device** using the HDHomeRun's LAN IP address or hostname.

Press `Ctrl+C` in the Lineup console or close that window to stop the application. Lineup stores mutable files outside the extracted program folder:

| Data | Windows location |
|---|---|
| Settings, guide database, logs, and other application state | `%LOCALAPPDATA%\Lineup\data` |
| Generated XMLTV guide | `%LOCALAPPDATA%\Lineup\xmltv\epg.xml` |

Enter `%LOCALAPPDATA%\Lineup` in File Explorer's address bar to open these folders. They remain in place when you replace the application during an update.

#### Update the direct Windows application

1. Stop the running Lineup console.
2. Download and extract the new ZIP for the same architecture.
3. Run `Start-Lineup.cmd` from the new folder.
4. If you configured automatic startup, replace the old Startup-folder shortcut with one that targets the new `Start-Lineup.cmd`.
5. After confirming that your settings are present, delete the old extracted program folder.

Do not delete `%LOCALAPPDATA%\Lineup` unless you intentionally want to remove the persisted installation. You can verify a download with `SHA256SUMS.txt` from the release assets:

```powershell
Get-FileHash .\Lineup-2.0.1-win-x64.zip -Algorithm SHA256
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

The first download can take a few minutes. Refresh the browser if Lineup is still starting. When Lineup opens, go to **Settings → Device**, enter the HDHomeRun's IP address or hostname, and save the settings. If `hdhomerun.local` does not work, use the numeric LAN address shown in the official HDHomeRun application or your router, such as `192.168.1.50`.

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

1. Create `Lineup\data` and `Lineup\xmltv` folders inside your Windows user folder. In File Explorer, enter `%USERPROFILE%\Lineup` in the address bar, create the `data` and `xmltv` folders there, and note the full path shown by File Explorer.
2. In Docker Desktop, open **Images**, search Docker Hub for `am385/lineup`, and pull the `latest` tag. Sign in to Docker Hub if Docker Desktop requests it.
3. Find `am385/lineup:latest` under **Images**, select **Run**, and expand **Optional settings**.
4. Set the container name to `lineup`.
5. Map the following ports. Keep the UDP host ports at their defaults if you plan to enable virtual HDHomeRun discovery:

   | Host port | Container port | Protocol | Purpose |
   |---:|---:|---|---|
   | `8080` | `8080` | TCP | Lineup web interface |
   | `8443` | `8443` | TCP | Optional HTTPS |
   | `65001` | `65001` | UDP | HDHomeRun discovery |
   | `1900` | `1900` | UDP | SSDP discovery |

6. Add these volume mappings:

   | Windows host folder | Container path |
   |---|---|
   | `C:\Users\YourName\Lineup\data` | `/appdata` |
   | `C:\Users\YourName\Lineup\xmltv` | `/xmltv` |
   | `C:\Users\YourName\Lineup\transient` | `/transient` |

   Replace `YourName` with the Windows user-folder name shown in File Explorer.

7. Select **Run**, wait for the container to start, then open <http://localhost:8080> and complete **Settings → Device**.

No environment variables are required for a normal HTTP installation. HTTPS is optional; see [HTTP and optional HTTPS](#http-and-optional-https) before adding a certificate mount or password. Open the `lineup` container in Docker Desktop to start, stop, restart, delete, inspect, or view its **Logs**. If it does not start automatically after Windows restarts, open **Containers** and select **Start**.

To update a GUI installation, pull the newest `am385/lineup:latest` image, record or copy the existing container's settings, remove the old `lineup` container, and run the new image with the same ports and host-folder mappings. Removing the container does not delete the two Windows folders or their Lineup data. Compose is recommended because it remembers these settings and performs this replacement automatically.

### Windows networking help

Docker Desktop runs Lineup behind a virtualized network. The web interface normally works through <http://localhost:8080>, but local broadcast and multicast discovery can vary with the Windows, Docker Desktop, firewall, and router configuration.

- Configure the physical HDHomeRun by its LAN IP address if its hostname does not resolve or automatic discovery fails.
- Keep host UDP ports `65001` and `1900` mapped to the same container ports. Using different host ports can prevent Jellyfin, Plex, and other clients from discovering Lineup as a virtual tuner.
- Allow Docker Desktop through Windows Firewall on the private network.
- If Lineup and the tuner are on different VLANs or subnets, allow routing between them and configure the tuner address manually.
- You can still add Lineup to Jellyfin or Plex with the virtual device URL shown in Lineup even when UDP discovery is unavailable.

Running Docker directly inside a WSL distribution, Podman Desktop, and Rancher Desktop can also run Linux containers, but they require more manual administration or may behave differently from the documented Docker workflow. They are advanced alternatives rather than supported beginner installation paths.

## Architecture

| Project | Description |
|---|---|
| `Lineup.HDHomeRun.Device` | Local device communication — discovery, channel lineup, native binary protocol |
| `Lineup.HDHomeRun.Api` | Remote API client — downloads XMLTV guide data from `api.hdhomerun.com` |
| `Lineup.Core` | Core business logic — XMLTV import, orchestration, and normalized caching (EF Core + SQLite) |
| `Lineup.Web` | Blazor Server web app — dashboard, EPG guide viewer, settings, live TV |
| `Lineup.Tui` | Terminal UI — interactive menu using Spectre.Console |

## Building from source

The following instructions are for developers who want to build or modify Lineup. Windows users who only want to run the published application should use [Installing Lineup on Windows](#installing-lineup-on-windows).

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
cd src/Lineup.Web
dotnet run
```

The app starts on `http://localhost:8080` by default. Persistent application data defaults to `/appdata`, XMLTV output defaults to `/xmltv/epg.xml`, and transient stream artifacts default to `/transient`. The development launch profiles keep local runtime state in the ignored repository-root `.lineup` directory, separated into `appdata`, `xmltv`, and `transient` subdirectories. Configure the HDHomeRun device address through the Settings UI.

### Running the TUI

```bash
cd src/Lineup.Tui
dotnet run
```

Configure the TUI via environment variables:

- `Lineup__DeviceAddress` — HDHomeRun device IP or hostname
- `Lineup__DatabasePath` — path to the SQLite cache database

### Building and running with Docker

```bash
docker compose up -d
```

Or using the production image:

```bash
docker compose -f docker-compose.prod.yml up -d
```

These commands run from a source checkout. The Docker image includes Jellyfin FFmpeg for live TV transcoding and ATSC 3.0 AC-4 audio decoding. Data is persisted via the `appdata` named volume mounted at `/appdata`. HLS segments and subtitle sidecars use the separate `transient` volume mounted at `/transient`. Lineup uses fixed container ports for HTTP (`8080`), HTTPS (`8443`), HDHomeRun discovery (`65001/udp`), and SSDP (`1900/udp`). The Compose `HTTP_PORT`, `HTTPS_PORT`, `HDHOMERUN_DISCOVERY_PORT`, and `SSDP_PORT` variables change only the corresponding host-facing ports. Keep the UDP ports at their defaults for standards-based automatic discovery. Native Linux deployments can use `network_mode: host` if the HDHomeRun device requires local network discovery.

### Publishing a Development Build

Maintainers can open **Actions → Publish Lineup Dev → Run workflow**, select any branch, and start a development publication. The selected revision is tested before the workflow publishes the moving development images to Docker Hub and GitHub Container Registry:

```text
am385/lineup:dev
ghcr.io/am385/lineup:dev
```

Each run also publishes a traceable Docker tag in the form `dev-<run-number>-<short-sha>`. Development binaries use a version derived from `Directory.Build.props`, such as `2.1.0-dev.42+abc1234`, without changing the stable version in source control.

The workflow creates or updates the **Lineup Dev** GitHub prerelease under the moving `dev` tag. Its `Lineup-dev-win-x64.zip`, `Lineup-dev-win-arm64.zip`, and checksum assets are replaced on each successful publication. The workflow run retains versioned Windows packages and checksums as a 30-day Actions artifact, and the prerelease notes identify the exact source ref, commit, version, run, and traceable Docker tag.

Starting a newer development publication cancels an older in-progress development run so the older revision cannot overwrite the newer requested `dev` channel. Development builds are mutable and intended for testing; use a stable versioned release for production installations. The workflow requires the same `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN` repository secrets as stable publication, while GHCR uses the workflow's built-in GitHub token.

### Publishing a Release

`Directory.Build.props` is the single source for Lineup's release version. Before publishing, update its `<Version>` value to the next stable semantic version.

Maintainers can then open **Actions → Publish Lineup Release → Run workflow** and select the commit or branch to publish. The workflow reads the application version, tests the selected revision, and publishes both the full version and major/minor tags to Docker Hub and GitHub Container Registry:

```text
am385/lineup:2.0.1
am385/lineup:2.0
ghcr.io/am385/lineup:2.0.1
ghcr.io/am385/lineup:2.0
```

The workflow also builds self-contained x64 and ARM64 Windows ZIPs with the matching pinned Jellyfin FFmpeg distribution. A manual run makes the ZIPs and `SHA256SUMS.txt` available as a workflow artifact but does not create a Git tag or GitHub Release. A pushed stable tag such as `v2.0.1` creates or updates the matching GitHub Release and attaches those Windows assets. Tag publication stops with an explicit error if the tag does not exactly match `v` followed by the version in `Directory.Build.props`.

Select **Also update the latest tag** only for the current stable release. Manual publication requires the `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN` repository secrets; GHCR uses the workflow's built-in GitHub token. Manual publication does not create a Git tag or GitHub Release.

The MPEG-TS proxy preserves every audio track and every standalone subtitle codec the MPEG-TS muxer can represent
(`dvb_subtitle` and `dvb_teletext`). It copies video and non-AC-4 audio while converting each AC-4 track to AC-3.
Language, title, and disposition metadata are retained where the container supports them. Embedded CEA-608/708 data is
retained with copied video and passed to the H.264 encoder when video conversion is enabled. Text/WebVTT, ASS, PGS, DVD,
and other subtitle formats that MPEG-TS cannot carry are reported in diagnostics rather than mapped.

Watch defaults to the source's default/first audio track and subtitles Off. Its Audio and Subtitles selectors restart only
that browser client's derived fMP4 output; the shared physical tuner source and other subscribers continue uninterrupted.
Text, WebVTT, ASS, and exposed CEA caption tracks are converted by the same FFmpeg process to a synchronized transient
WebVTT sidecar and rendered by the browser without re-encoding H.264 video. Bitmap subtitles are burned in only when
selected and force H.264 encoding, which increases CPU use. Unsupported tracks are disabled and invalid or unavailable
source indexes return an explicit stream error.

WebVTT sidecars live only beneath the application-owned `lineup-subtitles` temporary directory. They are removed on
disconnect, stop, application startup, and Factory Reset. If captions do not appear, inspect the active-stream details and
logs: captions embedded in video but not exposed by the tuner or FFmpeg as an addressable subtitle stream can be preserved
in MPEG-TS but cannot be synthesized as a Watch text track.

Virtual Tuner Video can optionally transcode HEVC to H.264 for clients that cannot process HEVC Live TV, at the cost of CPU and generation loss. Virtual Tuner Audio can preserve non-AC-4 codecs or transcode those tracks to AC-3 or E-AC-3. AC-4 can be preserved for compatible receivers or transcoded separately. Audio above 5.1 channels is capped at 5.1 when transcoded. An explicit `transcode` query parameter remains available for HDHomeRun hardware profiles (`mobile`, `heavy`, `internet720`, `internet480`, or `internet360`), but defaults to `none`.

Jellyfin FFmpeg's AC-4 support is decoder-only and does not support every object-based or Dolby Atmos AC-4 presentation. Unsupported presentations are reported as stream errors rather than silently copied or discarded. The HLS and fMP4 endpoints continue to encode audio as AAC.

When an HDHomeRun reports DRM error 811, API streams return a protected-content error by default. The Transcode Settings page can instead enable a synthetic **Content Protected** slate. The fallback is generated locally as H.264 video with silent AAC audio in the requested MPEG-TS, fMP4, or HLS format; protected programming is never decrypted. Synthetic DRM and disabled-channel slates count toward **Maximum Concurrent Streams** but do not consume physical tuner capacity.

## Configuration

All settings are configurable through the web UI's settings page and persisted to `settings.json`:

| Setting | Description |
|---|---|
| Device Address | HDHomeRun device IP or hostname |
| XMLTV Output Path | Where to publish and serve the canonical SiliconDust XMLTV document. The default is `/xmltv/epg.xml`. Enter a path directly or use the server directory browser. `Lineup:XmltvPath` optionally overrides the default for new settings and remains available as the reset value; the saved setting is authoritative afterward. |
| Automatic Guide Refresh | Enables randomized refreshes every 20-28 hours; availability is 2 days or 14 days with a DVR subscription |
| Virtual Tuner Audio | Preserve non-AC-4 audio codecs, or transcode those tracks to AC-3/E-AC-3 |
| AC-4 Audio | Preserve AC-4, or transcode it to AC-3 (default) or E-AC-3 |
| Virtual Tuner Video | Preserve the video codec (default), or transcode HEVC to H.264 for compatibility |
| DRM-Protected Content | Return an explicit error (default), or stream a synthetic Content Protected slate |
| Disabled Channels | Return an explicit error (default), or stream a synthetic Disabled Channel slate without opening a physical tuner |
| Active Stream Refresh | Dashboard refresh interval for hosted stream details; defaults to 5 seconds and 0 disables auto-refresh |
| HDHomeRun Proxy Profiles | Physical address, optional friendly name and tuner cap, and an explicit advertised URL for each virtual device |
| Network Discovery | Opt-in SiliconDust UDP (65001) and SSDP (1900) advertisement of enabled proxy profiles |
| File Logging | Opt-in rolling files with configurable minimum level, retention period, and per-file size limit; changes apply immediately when saved |
| Status API Privacy | Independently redact active-stream client addresses, device addresses, and URLs from `/api/v1/status` |

`XmltvOutputPath` accepts relative filenames and absolute filesystem paths. Lineup's web settings are unauthenticated and are intended for a trusted local network; do not expose the application to untrusted clients when arbitrary output paths are enabled.

### Versioned status API

`GET /api/v1/status` returns a stable JSON snapshot for external dashboards. It includes the application version and runtime identity, guide counts and coverage,
automatic-fetch and XMLTV state, the physical device, every configured virtual device, cached tuner status, and active streams with their source/output tracks.
The endpoint is passive: polling it does not fetch guide data, discover devices, refresh tuners, retune channels, or interrupt streams. Responses use
`Cache-Control: no-store`; unavailable sections report their own status and error without hiding the remaining state.

The endpoint is unauthenticated and follows Lineup's trusted-network model. Open **Settings → API** to redact active-stream client addresses, device addresses,
and URLs independently. Client addresses are redacted by default; device addresses and URLs are visible by default. Redacted properties remain in the JSON as
`null`, and HDHomeRun authentication values and server filesystem paths are never exposed. Use HTTPS and authentication at a reverse proxy before allowing
access from an untrusted network.

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

The Settings **Reset** tab can stage default settings for review or perform a Factory Reset. Factory Reset removes Lineup-owned settings, guide database, canonical XMLTV cache, rolling logs, configured XMLTV output, and transient stream files during a graceful restart. The browser displays a blocking restart screen and returns to Device setup after detecting the new Lineup instance. XMLTV files configured outside Lineup-owned storage are preserved. A service supervisor such as Docker's `restart: unless-stopped` must restart the process after reset.

Lineup-owned persistent data defaults to `/appdata`. The optional `Lineup:AppDataPath` setting overrides that root for custom deployments. This data includes settings and backups, the SQLite guide database and canonical cache, rolling logs, ASP.NET Core Data Protection keys, and the restart-safe Factory Reset marker.

Transient HLS segments and subtitle sidecars default to `/transient`. The optional `Lineup:TransientPath` setting (environment variable `Lineup__TransientPath`) overrides this location. The Compose files mount a `transient` named volume at `/transient`, matching the `appdata` and `xmltv` volume conventions. Custom deployments can replace that named-volume mount with a memory-backed bind mount or Compose `tmpfs`, or with disk-backed storage when transient data must not consume system memory.

The 2.0 Compose files rename the `config` volume to `appdata` and mount it at `/appdata`. Existing installations must preserve their data during the upgrade. Either copy the contents of the old named volume into the new `appdata` volume, or configure the `appdata` volume's `name` property to reference the existing Docker volume. Bind-mount users can mount the same host directory at `/appdata`. Starting 2.0 with a new empty volume begins with a new Lineup installation.

ASP.NET Core Data Protection keys are stored in the `data-protection-keys` directory beneath the app-data root. Persisting this directory through the Docker `/appdata` volume keeps antiforgery tokens and other protected browser state readable after a container recreation. Factory Reset deletes the key ring so previously protected state is intentionally invalidated.

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

### HDHomeRun proxy profiles

Open **Settings → Device → HDHomeRun Proxy** to configure virtual devices. The primary profile follows the existing **Device Address** setting and remains
available at the legacy root endpoints:

- `/discover.json`
- `/lineup.json`
- `/lineup.xml`
- `/lineup.m3u`
- `/device.xml`
- `/auto/v{channel}`

Additional profiles use the stable path `/hdhomerun/{virtualDeviceId}/`. Their discovery, lineup, device description, and stream URLs all remain under that
path. The Settings page displays copyable manual setup URLs for each enabled profile and the XMLTV guide URL at `/api/xmltv`.

Guide downloads concatenate the current `DeviceAuth` values from every enabled physical profile, allowing one canonical XMLTV document to cover all
configured tuners. The downloaded guide is filtered to the union of channels currently returned by those tuners. Use **Refresh Channels** on the
Dashboard to update the persisted tuner-lineup snapshot independently, or enable the guide-fetch option that refreshes channels first. A guide fetch
applies the saved snapshot without otherwise querying the tuners. Channels disabled on the Channels page remain visible in Lineup's Channels and Guide
pages, but are excluded from published XMLTV and virtual JSON, XML, and M3U lineups. Their MPEG-TS, fMP4, and HLS URLs return an error by default or a
synthetic slate when configured. Disabled choices survive tuner refreshes by guide number, and newly discovered channels start enabled. `DeviceAuth`
is read immediately before every request because SiliconDust rotates it regularly.

Lineup atomically limits HDHomeRun-compatible MPEG-TS routes to each profile's effective physical tuner count. Receivers for the exact same upstream
channel and hardware-transcode source share one tuner lease through the stream multiplexer. Different channels consume separate slots. This includes
the legacy `/api/stream/{channel}` route for the primary profile, so it does not double-count a matching `/auto/v{channel}` source. Browser-specific
fMP4 and HLS workflows are outside the HDHomeRun-compatible route lease boundary and remain governed by **Maximum Concurrent Streams**. The same
host-wide stream limit applies to synthetic DRM and disabled-channel slates in every format even though those slates do not consume tuner capacity.

Network discovery is disabled by default to avoid UDP port conflicts. To enable it, turn on **Enable UDP and SSDP network discovery** and configure an
absolute HTTP or HTTPS **Advertised base URL** for every profile that should be discovered. This must be a Lineup address reachable by media servers;
request-derived and container-internal addresses are not advertised.

Jellyfin commonly identifies auto-discovered tuners by the source network address. Advertising several virtual devices from one process may therefore
require a distinct host IP for each advertised profile; otherwise add the displayed tuner URLs manually. Docker bridge networks frequently do not pass
UDP broadcast or multicast traffic. Use host networking where the platform supports it, or use manual setup. The existing Dashboard and EPG workflow
continues to use the primary legacy **Device Address** only.

On Windows, Lineup requires exclusive ownership of UDP port 1900 because Winsock does not reliably duplicate SSDP datagrams across shared bindings.
If the Windows **SSDP Discovery** service (`SSDPSRV`) already owns the port, Lineup logs an explicit error and disables only SSDP; SiliconDust UDP
discovery on port 65001 continues running. Stop and disable `SSDPSRV` before starting Lineup, or use the manual profile URLs shown in Settings.

## Tech Stack

- **.NET 10** — target framework for all projects
- **Blazor Server** — interactive web UI
- **Entity Framework Core + SQLite** — EPG data caching
- **Spectre.Console** — terminal UI rendering
- **Serilog** — structured logging
- **Jellyfin FFmpeg** — live TV stream transcoding with AC-4 decoding

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE).
