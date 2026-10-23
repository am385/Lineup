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

The live executable and fault source are intentionally excluded from `Lineup.slnx` and CI/CD.
The separate `Lineup.Streaming.Sweep.Tests` xunit project is in the solution; it references the executable
for deterministic policies only. Building that reference does not execute it or install/launch a browser.
Normal test discovery, constructors and tests require no Docker, browser, network endpoint or hardware.

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

## Executable contract

Use .NET 10 (the repository's pinned SDK) and the project's pinned Playwright package.
Install Playwright's matching Chromium into `out` if no installed browser is available:

```powershell
dotnet build tests\Integration\Lineup.Streaming.Sweep\Lineup.Streaming.Sweep.csproj -c Release
$env:PLAYWRIGHT_BROWSERS_PATH = Join-Path $PWD "out\playwright"
pwsh out\bin\Lineup.Streaming.Sweep\release\playwright.ps1 install chromium
dotnet test tests\Integration\Lineup.Streaming.Sweep.Tests\Lineup.Streaming.Sweep.Tests.csproj -c Release
```

Browser validation occurs before starting owned services. TLS errors are not bypassed: install a trusted
certificate and configure reachable public listener URIs, allowed networks, CORS and rate limits.
Status diagnostics must expose the selected projection, capacity graph and viewer identifiers.
A dedicated Web Player must provide the passive `GET /media/sessions` inventory added with this harness.
It returns generation and session IDs only, under the listener's existing TLS, network and rate-limit
policies. It does not extend media inactivity leases. Older workers without it are inconclusive, not
assumed clean. Management uses the existing `GET /api/stream/cmaf/sessions` endpoint.

### Existing deployment (no Docker required)

```powershell
dotnet run --project tests\Integration\Lineup.Streaming.Sweep\Lineup.Streaming.Sweep.csproj -c Release --no-build -- existing --max-channels 1 --profile out\sweep-profile --setup-compatibility
dotnet run --project tests\Integration\Lineup.Streaming.Sweep\Lineup.Streaming.Sweep.csproj -c Release --no-build -- existing --base-url http://localhost:8080 --include 104.1,122.1 --profile out\sweep-profile
dotnet run --project tests\Integration\Lineup.Streaming.Sweep\Lineup.Streaming.Sweep.csproj -c Release --no-build -- sync-only --include 104.1 --profile out\sweep-profile
```

`existing` defaults to `http://localhost:8080`. It never starts/stops/restarts services, edits settings,
resets state or refreshes the guide. Compatibility setup is **opt-in**; without `--setup-compatibility`
a complete current profile matching the browser identity must already exist in `--profile`.
Compatibility sessions are separately tracked and verified cleaned.
Pre-existing viewers are recorded, never stopped. Other viewers may continue watching; changes to
their baseline during a measurement are reported as interference/inconclusive rather than attributed
to this channel. A shared producer is not an owned viewer: cleanup waits for capacity to return to the
baseline, allowing layered keep-warm expiry without releasing another consumer.

Only one sweep should run at a time (including runs with different output roots). Each output root
has a process-held deployment lock, and each canonical run has an exclusive lease shared by execution,
resume and report regeneration regardless of output-root overrides. Docker matrices require exclusive physical tuner access; they
discover the tuner's capacity instead of assuming four receivers.

### Options

All execution aliases accept these named options; comma-separated filters match projected numbers
case-insensitively. The authoritative identity is target + virtual device + projected number.
Missing requested channels, duplicate keys, conflicts and zero filter matches fail preflight.

| Option | Default / meaning |
|---|---|
| `--base-url <origin>` / `--existing-instance` | Attach to an existing deployment; default origin `http://localhost:8080` |
| `--include <numbers>` / `--exclude <numbers>` | Select or omit projected channels |
| `--max-channels <n>` | All selected channels; positive smoke limit |
| `--matrices <ids>` | All six Docker matrices; not permitted in existing-instance mode |
| `--startup-seconds <n>` | 60; CMAF response deadline |
| `--stable-seconds <n>` | 30 continuous seconds, reset on stalls, errors or session replacement |
| `--media-advance-seconds <n>` | 15 within the continuous interval |
| `--observation-seconds <n>` | 120; independent media observation deadline |
| `--cleanup-seconds <n>` | 120; includes nested warm expiry; increase for longer configured warm periods |
| `--attempts <n>` | 3 maximum attempts, bounded to 1-20 |
| `--retry-delay-seconds <n>` | 2 |
| `--max-sequence-delta <n>` | 1 inclusive transport-fragment sequence difference |
| `--max-product-failures <n>` | Unset: product findings reported without a hard failure threshold |
| `--browser <path>` / `--profile <directory>` | Browser executable / persistent profile; use an output directory under `out` |
| `--output <directory>` | `LINEUP_SWEEP_OUTPUT_ROOT` or `out\integration\streaming-sweep` |
| `--setup-compatibility` | Explicitly permit synthetic Watch Test setup on an existing deployment |
| `--allow-matrix-recovery` | Explicitly permit owned Compose teardown/recreation after contamination; **never** existing instances |
| `--sync-only` | Fresh transport observation without a separate preceding playback pass |
| `--input-run <sweep-run.json>` | Required for explicit resume, failed-channel retry and offline regeneration |

`execute-channel`, `execute-channels`, `execute-local-channel`, `execute-remote-channels`, `execute`,
`execute-fresh`, `smoke`, `generate`, and `verify-issue1` through `verify-issue10` remain available.
`execute` may resume its current canonical run; `execute-fresh` always creates a new run.
`smoke` selects the first projected channel in `local-management`.
Issue regression commands retain their existing focused reports and synthetic fault source.
Use the same configured output root to find the `current.txt` pointer.

### Interruption, retry and offline reports

```powershell
dotnet run --project tests\Integration\Lineup.Streaming.Sweep\Lineup.Streaming.Sweep.csproj -c Release --no-build -- resume --input-run out\integration\streaming-sweep\runs\<run-id>\sweep-run.json
dotnet run --project tests\Integration\Lineup.Streaming.Sweep\Lineup.Streaming.Sweep.csproj -c Release --no-build -- recheck-post-fix-failures --input-run out\integration\streaming-sweep\runs\<run-id>\sweep-run.json
dotnet run --project tests\Integration\Lineup.Streaming.Sweep\Lineup.Streaming.Sweep.csproj -c Release --no-build -- report-only --input-run out\integration\streaming-sweep\runs\<run-id>\sweep-run.json
```

Ctrl+C requests graceful cancellation: new observations stop, independent bounded cleanup runs and
partial canonical reports are written. Do not force-kill a healthy cleanup. Resume uses persisted
effective options, verifies inventory, image/application/topology/profile/preference provenance,
reconciles unfinished ownership and assigns new immutable attempt IDs. It refuses to send emergency
stops to another runtime generation. If a crashed deployment is no longer reachable, ownership
cannot be proven absent: retain the evidence and explicitly start a new run after human reconciliation.
Owned matrix generations are recorded separately when normally recreated; existing-instance runtime
changes require a new run.

Failed-channel retry takes its endpoint/matrices and effective policy from the explicit input run,
not a historical machine-specific directory. It selects pending/failed playback and unresolved mandatory
evidence, including missing or insufficient fresh synchronization and fresh-sync startup, decoder, or
alignment failures. A fresh-sync source-unavailable result remains availability, not a product failure.
Offline `report-only` accepts only input/output options,
requires **no** browser, Docker, tuner or running deployment, and deterministically regenerates reports.
It rejects an actively leased input/output run and never rewrites its authoritative input checkpoint.
Schema 1 canonical objects are supported. Legacy result arrays/journals are not silently upgraded.
An incomplete last JSONL record may be truncated; interior corruption and malformed tails are errors.

### Verdicts, invariants and output

`sweep-run.json` is the versioned canonical checkpoint; `results.json` exports the same object,
not the previous unversioned terminal array. JSON, CSV and Markdown are atomically replaced using
same-directory temporary files and flushed writes. `events.jsonl` remains append-only and durable.
Every startup request/response, source/fallback owner, discarded attempt, accepted playback reference,
independent caption reference, cleanup result and fresh sync result retains its attempt provenance.
Browser attempt results carry `EvidenceId`, independently of their numeric retry ordinal. Diagnostic
readers use that exact identity under `attempts\<channel>\<EvidenceId>`; they never guess from numeric
or older directories. Historical results without this additive field remain loadable, but cannot be
used for identity-dependent diagnostic reads.
`summary.md` contains one uniquely keyed row for **every selected channel**, explicitly marking pending
rows, plus environment/options, media metrics, lifecycle findings and evidence limitations.

Stable verdicts distinguish playback, structured source unavailability, startup timeout, initial server
failure, fallback-start failure, confirmed/inferred protection, decoder failure, capacity contamination,
sync misalignment, observation timeout and infrastructure failure. Generic HTTP 502/503 is **not**
proof of off-air content. Confirmed DRM requires explicit server evidence; an inferred label must never
be presented as confirmed. Startup HTTP evidence retains `inferred` and `source`; only explicit
`inferred=false` establishes confirmed protection. Missing inference remains `UnconfirmedDrm`.
Synthetic protected/disabled slates preserve the owned flow's source reason and are recorded separately
from broadcasts (`UnconfirmedDrm`, `DisabledSlate`, or `UnknownSynthetic` when the reason is unknown).
`AV` remains the decoded transport check used by Issue 6's slate regression; it is not a broadcast pass.
Broadcast pass totals exclude known synthetic playback, even when that transport check succeeds.
When audio is required, the browser must report a positive decoded-audio byte delta or a positive
analyser peak. Missing, zero, or negative counters alone do not establish an audio pass. Explicit
video-only observations retain their existing exemption.

For every accepted playback pass a separate fresh tune observes successful fragments with
`chunk-<representation>-<sequence>.m4s` names. Current DASH manifests map the observed audio/video
representations. For both DASH and HLS playback, the current owned startup's public DASH manifest is
read solely for representation metadata; the browser need not request DASH. Origin/session validation
and final-startup correlation reject stale or fallback metadata. Two distinct fragments per active
stream, one session/rendition pair and overlapping observation windows are required. Unmappable or
changing pairs report insufficient samples, not an invented alignment. Video-only content has no measurable A/V pair.
Fresh-sync product startup, timeout and decoder findings count toward configured product-failure
limits once per channel; an unavailable source on the retest remains availability.
Transport alignment is **not human audible lip-sync assessment**.

Watch stop/navigation is attempted first. Only still-owned viewers may receive an emergency POST at
the validated returned stop URL (management responses without a stop URL use Watch's management route).
Forced cleanup, leaked/unproven ownership, browser failures, unresolved capacity, invalid totals and
output failures cannot be waived by product thresholds. Opted-in Compose recovery preserves logs and
all prior attempts; any restart needed to release capacity permanently fails the run, even after a
successful rerun. Normal teardown of services created for a Docker matrix is not leak recovery.

| Exit | Meaning |
|---|---|
| 0 | Complete valid run with no invariant or configured threshold failure; expected unavailable/protected channels are allowed |
| 2 | Complete observations but failed lifecycle/integrity/product-threshold invariant |
| 3 | Invalid input, preflight/infrastructure failure, incomplete or inconclusive evidence |
| 130 | Gracefully cancelled; inspect checkpoint and cleanup before resume |

`completed.marker` is emitted only after all final reports validate successfully; it never overrides
the canonical run state or exit policy. Full runs may take hours: each channel can require three
observations plus a fresh sync tune, warm drain and compatibility setup per matrix.
Artifacts include local network/device metadata, browser profiles, request diagnostics, traces and
screenshots; treat them as sensitive local evidence. Never publish them without review.

## Validation

Run focused deterministic tests first, then the complete `AGENTS.md` gate. Also build/format the
excluded live projects explicitly:

```powershell
dotnet build tests\Integration\Lineup.Streaming.Sweep\Lineup.Streaming.Sweep.csproj -c Release --no-restore
dotnet build tests\Integration\Lineup.Streaming.Sweep.FaultSource\Lineup.Streaming.Sweep.FaultSource.csproj -c Release --no-restore
dotnet format tests\Integration\Lineup.Streaming.Sweep\Lineup.Streaming.Sweep.csproj --verify-no-changes --no-restore
dotnet format tests\Integration\Lineup.Streaming.Sweep.FaultSource\Lineup.Streaming.Sweep.FaultSource.csproj --verify-no-changes --no-restore
```

Deterministic tests alone do not certify any live hosting topology. Record exact live commands,
artifact paths, cleanup evidence and unverified behaviors in the handoff.
