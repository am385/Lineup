# Lineup.Tools.PerformanceCollector

Manually invoked Windows profiling, its temporary elevated collector and repeatable cross-process analysis. Commands run from the repository root unless stated otherwise.

- [Windows performance profiling](#windows-performance-profiling)

## Windows performance profiling

Use **Windows Performance Recorder (WPR)** and **Windows Performance Analyzer (WPA)** for a complete cross-process CPU profile.
Attaching a managed profiler only to the Web host misses FFmpeg and dedicated listener work. WPR records system-wide sampled CPU stacks,
process/thread lifetimes and scheduler events, including children that start and exit between process polls. WPA is available in the
[Windows Performance Toolkit](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/windows-performance-recorder).
Hardware encoding does not imply hardware decoding or filtering: Lineup currently offloads encoding, while decoding, deinterlacing,
scaling, audio conversion and embedded-caption extraction can still consume CPU.

Run an optimized Release deployment outside the debugger. Identify the management host and any independently launched external listeners
or other local Lineup hosts; use their **actual PIDs**, not an executable-name match that might include unrelated deployments.
The host's children include discovery/listener workers, FFprobe, media/caption FFmpeg processes and owned-process lifetime helpers.
`OwnedMediaProcess` returns a helper `Process`; its `GetTargetProcessId` identifies the actual media/worker executable.
Use PID **and creation time** when identifying a process. Include each shared producer only once, regardless of viewer count.

The manually invoked capture helper neither starts nor stops applications, changes settings, accesses tuners, nor installs tools:

```powershell
# Run from an elevated PowerShell session. Substitute the selected process IDs.
.\scripts\Profile-Lineup.ps1 -RootProcessId 1234,5678 -Scenario gpu-captions -DurationSeconds 120 -IncludeGpu -IncludeDotNet
```

Default captures use the built-in CPU profile. Add `-IncludeIo` for disk/file/network investigation; collect expensive detail only where needed.
`-SampleIntervalSeconds` controls the supplementary process census, not ETW sampling. `-DurationSeconds` bounds the requested observation
(1-3600 seconds); CIM queries and WPR startup/merge add overhead outside that interval. The helper requires at least 2 GiB free space
on the local output volume and requests early stop below 512 MiB; these checks cannot guarantee space for a high-volume trace.
Use a sufficiently large local drive and a bounded scenario, not an unattended long-running recording.
Optional `-OutputDirectory` must name a **new** directory; repository-local output must be under `out`.

Each run defaults to `out\performance\<run-id>` and contains:

- `system.etl`, for authoritative cross-process CPU and lifetime analysis.
- `capture.json`, with roots/creation times, selected providers, GPU drivers, observation bounds, capture state and limitations.
- `markers.jsonl`, with UTC observation markers, and `process-census.csv`, with supplementary process identities and cumulative lifetime CPU/I/O.
- WPR command output and `wpr-temporary`, retained for recovery if saving the trace fails.

The helper uses a unique WPR instance and stops only that instance. It records existing default-session status and fails explicitly if
its selected profiles cannot start; it never cancels another capture. Ctrl+C attempts to save a partial trace. If stop/save fails, the
manifest identifies the owned instance and records `cleanup-failed`; preserve the temporary files and resolve **that instance** explicitly.
Do not treat `captured` as a performance verdict: lost ETW events, missing symbols and unobserved scenarios still need review.
Do not sum census rows: CPU values are cumulative since process creation; blank counters are unavailable, not zero.
Polling can miss short-lived processes or orphan ancestry.
Use ETW process lifetimes for those cases. An independently restarted external listener may need another explicit root/capture.
Raw ETL files include **system-wide private metadata** such as process paths, command lines and network activity. Keep them local; review
and redact before any sharing. Do not add credentials, environments, enrollment descriptors or unrestricted status dumps to the artifact bundle.

### Temporary elevated collector

For repeated captures without repeated UAC prompts, build Release, then start this **from an unelevated PowerShell** and approve
the single Windows UAC prompt for `Lineup.Tools.PerformanceCollector.exe`. The approving administrator must be the same Windows account;
alternate administrator credentials are not supported. Starting the collector does not start a recording or change Lineup:

```powershell
$collector = .\scripts\Start-PerformanceCollector.ps1
$session = $collector.SessionId
$tool = '.\out\bin\Lineup.Tools.PerformanceCollector\release\Lineup.Tools.PerformanceCollector.exe'
& $tool status --session $session

# These subsequent requests stay unelevated; substitute actual target PIDs.
.\scripts\Profile-Lineup.ps1 -CollectorSession $session -RootProcessId 1234,5678 -Scenario gpu-captions -DurationSeconds 120 -IncludeGpu -IncludeDotNet
& $tool stop --session $session
```

The launcher also saves its non-secret session descriptor under `out\performance-collector\<session>.json`, so an unelevated automation
can recover the session ID without another prompt. A saved descriptor is not proof of liveness: always use the authenticated status command.
If local script execution is blocked, use your operator-approved process-only execution policy; do not change machine policy for profiling.

The temporary process accepts only local same-account, same-Windows-session **status, capture and stop** requests. It uses an explicit pipe ACL,
a medium-integrity data pipe and peer identity checks; `PipeOptions.CurrentUserOnly` is unsuitable across elevation levels. Requests cannot supply scripts, commands,
executables, WPR profile names, privileged output paths, or deletion targets. Only the operating system's WPR is launched, with fixed provider
choices and a unique instance per recording. Lineup, its children, and the workload driver remain unelevated.
No service, scheduled task, credentials, UAC/policy change, network listener or persistent automation is installed.

Each observation is limited to 1-300 seconds, at most 16 explicit process roots and 20 captures per collector. It exits after ten idle minutes,
ninety minutes total, a stop request, or unconfirmed WPR cleanup. Captures are serialized: another status/stop cannot interrupt an in-progress
capture/export. WPR start allows one minute; trace save, source checksumming and normal export acknowledgment each allow ten minutes.
Closing a client before its request frame is complete does not terminate the collector session.
Expiry/Ctrl+C requests an owned-instance save before exiting; closing/killing the console or restarting Windows cannot guarantee cleanup.
Stop the collector before rebuilding its tool binaries, which remain locked against modification while it runs.

Elevated recording writes only to a new administrator-owned `Lineup.Performance.<session>` directory beneath Windows's Program Files directory.
The same account can read this private spool but its unelevated token cannot write it. The ordinary client copies allowlisted artifacts into
a **new** local output directory (default `out\performance`) and verifies lengths and SHA-256 checksums before acknowledging source cleanup.
Insufficient space, disconnects, failed exports or unconfirmed WPR cleanup are failures, not successful profiles. Unacknowledged/failed-cleanup
source files remain in the reported protected spool for recovery; do not delete them until exported and reviewed. Fully exported sessions remove
their empty spool on shutdown. The recorder requires 8 GiB free and requests early stop below 2 GiB; merging may still require more space.

Collector mode writes `system.etl`, `capture.json` and WPR logs; it does not produce the direct helper's supplementary census/marker journal or
GPU inventory. Use ETW lifetimes and manifest UTC bounds for attribution, including short-lived child processes and explicit external roots.
`-SampleIntervalSeconds` is rejected in collector mode instead of pretending a census was collected. The normal .NET gate tests the protocol
and local IPC with explicitly fake artifacts; it never elevates, records ETW, runs Lineup or uses tuners. A first approved elevated capture is
still required to verify cross-elevation access and WPR/provider availability on a particular machine.

### Managed-worker diagnostic opt-in

Managed workers normally launch with `DOTNET_EnableDiagnostics=0` and a sanitized environment. For an approved profiling deployment,
set the following **before starting the Web/TUI host**, then start the host through your normal operator-controlled procedure:

```powershell
$env:Lineup__VirtualOutputs__EnableWorkerDiagnostics = 'true'
```

This local launch configuration enables .NET diagnostic attachment in newly launched managed workers, including shared discovery;
it is not a persisted application preference and does not change settings/IPC versions. The trusted host logs a warning while enabled.
Invalid Boolean configuration fails configuration binding rather than silently enabling profiling. Worker environment isolation, bootstrap
delivery, process containment and resource limits remain unchanged; arbitrary diagnostic ports, profiler paths and startup hooks are not inherited.
An already running diagnostics-disabled worker needs an approved relaunch. For externally supervised workers, the operator must separately
enable `DOTNET_EnableDiagnostics=1` in that worker's launch environment. Do not expose a diagnostic port to the network.
Remove the host override and restart through the normal procedure when finished:

```powershell
Remove-Item Env:\Lineup__VirtualOutputs__EnableWorkerDiagnostics
```

WPR `-IncludeDotNet` adds runtime events for analysis in WPA. Complement it with installed `dotnet-trace`/`dotnet-counters` tools for
deeper managed analysis, attaching **separately to each selected host or worker PID**. These tools do not automatically profile all descendants
and do not replace native FFmpeg stacks. Check installed versions' `--help`/profiles/available counters; for example:

```powershell
dotnet-trace collect --process-id 1234 --profile cpu-sampling --duration 00:00:02:00 --output out\performance\<run-id>\host.nettrace
dotnet-counters collect --process-id 1234 --counters System.Runtime --duration 00:02:00 --format csv --output out\performance\<run-id>\host-counters.csv
```

Inspect allocation rate, GC time/pauses, heap size, ThreadPool activity, exceptions and contention as available.
Use existing `Lineup` meters/activities and low-frequency passive graph/worker/session observations for correlation; avoid enabling verbose
per-packet logging. Record attachment failures or missing counters as unavailable, not zero. Keep a separate low-overhead baseline to quantify
the profiling/census overhead.

### Repeatable audit and analysis

Record source codec/resolution/interlace/frame rate, effective physical and virtual plans, actual encoder/fallback state, audio/subtitle
renditions, app/worker/FFmpeg build identities, GPU/driver, power mode, resource limits, viewer count and unique producer count for each scenario.
Do compatibility setup and other warmup outside the observation interval. Repeat matched scenarios at least three times and report variation.
Use identical operator-approved unprotected input for controlled media comparisons where possible; label live-source variability separately.

Start with idle/no UI, dashboard open and keep-warm costs; then MPEG-TS copy, CMAF copy, matched software/GPU encoding, interlacing/cadence,
scaling and audio conversion. Measure management versus dedicated hosting, identical viewers sharing one producer, distinct channels/settings,
mixed Web Player/HDHomeRun/Lineup Link consumers, startup/tune/stop and return to the original resource baseline.
Captions displayed versus hidden currently does **not** disable extraction. Attribute the caption FFmpeg process directly; a causal on/off A/B
must use owned lab/offline replay with otherwise equivalent input and media plans, including added pipe-copy cost. Do not kill live caption
children or compare unrelated channels as if they were controlled inputs. Standalone text and bitmap burn-in are separate workloads.
Complete the audit with discovery/supervision/device refresh, guide import/merge/database/publication/maintenance, guide browsing and logging,
both alone and alongside representative streaming. Trigger disruptive work only on an approved/disposable instance.

The existing streaming sweep can supply workload/playback evidence using its `existing` mode; its `--profile` option is a **browser profile**,
not a CPU profile. Follow its [runbook](../../tests/Integration/Lineup.Integration.Streaming.Sweep/README.md), prepare compatibility only when approved,
preserve unrelated viewers, and never run competing sweeps. Live hardware sweeps and WPR recordings remain explicitly invoked, never run by
solution builds/tests or CI; only deterministic collector regressions participate in the ordinary gate.

In WPA, open `system.etl`, review trace/lost-event statistics, set symbol paths to matching Release assemblies/PDBs and available native symbols,
then select the exact UTC observation interval from the journal. Use **System Activity / Processes** to resolve initial roots, helpers,
descendants, restarts and explicit external roots by lifetime. Filter **CPU Usage (Sampled)** to those exact process instances, group by
process/thread/stack and inspect weight, inclusive/exclusive work and hottest functions/modules. Use **CPU Usage (Precise)** for scheduling/waits,
not as a substitute for sampled function attribution. Retain machine-wide context for unrelated interference, driver CPU and saturation.
Inspect GPU engine activity (encode/decode/copy separately), I/O and .NET tables where captured/supported. Missing FFmpeg/driver symbols can
limit conclusions to modules; retain unresolved frames rather than labeling them as a specific decoder/filter.

Export the selected CPU-by-process/stack and supporting GPU/I/O tables to CSV with their grouping/filter configuration. Produce separate
startup, steady-state and cleanup results. For a window of `T` seconds, CPU-seconds are the sampled CPU weight converted from milliseconds,
average core-equivalents are `CPU-seconds / T`, and machine-normalized CPU is `100 * CPU-seconds / (T * logical processors)`.
Keep peak-window definitions consistent and do not conflate single-core percentages with Task Manager's whole-machine percentages.
Rank measured costs and proposed optimizations with confidence, expected removable work and playback/compatibility risk; distinguish measurements
from hypotheses. Re-run matched captures and playback/AV/caption/sharing/cleanup checks before claiming savings.

The capture helper's deterministic identity/ancestry regressions need no elevation, tuner, WPR recording or external test packages:

```powershell
.\tests\Tools\PerformanceProfile.Tests.ps1
```

## Related documentation

[Project overview](../../README.md) | [Contributor standards](../../CONTRIBUTING.md)
