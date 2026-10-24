---
name: lineup-streaming-sweep
description: Run and interpret Lineup's manually invoked Docker and HDHomeRun streaming certification sweeps. Use for channel sweeps, streaming matrix validation, targeted live-channel checks, compatibility checks, and sweep report triage.
---

# Lineup streaming sweep

Use the checked-in harness at `tests/Integration/Lineup.Streaming.Sweep`. Its synthetic HDHomeRun-compatible test server is the sibling project at `tests/Integration/Lineup.Streaming.Sweep.FaultSource`; the harness publishes it automatically for fault scenarios.

## Safety rules

- Never add the live executable or fault source as solution/CI entry points. The separate non-live `Lineup.Streaming.Sweep.Tests` project is in the normal gate.
- Never hard-code a tuner address, browser path, output path, or machine-specific repository path.
- Do not run multiple sweeps concurrently. Each run requires exclusive access to the physical tuners.
- Docker matrices require all discovered tuner receivers idle; no four-tuner assumption. Existing-instance mode preserves and verifies the pre-existing viewer baseline instead of requiring globally zero streams.
- Run one compatibility profile at the beginning of each matrix only. Do not invoke separate compatibility checks between channels.
- Leave generated matrices, browser profiles, logs, screenshots, and reports beneath the configured output directory. Do not add them to source control.
- Do not treat protected content or transient source availability as Lineup playback defects. Use the structured outcome category, HTTP status, code, stage, and message.
- Stop and report the exact artifact path if cleanup fails or the tuners do not return to idle.
- Existing-instance mode must never restart/reconfigure/reset/refresh the guide or stop unrelated viewers. Compatibility setup requires `--setup-compatibility`.
- Docker leak recovery requires `--allow-matrix-recovery`; it permanently fails the lifecycle invariant even if a later attempt succeeds.

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
| Attach without deployment changes | `existing --base-url <origin> --profile <output-profile>` |
| Fresh synchronization observation only | `sync-only --include <numbers> --profile <output-profile>` |
| Resume an explicit checkpoint | `resume --input-run <sweep-run.json>` |
| Retry failed/pending channels from explicit input | `recheck-post-fix-failures --input-run <sweep-run.json>` |
| Regenerate reports entirely offline | `report-only --input-run <sweep-run.json>` |

Use `execute-fresh` only when the user explicitly requests a new full sweep. Prefer targeted commands while developing a fix.

## Prepare a live run

1. Read `tests/Integration/Lineup.Streaming.Sweep/README.md` for the authoritative command/options/exit contract. Existing-instance and offline modes do not require Docker or a tuner environment variable.
2. For Docker modes, confirm Docker is available.
3. For Docker modes, require `LINEUP_SWEEP_TUNER_ADDRESS`. If it is unset and the user has not supplied the tuner address, ask for it rather than guessing.
4. For Docker modes, build a current image from the repository root:

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

- `sweep-run.json` — versioned atomic canonical checkpoint with effective options, environment, frozen projections, all attempts and ownership
- `events.jsonl` — durable per-attempt and terminal event journal
- `results.json` — canonical run export (not the legacy result array)
- `results.csv` — tabular results
- `summary.md` — grouped outcome summary
- per-matrix compatibility results, browser traces, screenshots, network logs, player metrics, active-stream status, and compose logs

Canonical schema 1 is required for resume/offline regeneration. Changed target/runtime/topology/profile/options
or malformed journals are not silently mixed with prior evidence. Ctrl+C requests bounded cleanup and partial
reports, exiting 130. Exit 2 means an invariant/threshold failure; exit 3 means incomplete/preflight/infrastructure
failure. A missing or invalid cleanup inventory is inconclusive, never assumed success. Dedicated listeners
require the passive `/media/sessions` API; it does not refresh session lifetime.

Classify outcomes as follows:

- `pass` — startup succeeded and required AV/caption evidence passed
- `protected-content` — expected terminal outcome; no retry
- `source-availability` — structured no-signal/no-data/unavailable source, not generic HTTP 502/503
- `capacity-contamination` — unresolved resource baseline; never an accepted channel outcome
- `product-startup-defect` — Lineup returned a non-availability startup failure
- `product-playback-defect` — HTTP startup succeeded but attachment or decoded playback failed
- `harness-failure` — navigation, request transport, client cancellation, or no response

The exact CMAF start response is authoritative. Do not replace a structured response with a later player-locator timeout.
Canonical typed verdicts additionally distinguish fallback-start failures, confirmed/inferred/unconfirmed
protection, disabled/unknown generated slates, decoder failures and observation expiry. HTTP `inferred`
and `source` and the current owned synthetic-flow reason are retained; generated transport is never a
broadcast playback pass. Fresh sync requires two distinct successful fragments per active audio/video
representation, with default absolute sequence delta <= 1. DASH and HLS playback both use the current
owned startup's public DASH manifest solely for metadata, rejecting stale/fallback session mappings.
Insufficient samples are not alignment evidence; transport alignment is not audible lip-sync certification.

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
