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

Physical acquisition timeouts use the same public `source_unavailable` code in management and broker
hosting; candidate-specific timeout codes remain in server diagnostics. The harness also recognizes
the historical `source_acquisition_timeout` code as source availability, without treating generic
HTTP 502/503 failures as source outages.

Timestamp/manifest agreement uses one tick of the coarsest contributing clock: DASH, MP4 media,
and the movie clock only when a nonzero empty edit contributes to presentation timing.
Decode continuity always retains media-clock precision. This is quantization handling, not a
subjective lip-sync allowance; differences beyond that precision remain failures.

Fresh synchronization explicitly retains completed browser response bodies in
`synchronization-resources.zip`, independently of the diagnostic Playwright trace. The separately
fetched owned DASH manifest remains mandatory; a redundant trace manifest without a body does not
invalidate complete owned media. Missing initialization or selected media is still inconclusive.
Each body capture is bounded by the startup deadline, and metadata collection also respects the
remaining observation budget; a stalled response is retained as a capture failure, not a fragment.
`synchronization-window.json` records the final uninterrupted interval and its completed fragments.
Only that interval's final owned session and observed representations are evaluated; earlier sessions
cannot fill in missing tracks, and switches within the interval remain ambiguous. Watch navigation
waits for DOM readiness and the channel control, not a network-idle condition on a live application.

Exit 130 is reserved for an actual cancellation request. HTTP/dependency timeouts, including
`TaskCanceledException` without user cancellation, finish as infrastructure failure (exit 3).
Canonical execution persists `inconclusive` and `FinishedUtc` after a teardown timeout instead
of leaving a finished checkpoint marked `running`; cleanup failures are never waived by this classification.

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
| `--max-sequence-delta <n>` | 1 inclusive legacy sequence diagnostic threshold; does not determine timestamp integrity or lip-sync |
| `--max-product-failures <n>` | Unset: product findings reported without a hard failure threshold |
| `--browser <path>` / `--profile <directory>` | Browser executable / persistent profile; use an output directory under `out` |
| `--output <directory>` | `LINEUP_SWEEP_OUTPUT_ROOT` or `out\integration\streaming-sweep` |
| `--setup-compatibility` | Explicitly permit synthetic Watch Test setup on an existing deployment |
| `--allow-matrix-recovery` | Explicitly permit owned Compose teardown/recreation after contamination; **never** existing instances |
| `--continue-after-verified-cleanup` | Default off: continue after an emergency owned-session stop only after passive ownership/runtime and viewer/capacity baseline reverification, plus physical tuner-idle verification for Docker; the lifecycle failure remains permanent |
| `--sync-only` | Fresh transport observation without a separate preceding playback pass |
| `--input-run <sweep-run.json>` | Required for explicit resume, failed-channel retry and offline regeneration |

`execute-channel`, `execute-channels`, `execute-local-channel`, `execute-remote-channels`, `execute`,
`execute-fresh`, `smoke`, `generate`, and `verify-issue1` through `verify-issue10` remain available.
`execute` may resume its current canonical run; `execute-fresh` always creates a new run.
`smoke` selects the first projected channel in `local-management`.
Issue regression commands retain their existing focused reports and synthetic fault source.
Use the same configured output root to find the `current.txt` pointer.

### Collecting remaining results after emergency cleanup

Default execution still stops on any forced or unverified cleanup. To collect remaining channel
and matrix observations after **successful emergency cleanup**, explicitly opt in on a new run:

```powershell
dotnet run --project tests\Integration\Lineup.Streaming.Sweep\Lineup.Streaming.Sweep.csproj -c Release --no-build -- execute-fresh --continue-after-verified-cleanup
```

This does not waive the failure or restart the deployment. Continuation requires verified owned
session absence, valid runtime identity, no unresolved browser/mandatory-evidence failures, and a
fresh passive check of the captured viewer/capacity baseline. Docker also requires the discovered
physical receivers to be idle. Missing ownership/baseline evidence, a changed runtime, or a failed
verification cannot be treated as safe continuation. Existing-instance mode preserves unrelated
viewers and compares against their baseline, never a globally idle target.

`--allow-matrix-recovery` is separate permission to recreate an **owned Docker** deployment.
Playback, fresh sync and resumed sync use the same lifecycle/recovery handling. Recovery consumes
the next attempt within `--attempts` for that observation; it is not an additional unbounded retry
loop. Recovery preserves evidence and the browser's existing compatibility profile instead of
rerunning synthetic compatibility setup between channels. Teardown must verify tuner idleness,
and preflight and frozen channel-inventory validation must succeed before retrying. A failed teardown
or unproven tuner-idle check stops the run. Existing-instance mode never permits deployment recovery.

When both options are enabled, verified continuation is tried before deployment recovery.
Every emergency stop and recovery remains a permanent lifecycle finding, even when later media
observations pass. A complete run with these findings exits **2**, never 0; an aborted/incomplete
run exits **3**. Historical failed attempts remain in canonical JSON, CSV and Markdown reports.
Continuation permission is persisted with the effective options; resume cannot enable it on an
older run whose options did not request it.

