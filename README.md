# Lineup

**Lineup** is a .NET application that fetches Electronic Program Guide (EPG) data from a network TV tuner device and converts it into [XMLTV](http://wiki.xmltv.org/index.php/XMLTVFormat) format — the standard format used by media servers like **Jellyfin** and **Plex** for TV guide display.

It includes a web-based dashboard, a terminal UI, live TV streaming with transcoding, and automatic scheduled EPG fetching with smart incremental updates.

> Currently this is only supporting the [HDHomeRun](https://www.silicondust.com/) Tuners like the HDHomeRun FLEX 4K. Hopefully more can be added in the future.

## Features

- **Automatic EPG fetching** — background service with configurable interval and target days; reacts to settings changes in real time
- **Incremental fetching** — determines a safe start time based on cached data, avoiding redundant API calls
- **XMLTV conversion** — full-featured output including episode numbering, categories, artwork, original airdates, and new-episode markers
- **Live TV streaming** — proxy with ffmpeg transcoding to HLS/fMP4 (profiles: mobile, heavy, internet 720/480/360)
- **Device diagnostics** — connectivity checks across DNS, ping, HTTP API, TCP, and UDP discovery
- **Native HDHomeRun protocol** — binary protocol implementation for UDP discovery, TCP control, and channel scanning
- **XMLTV endpoint** — `/api/xmltv` for external media servers to pull the guide file directly
- **SQLite caching** — EPG data stored locally with automatic cleanup of expired entries

## Architecture

| Project | Description |
|---|---|
| `Lineup.HDHomeRun.Device` | Local device communication — discovery, channel lineup, native binary protocol |
| `Lineup.HDHomeRun.Api` | Remote API client — fetches EPG data from `api.hdhomerun.com` |
| `Lineup.Core` | Core business logic — orchestration, XMLTV conversion, caching (EF Core + SQLite) |
| `Lineup.Web` | Blazor Server web app — dashboard, EPG guide viewer, settings, live TV |
| `Lineup.Tui` | Terminal UI — interactive menu using Spectre.Console |

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An HDHomeRun device on your local network
- ffmpeg (required for live TV transcoding; included in the Docker image)

### Running the Web App

```bash
cd src/Lineup.Web
dotnet run
```

The app starts on `http://localhost:8080` by default. Configure the HDHomeRun device address through the settings UI or via the `Lineup__DeviceAddress` environment variable.

### Running the TUI

```bash
cd src/Lineup.Tui
dotnet run
```

Configure via environment variables:

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

The Docker image includes ffmpeg for live TV transcoding. Data is persisted via a named volume at `/app/data`. Use `network_mode: host` if your HDHomeRun device requires local network discovery.

## Configuration

All settings are configurable through the web UI's settings page and persisted to `settings.json`:

| Setting | Description |
|---|---|
| Device Address | HDHomeRun device IP or hostname |
| XMLTV Output Path | Where to write the generated XMLTV file |
| Fetch Interval | Minutes between automatic EPG fetches |
| Target Days | Number of days of EPG data to fetch |

## Tech Stack

- **.NET 10** — target framework for all projects
- **Blazor Server** — interactive web UI
- **Entity Framework Core + SQLite** — EPG data caching
- **Spectre.Console** — terminal UI rendering
- **Serilog** — structured logging
- **ffmpeg** — live TV stream transcoding

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE).
