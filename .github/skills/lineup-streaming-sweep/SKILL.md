---
name: lineup-streaming-sweep
description: Run and interpret Lineup's manually invoked Docker and HDHomeRun streaming certification sweeps. Use for channel sweeps, streaming matrix validation, targeted live-channel checks, compatibility checks, and sweep report triage.
---

# Lineup streaming sweep

Use the checked-in harness at `tests/Integration/Lineup.Streaming.Sweep`. Its synthetic HDHomeRun-compatible test server is the sibling project at `tests/Integration/Lineup.Streaming.Sweep.FaultSource`; the harness publishes it automatically for fault scenarios.

## Safety rules

- Never add this project to `Lineup.slnx`, `dotnet test`, or CI/CD. The live sweep requires local physical tuner access.
- Never hard-code a tuner address, browser path, output path, or machine-specific repository path.
- Do not run multiple sweeps concurrently. Each run requires exclusive access to the physical tuners.
- Do not start a live run while any tuner is in use. The harness waits for all four tuners to become idle and fails rather than interrupting active viewers.
- Run one compatibility profile at the beginning of each matrix only. Do not invoke separate compatibility checks between channels.
- Leave generated matrices, browser profiles, logs, screenshots, and reports beneath the configured output directory. Do not add them to source control.
- Do not treat protected content or transient source availability as Lineup playback defects. Use the structured outcome category, HTTP status, code, stage, and message.
- Stop and report the exact artifact path if cleanup fails or the tuners do not return to idle.

## Determine the requested scope

Choose the smallest command that answers the request:

| Request | Command |
|---|---|
| Verify harness outcome logic without hardware | `verify-outcome-reporting` |
| Validate one channel in local management hosting | `execute-local-channel <channel>` |
| Validate one channel across all six matrices | `execute-channel <channel>` |
| Validate selected channels across all six matrices | `execute-channels <channel>...` |
| Validate selected channels across remote matrices | `execute-remote-channels <channel>...` |
| Quick live sanity check using the first enabled channel | `smoke` |
| Run a new complete six-matrix sweep | `execute-fresh` |
| Resume the current complete sweep | `execute` |

Use `execute-fresh` only when the user explicitly requests a new full sweep. Prefer targeted commands while developing a fix.

## Prepare a live run

1. Read `tests/Integration/Lineup.Streaming.Sweep/README.md`.
2. Confirm Docker is available.
3. Require `LINEUP_SWEEP_TUNER_ADDRESS`. If it is unset and the user has not supplied the tuner address, ask for it rather than guessing.
4. Build a current image from the repository root:

   ```powershell
   docker build -t lineup-sweep:current -f Dockerfile .
   ```

5. Optionally honor:
   - `LINEUP_SWEEP_IMAGE`
   - `LINEUP_SWEEP_BROWSER_EXECUTABLE`
   - `LINEUP_SWEEP_OUTPUT_ROOT`
6. Build the harness:

   ```powershell
   dotnet build tests\Integration\Lineup.Streaming.Sweep\Lineup.Streaming.Sweep.csproj -c Release
   ```

   For synthetic fault scenarios, also verify the sibling source builds:

   ```powershell
   dotnet build tests\Integration\Lineup.Streaming.Sweep.FaultSource\Lineup.Streaming.Sweep.FaultSource.csproj -c Release
   ```

## Execute

Run from the repository root:

```powershell
dotnet run --project tests\Integration\Lineup.Streaming.Sweep\Lineup.Streaming.Sweep.csproj -c Release --no-build -- <command> [channels]
```

Do not impose an external timeout shorter than the harness operation. A full sweep can take hours. Use the process output and generated event journal for status rather than interrupting a healthy run.

## Interpret results

The default artifact root is `out\integration\streaming-sweep`. Generated matrix configuration lives at:

- `out\integration\streaming-sweep\matrices.json`
- `out\integration\streaming-sweep\matrices\<matrix-id>\`

Run evidence lives under `out\integration\streaming-sweep\runs`. The repository's existing `[Oo]ut/` rule excludes the default output root. The harness rejects repository-internal output paths outside `out/`.

Each run includes:

- `run.json` — image, tuner, matrix, and execution metadata
- `events.jsonl` — durable per-attempt and terminal event journal
- `results.json` — typed terminal results
- `results.csv` — tabular results
- `summary.md` — grouped outcome summary
- per-matrix compatibility results, browser traces, screenshots, network logs, player metrics, active-stream status, and compose logs

Classify outcomes as follows:

- `pass` — startup succeeded and required AV/caption evidence passed
- `protected-content` — expected terminal outcome; no retry
- `source-availability` — no signal, no data, or capacity; retryable and not automatically a product defect
- `product-startup-defect` — Lineup returned a non-availability startup failure
- `product-playback-defect` — HTTP startup succeeded but attachment or decoded playback failed
- `harness-failure` — navigation, request transport, client cancellation, or no response

The exact CMAF start response is authoritative. Do not replace a structured response with a later player-locator timeout.

## Report progress and completion

For status requests, report:

- current matrix
- current channel and attempt
- completed terminal results versus total expected
- elapsed time
- ETA based on completed result rate, clearly labeled as an estimate
- any active structured failure category

At completion, report:

- run artifact path
- matrix/channel totals
- AV and caption totals
- grouped startup outcomes
- cleanup and tuner-idle status
- commands used

Do not stage, commit, or publish sweep changes or artifacts unless the user explicitly requests that Git action.