The normal Watch-stop grace interval remains at most 10 seconds (or one third of a shorter cleanup
deadline). `--cleanup-seconds` controls the overall cleanup/baseline deadline, not an unlimited
normal-stop grace interval.
The Watch page remains open after clicking Stop while ownership-qualified passive inventory and
the resource baseline are checked; a completed browser click is not a completed Blazor handler.
The page is closed only after the cleanup attempt and evidence capture. `console.json` records
whether the Stop button was found and the cleanup duration, and `network.json` retains browser stop requests and responses,
including successful responses. Emergency cleanup remains separate and never counts as normal shutdown.

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
stops to another runtime generation. An unfinished attempt with no startup requests, responses, ownership,
baseline, artifacts or other runtime evidence can wait for owned-deployment recreation and preflight before
being marked interrupted; its channel still requires a fresh attempt. Recorded runtime-bound evidence
continues to require the previous runtime before recreation. If that deployment is no longer reachable,
ownership cannot be proven absent: retain the evidence and explicitly start a new run after human reconciliation.
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
Startup, decoded AV and transport alignment remain separate from cleanup. An emergency stop cannot
rewrite HTTP 200 as a startup failure or discard already collected alignment measurements.
Post-start playback and metadata failures are labeled `playback-failure` and `metadata-failure`
in synchronization evidence, not `startup-failure`. `CleanupContinuationVerified` records explicit
post-cleanup reverification. Older schema-one checkpoints default this field and the new option to
false. CSV includes separate cleanup intervention, absence, continuation and error columns; selected
Markdown rows flag lifecycle failures even if the accepted media result passed.
Each observation also writes independent `cleanup.json` evidence beside `result.json`.
Standalone issue-regression scenarios retain strict cleanup failure behavior.
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
`chunk-<representation>-<sequence>.m4s` names. Sequence numbers are track-local diagnostics:
different segment durations and numbering origins are valid, and HTTP response order never clips a track's head.
`--max-sequence-delta` only sets `SequenceDiagnosticExceeded`; it cannot certify or fail media timing.

Every fresh observation retains `trace.zip`, `synchronization-manifest.mpd` and `synchronization.json`,
including successful playback. The trace supplies initialization and completed media response bodies and
browser-requested DASH snapshots. For both DASH and HLS, the current owned startup's public DASH manifest
is also fetched and saved explicitly; browser DASH requests are not required. Origin/session validation
and final-startup correlation reject stale/fallback evidence.

`media-timeline-v1` checks actual MP4 decode times, sample durations, composition offsets and edit lists,
normalizing DASH timescale, Period start and presentation-time offset. Each active track requires two
distinct fragments, an adjacent numbered pair and two manifest matches; the two tracks need overlapping
presentation coverage. The tolerance is one clock tick (the coarser of media and manifest clocks), not a
perceptual millisecond threshold. Gaps/overlaps between observed adjacent decode intervals or disagreement
with the manifest produce `timeline-discontinuity`. Unsupported/missing timing, ambiguous active tracks,
or insufficient coverage produce `insufficient-samples`; neither can manufacture a pass.
Fragments outside retained rolling manifest windows still contribute decode continuity but not manifest-match counts.
Video-only content has no measurable A/V pair. Timestamp-integrity failures fail the completed run's integrity gate.

Historical schema-one runs remain readable and retain their original sequence-only `aligned`/`misaligned`
labels. New execution records `TimestampEvidenceVersion=1`; old measurement-policy checkpoints cannot
resume under the new policy. Use an explicit new targeted run instead.
Fresh-sync product startup, timeout and decoder findings count toward configured product-failure
limits once per channel; an unavailable source on the retest remains availability.
Transport integrity and buffered presentation endpoints are **not rendered/audible lip-sync measurements**.
`LipSync=not-measured` remains explicit even when the timelines are continuous. Perceptual certification
requires a known synchronized flash/beep source and correlated audiovisual output, not sequence numbers,
audio peaks, `currentTime`, or comparing the last buffered timestamps.

Encoded video forces each keyframe at least two seconds after the preceding forced keyframe. Absolute
two-second targets can round to a shorter interval at fractional frame rates, making the DASH muxer
skip a boundary and emit a roughly four-second segment. Source-copy renditions are unchanged.
Player timestamp mode is intentionally unchanged; continuous packaged timestamps do not establish that
changing the existing browser workaround is safe.

Watch stop/navigation is attempted first. Only still-owned viewers may receive an emergency POST at
the validated returned stop URL (management responses without a stop URL use Watch's management route).
Forced cleanup, leaked/unproven ownership, browser failures, unresolved capacity, invalid totals and
output failures cannot be waived by product thresholds. Explicitly permitted safe continuation affects
whether more observations run, never certification success. Opted-in Compose recovery preserves logs and
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
