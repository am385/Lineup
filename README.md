# Lineup

**Lineup** is a .NET application that downloads official HDHomeRun [XMLTV](http://wiki.xmltv.org/index.php/XMLTVFormat) guide data and proxies network TV tuner streams for media servers like **Jellyfin** and **Plex**.

It includes a web-based dashboard, a terminal UI, live TV streaming with transcoding, and automatic XMLTV refreshes.

> Currently this is only supporting the [HDHomeRun](https://www.silicondust.com/) Tuners like the HDHomeRun FLEX 4K. Hopefully more can be added in the future.

## Features

- **Automatic EPG fetching** — downloads SiliconDust's complete gzip-compressed XMLTV guide on the required randomized 20-28 hour schedule
- **Canonical XMLTV output** — preserves SiliconDust metadata and atomically publishes the downloaded document without lossy reconstruction
- **Live TV streaming** — multi-track MPEG-TS proxy plus selectable Watch audio and subtitles, with Jellyfin FFmpeg AC-4 decoding
- **Device diagnostics** — connectivity checks across DNS, ping, HTTP API, TCP, and UDP discovery
- **Active stream monitoring** — Dashboard visibility into hosted MPEG-TS, fMP4, and HLS sessions with source/output codec and bitrate details
- **Application diagnostics** — live in-app structured logs, optional rolling files, and opt-in external observability targets
- **Native HDHomeRun protocol** — binary protocol implementation for UDP discovery, TCP control, and channel scanning
- **Multi-device HDHomeRun proxy** — one isolated virtual profile per physical tuner, with optional SiliconDust and SSDP discovery
- **XMLTV endpoint** — `/api/xmltv` for external media servers to pull the guide file directly
- **Versioned status API** — `/api/v1/status` provides one stable guide, device, tuner, and stream snapshot for Home Assistant and other dashboards
- **SQLite caching** — EPG data stored locally with automatic cleanup of expired entries

## Architecture

| Project | Description |
|---|---|
| `Lineup.HDHomeRun.Device` | Local device communication — discovery, channel lineup, native binary protocol |
| `Lineup.HDHomeRun.Api` | Remote API client — downloads XMLTV guide data from `api.hdhomerun.com` |
| `Lineup.Core` | Core business logic — XMLTV import, orchestration, and normalized caching (EF Core + SQLite) |
| `Lineup.Web` | Blazor Server web app — dashboard, EPG guide viewer, settings, live TV |
| `Lineup.Tui` | Terminal UI — interactive menu using Spectre.Console |

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An HDHomeRun device on your local network
- [Jellyfin FFmpeg](https://github.com/jellyfin/jellyfin-ffmpeg) (required for live TV transcoding and AC-4 decoding; included in the Docker image and available as `ffmpeg` on `PATH`)

On Windows, install the pinned portable Jellyfin FFmpeg build for local development:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-JellyfinFfmpeg.ps1
```

The script verifies the official release checksum and installs `ffmpeg.exe` and `ffprobe.exe` into the ignored `src/Lineup.Web/.ffmpeg` directory. Lineup adds that directory to its process `PATH` automatically. Restart the app after installation.

### Running the Web App

```bash
cd src/Lineup.Web
dotnet run
```

The app starts on `http://localhost:8080` by default. Persistent application data defaults to `/appdata`, and XMLTV output defaults to `/xmltv/epg.xml`. The development launch profiles override these with `Lineup__AppDataPath=.` and `Lineup__XmltvPath=xmltv` so local state is retained in the web project directory. Configure the HDHomeRun device address through the Settings UI.

### Running the TUI

```bash
cd src/Lineup.Tui
dotnet run
```

Configure the TUI via environment variables:

- `Lineup__DeviceAddress` — HDHomeRun device IP or hostname
- `Lineup__DatabasePath` — path to the SQLite cache database

### Docker

```bash
docker compose up -d
```

Or using the production image:

```bash
docker compose -f docker-compose.prod.yml up -d
```

The Docker image includes Jellyfin FFmpeg for live TV transcoding and ATSC 3.0 AC-4 audio decoding. Data is persisted via the `appdata` named volume mounted at `/appdata`. Lineup uses fixed container ports for HTTP (`8080`), HTTPS (`8443`), HDHomeRun discovery (`65001/udp`), and SSDP (`1900/udp`). The Compose `HTTP_PORT`, `HTTPS_PORT`, `HDHOMERUN_DISCOVERY_PORT`, and `SSDP_PORT` variables change only the corresponding host-facing ports. Keep the UDP ports at their defaults for standards-based automatic discovery. Use `network_mode: host` if your HDHomeRun device requires local network discovery.

### Publishing a Container Release

Maintainers can open **Actions → Build and Push Docker Image → Run workflow**, select the commit or branch to publish, and enter a stable semantic version such as `2.0.0`. The workflow tests the selected revision and publishes both the full version and major/minor tags to Docker Hub and GitHub Container Registry:

```text
am385/lineup:2.0.0
am385/lineup:2.0
ghcr.io/am385/lineup:2.0.0
ghcr.io/am385/lineup:2.0
```

Select **Also update the latest tag** only for the current stable release. Manual publication requires the `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN` repository secrets; GHCR uses the workflow's built-in GitHub token. Publishing an image does not create a Git tag or GitHub Release.

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

When an HDHomeRun reports DRM error 811, API streams return a protected-content error by default. The Transcode Settings page can instead enable a synthetic **Content Protected** slate. The fallback is generated locally as H.264 video with silent AAC audio in the requested MPEG-TS, fMP4, or HLS format; protected programming is never decrypted.

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
- `/device.xml`
- `/auto/v{channel}`

Additional profiles use the stable path `/hdhomerun/{virtualDeviceId}/`. Their discovery, lineup, device description, and stream URLs all remain under that
path. The Settings page displays copyable manual setup URLs for each enabled profile and the XMLTV guide URL at `/api/xmltv`.

Guide downloads concatenate the current `DeviceAuth` values from every enabled physical profile, allowing one canonical XMLTV document to cover all
configured tuners. `DeviceAuth` is read immediately before every request because SiliconDust rotates it regularly.

Lineup atomically limits HDHomeRun-compatible MPEG-TS routes to each profile's effective physical tuner count. Receivers for the exact same upstream
channel and hardware-transcode source share one tuner lease through the stream multiplexer. Different channels consume separate slots. This includes
the legacy `/api/stream/{channel}` route for the primary profile, so it does not double-count a matching `/auto/v{channel}` source. Browser-specific
fMP4 and HLS workflows are outside the HDHomeRun-compatible route lease boundary and remain governed by **Maximum Concurrent Streams**.

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
