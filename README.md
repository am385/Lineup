# Lineup

**Start here:** [What is Lineup?](#what-is-lineup) | [Install](#installation) | [Use Lineup](#using-lineup) | [Report an issue](#getting-help-and-reporting-issues) | [Contribute](#contributing)

## What is Lineup?

**Lineup** is a .NET live-TV routing and guide platform with a Blazor management UI and a terminal host.
It combines physical tuners, shared streaming pipelines, guide sources and independently configured virtual outputs.
The built-in physical adapter supports [HDHomeRun](https://www.silicondust.com/) devices; Lineup Link connects other Lineup instances.

Use it to bring your tuners and programme guides together, watch live TV in a browser, or provide virtual tuners and
guide data to media servers such as Jellyfin and Plex. You need a reachable TV source; Lineup does not supply broadcasts
or bypass protected-content restrictions.

## Features

- Live MPEG-TS streaming and browser HLS/DASH playback with selectable audio and subtitles.
- Shared acquisitions and transcoding across viewers, with optional GPU video encoding.
- Aggregated virtual HDHomeRun devices and authenticated Lineup-to-Lineup sharing.
- Multi-source guides, channel preferences, and global or scoped XMLTV/JSON publications.
- Dashboard stream-flow/resource diagnostics, structured logs and a passive status API.
- Inherited device profiles, management-hosted or isolated outputs, and local SQLite storage.

## Installation

- [Docker Desktop on Windows (recommended)](#recommended-run-lineup-with-docker-desktop)
- [Direct Windows application](#alternative-run-lineup-directly-on-windows)
- [Docker from a source checkout](#building-and-running-with-docker)
- [Advanced networking, reverse proxies and HTTPS](docs/deployment.md)

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
Get-FileHash .\Lineup-3.0.1-win-x64.zip -Algorithm SHA256
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

No environment variables are required for a normal HTTP installation. The dedicated-output range is optional when every virtual output uses the management host; when using dedicated listeners, publish each configured listener port. HTTPS is optional; see [HTTP and optional HTTPS](docs/deployment.md#http-and-optional-https) before adding a certificate mount or password. Open the `lineup` container in Docker Desktop to start, stop, restart, delete, inspect, or view its **Logs**. If it does not start automatically after Windows restarts, open **Containers** and select **Start**.

To update a GUI installation, pull the newest `am385/lineup:latest` image, record or copy the existing container's settings, remove the old `lineup` container, and run the new image with the same ports and host-folder mappings. Removing the container does not delete the three Windows folders or their Lineup data. Compose is recommended because it remembers these settings and performs this replacement automatically.

### Windows networking help

Docker Desktop runs Lineup behind a virtualized network. The web interface normally works through <http://localhost:8080>, but local broadcast and multicast discovery can vary with the Windows, Docker Desktop, firewall, and router configuration.

- Configure the physical HDHomeRun by its LAN IP address if its hostname does not resolve or automatic discovery fails.
- Keep host UDP ports `65001` and `1900` mapped to the same container ports. Using different host ports can prevent Jellyfin, Plex, and other clients from discovering Lineup as a virtual tuner.
- Allow Docker Desktop through Windows Firewall on the private network.
- If Lineup and the tuner are on different VLANs or subnets, allow routing between them and configure the tuner address manually.
- You can still add Lineup to Jellyfin or Plex with the virtual device URL shown in Lineup even when UDP discovery is unavailable.

Running Docker directly inside a WSL distribution, Podman Desktop, and Rancher Desktop can also run Linux containers, but they require more manual administration or may behave differently from the documented Docker workflow. They are advanced alternatives rather than supported beginner installation paths.

## Building and running with Docker

```bash
docker compose up -d
```

Or using the production image:

```bash
docker compose -f docker-compose.prod.yml up -d
```

These commands run from a source checkout. The Docker image includes Jellyfin FFmpeg for live TV transcoding, ATSC 3.0 AC-4 audio decoding, and the isolated Lineup Link and HDHomeRun worker packages. Data is persisted via the `appdata` named volume mounted at `/appdata`. CMAF fragments and subtitle sidecars use the separate `transient` volume mounted at `/transient`. Lineup uses fixed container ports for its management host HTTP (`8080`), HTTPS (`8443`), HDHomeRun discovery (`65001/udp`), and SSDP (`1900/udp`). Dedicated output listener ports are configured per virtual device. Both Compose files publish container ports `8600-8615` by default, supporting up to 16 dedicated listeners.

Set `VIRTUAL_OUTPUT_PORTS` to an equal-sized host-to-container range mapping when a deployment needs different external ports or a different number of published listeners. The internal range must include every port assigned to a virtual device. For example, `VIRTUAL_OUTPUT_PORTS=9600-9607:8600-8607` publishes eight internal listeners on host ports `9600-9607`. In each virtual device's dedicated **Output Hosting** settings, select its internal listener port and set the optional published-port override to the corresponding host port; Lineup uses the published port in discovery responses and connection bundles. Docker requires both sides of a range mapping to contain the same number of ports. The Compose `HTTP_PORT`, `HTTPS_PORT`, `HDHOMERUN_DISCOVERY_PORT`, and `SSDP_PORT` variables change only the corresponding host-facing management or discovery ports. Keep the UDP ports at their defaults for standards-based automatic discovery. Native Linux deployments can use `network_mode: host` if the HDHomeRun device requires local network discovery.

## Before upgrading to Lineup 3.0

**Lineup 3.0 intentionally resets earlier configuration and storage; it does not migrate Lineup 2.0 data.**
Record the device, guide and network settings you need to recreate. Incompatible persisted state requires explicit
recovery or Factory Reset. Read the [reset and recovery guide](docs/recovery.md) before replacing an installation.

## Network trust

The management UI and status API are unauthenticated and intended for a trusted local network.
Do not expose them directly to untrusted clients. Lineup Link uses separate access grants and can use an isolated
listener/firewall boundary; protect tokens and configure proxy/TLS trust explicitly.

## Using Lineup

### First-run setup

After installing and starting Lineup:

1. Open <http://localhost:8080> on the computer running Lineup. From another computer on your trusted network, use the Lineup computer's LAN address and published HTTP port.
2. Under **Settings > Physical Devices**, add your HDHomeRun and enter its LAN address. A physical device is the source of your live channels; use its numeric IP address if discovery or hostname resolution fails.
3. Under **Settings > Guide Sources**, add a SiliconDust guide source for that device. Guide sources supply programme listings; they are configured separately from physical tuners.
4. Select **Save Settings**. The built-in Web Player is available without adding a virtual HDHomeRun output.
5. On the **Dashboard**, select **Refresh Channels**, then **Fetch Guide**. Wait for the fetch to finish and inspect any reported source errors.
6. Open **Guide** to browse listings, or **Watch** to select a channel and play live TV.

You can add more physical devices and guide sources later. Use **Channels** to review the lineup, enable or disable
channels, and adjust guide bindings. Guide sources have automatic refresh schedules, so you do not need to fetch manually
every time you watch.

### Watching and managing live TV

**Watch** plays enabled channels in your browser. Use the player's menu for available audio tracks and text subtitles;
captions are available only when the source exposes them. **Watch Test > Run All** measures playback support in your
current browser without using a tuner and saves a browser-specific profile. Run it when setting up a new browser or
troubleshooting playback; leave stream overrides at **Browser profile** unless you need a specific manual choice.

The **Dashboard** shows devices, guide status, active streams and resource usage. Select a stream or processing node
to inspect its details. A session's **Stop** button stops that viewer, not unrelated viewers sharing the same producer.
Use **Logs** to inspect recent events and **Settings > Logging** if you need retained log files.

See the [Web application guide](src/Apps/Lineup.App.Web/README.md) for settings and diagnostics, and the
[Web Player guide](src/Plugins/WebPlayer/Lineup.Plugin.WebPlayer/README.md) for playback, compatibility and caption details.

### Connecting Jellyfin, Plex or another Lineup instance

For a media server, add a virtual HDHomeRun under **Settings > Virtual Devices**, subscribe it to the physical devices
you want to expose, and save. For management-hosted output, configure an **Advertised base URL** reachable by the media
server, using the Lineup computer's LAN address rather than `localhost`. Use the displayed virtual-device URL when
adding the tuner to your media server; automatic UDP/SSDP discovery is optional and disabled by default.

Under **Settings > Guide Outputs**, use the published XMLTV guide URL for your media server's programme-guide setup.
The [virtual HDHomeRun guide](src/Plugins/SiliconDust/Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner/README.md)
explains device URLs and discovery. To share with another Lineup installation instead, configure a Lineup Link output
and access grant as described in the [Lineup Link guide](src/Plugins/LineupLink/Lineup.Plugin.LineupLink/README.md).

## Getting help and reporting issues

If the web page does not open, check that the application console or Docker container is still running and that the
published port is reachable. If channels or guide data are missing, confirm the tuner address, saved source settings,
and the Dashboard's refresh/fetch result. For playback problems, check **Watch Test**, active-stream details and **Logs**.
See [Windows networking help](#windows-networking-help) for discovery and firewall issues.

Search [existing issues](https://github.com/am385/Lineup/issues) before opening a
[bug report](https://github.com/am385/Lineup/issues/new?template=bug_report.md). Include the Lineup version from **About**,
installation method, operating system, tuner model, browser, reproduction steps, expected and actual behavior,
and relevant error messages or logs. For streaming problems, also include the channel, approximate time, and whether
the failure happens in Watch or an external media server. Redact tokens, passwords and private network details before
posting diagnostics. A .NET SDK is not required to report a problem with a packaged installation.

For improvements rather than bugs, use the
[feature request](https://github.com/am385/Lineup/issues/new?template=feature_request.md) template and describe your use case.

## Documentation

| Topic | Guide |
|---|---|
| Installation and updates | [Installation instructions](#installation) |
| Advanced networking and TLS | [Deployment](docs/deployment.md) |
| Settings, dashboard, logging, health and status API | [Web application](src/Apps/Lineup.App.Web/README.md) |
| Terminal host and source configuration | [TUI](src/Apps/Lineup.App.Tui/README.md) |
| Watch, compatibility profiles and captions | [Web Player](src/Plugins/WebPlayer/Lineup.Plugin.WebPlayer/README.md) |
| Transcoding, GPU access and slate behavior | [FFmpeg](src/Core/Lineup.Core.Streaming.FFmpeg/README.md) |
| Physical tuners and EXTEND | [HDHomeRun physical adapter](src/Plugins/SiliconDust/Lineup.Plugin.SiliconDust.HDHomeRun.PhysicalTuner/README.md) |
| Virtual tuner URLs and discovery | [Virtual HDHomeRun](src/Plugins/SiliconDust/Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner/README.md) |
| Remote sharing and access grants | [Lineup Link](src/Plugins/LineupLink/Lineup.Plugin.LineupLink/README.md) |
| Guides, topology and shared-stream resource limits | [Core](src/Core/Lineup.Core/README.md) |
| Dedicated-output supervision and resource policies | [Worker runtime](src/Core/Lineup.Core.VirtualOutputs.Runtime/README.md) |
| Reset, offline backup and restore | [Recovery](docs/recovery.md) |
| Windows CPU/GPU investigation | [Performance collector](tools/Lineup.Tools.PerformanceCollector/README.md) |
| Source builds, browser assets and worker packaging | [Build guide](build/README.md) |
| Test conventions and optional hardware certification | [Testing](tests/README.md) |
| Architecture and project responsibilities | [Architecture](docs/architecture.md), [dependency map](docs/project-dependencies.md) |
| Development and stable publication | [Release workflows](docs/releases.md) |

## Contributing

Want to help develop Lineup? Start with [CONTRIBUTING.md](CONTRIBUTING.md) for source setup, bug and feature contributions,
pull requests, detailed coding and testing standards, and the required validation gate.
Production projects live under `src/Apps`, `src/Core` and `src/Plugins`; `tests` mirrors those roles.
Live hardware certification is manually invoked, never part of normal solution builds or CI.

## Tech Stack

- **.NET 10** — target framework for all projects
- **Blazor Server** — interactive web UI
- **Entity Framework Core + SQLite** — EPG data caching
- **Spectre.Console** — terminal UI rendering
- **Serilog** — structured logging
- **Jellyfin FFmpeg** — live TV stream transcoding with AC-4 decoding

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE).
