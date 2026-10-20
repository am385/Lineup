# Streaming channel sweep

This integration harness validates Lineup's Web Player streaming behavior across management-hosted, managed-process, external-process, and remote Lineup Link topologies.

Agents should follow [`.github/skills/lineup-streaming-sweep/SKILL.md`](../../../.github/skills/lineup-streaming-sweep/SKILL.md) for scope selection, safety checks, execution, progress reporting, and result interpretation.

## Local deterministic verification

The outcome-reporting verification can be run locally without Docker, a browser, or tuner hardware:

```powershell
dotnet run --project tests\Integration\Lineup.Streaming.Sweep\Lineup.Streaming.Sweep.csproj -c Release -- verify-outcome-reporting
```

It verifies terminal protected-content handling, retryable source-availability handling, and aggregation of the strongest AV and caption evidence.
This is also the default command when no command argument is supplied.

The project is intentionally excluded from `Lineup.slnx` and CI/CD. GitHub-hosted runners cannot reach the physical tuner hardware, and a partial pipeline run would not certify the six live streaming matrices.

## Project layout

The sweep application is organized by responsibility:

- `Configuration` generates matrix settings and Docker Compose definitions.
- `Execution` drives browser playback, startup outcome handling, and retry policy.
- `Infrastructure` owns processes, browsers, journals, paths, reports, and cleanup.
- `Models` contains the typed matrix, result, and outcome contracts.
- `Scenarios` contains the focused regression workflows and synthetic-source integration helpers.

The synthetic HDHomeRun-compatible source is a separate sibling project at
`tests\Integration\Lineup.Streaming.Sweep.FaultSource`. It provides deterministic protected-content,
source-availability, delayed-audio, and video-only scenarios. The sweep publishes that project into
the generated `fault-source\publish` directory when a scenario needs it.

## Live hardware sweep

Live commands require:

- Docker
- an HDHomeRun device reachable from Docker containers
- Chromium installed by Playwright or an installed Chromium-family browser
- exclusive use of the physical tuners for the duration of the run
- a current `lineup-sweep:current` image

Build the image from the repository root:

```powershell
docker build -t lineup-sweep:current -f Dockerfile .
```

Configure the tuner address:

```powershell
$env:LINEUP_SWEEP_TUNER_ADDRESS = "192.0.2.10"
```

Optionally override the image, browser executable, or output directory:

```powershell
$env:LINEUP_SWEEP_IMAGE = "lineup-sweep:current"
$env:LINEUP_SWEEP_BROWSER_EXECUTABLE = "C:\Program Files\Google\Chrome\Application\chrome.exe"
$env:LINEUP_SWEEP_OUTPUT_ROOT = "D:\lineup-sweep-results"
```

Run one compatibility profile at the beginning of each matrix and sweep the complete channel inventory:

```powershell
dotnet run --project tests\Integration\Lineup.Streaming.Sweep\Lineup.Streaming.Sweep.csproj -c Release -- execute-fresh
```

Useful narrower commands:

```powershell
dotnet run --project tests\Integration\Lineup.Streaming.Sweep\Lineup.Streaming.Sweep.csproj -c Release -- smoke
dotnet run --project tests\Integration\Lineup.Streaming.Sweep\Lineup.Streaming.Sweep.csproj -c Release -- execute-channel 104.1
dotnet run --project tests\Integration\Lineup.Streaming.Sweep\Lineup.Streaming.Sweep.csproj -c Release -- execute-remote-channels 104.1 122.1
```

## Generated artifact layout

Generated state lives beneath `out\integration\streaming-sweep` by default:

```text
out/integration/streaming-sweep/
├── matrices.json
├── matrices/
│   └── <matrix-id>/
│       ├── compose.yml
│       ├── templates/
│       └── runtime/
├── runs/
│   └── <run-id>/
└── fault-source/
    └── publish/
```

The repository's existing `[Oo]ut/` rule excludes the complete default output root. The harness rejects `LINEUP_SWEEP_OUTPUT_ROOT` values inside the repository unless they are beneath `out/`; external output directories remain supported.

Browser profiles, event journals, JSON/CSV/Markdown reports, screenshots, traces, and compose logs remain inside the selected output root. Do not copy generated sweep state into a tracked source directory.
