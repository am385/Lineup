# Lineup.App.Web

The Blazor management host: configuration, dashboard resources, status API, logging and operational health. Source-development commands run from the repository root.

- [Running the Web App](#running-the-web-app)
- [Interactive Guide](#interactive-guide)
- [Streaming flow](#streaming-flow)
- [Configuration](#configuration)
- [Dashboard resources](#dashboard-resources)
- [Versioned status API](#versioned-status-api)
- [Saving and applying settings](#saving-and-applying-settings)
- [Operational health and stream recovery](#operational-health-and-stream-recovery)
- [Logging](#logging)
- [Localization conventions](#localization-conventions)

## Running the Web App

```bash
cd src/Apps/Lineup.App.Web
dotnet run
```

The app starts on `http://localhost:8080` by default. Persistent application data defaults to `/appdata`, XMLTV output defaults to `/guide/epg.xml`, and transient stream artifacts default to `/transient`. The development launch profiles keep local runtime state in the ignored repository-root `.lineup` directory, separated into `appdata`, `guide`, and `transient` subdirectories. Configure the HDHomeRun device address through the Settings UI.

## Interactive Guide

The interactive Guide uses fixed 30-minute elapsed-time slots in the configured timezone. Spring daylight-saving gaps are skipped;
repeated fall times include their UTC offsets, including program tooltips and details. Program widths and durations use elapsed time.
Navigation stays within the selected local date, including 23- and 25-hour days. Rapid navigation and resizing cancel obsolete reads;
only the latest request may display results or end its loading indicator. Failed reads show an explicit retry action instead of old-window rows.

## Streaming flow

The streaming graph preserves selectable Bootstrap node cards in five lane boxes: **Physical Sources -> Source Processing Multiplexer -> Virtual Outputs -> Output Processing Multiplexer -> Consumers**.
The processing multiplexer boxes group reusable producers by responsibility; they are not additional processing steps or separate backend ownership systems. There is no separate inventory reference for a producer already represented by its processing node.
Physical devices and virtual outputs retain configured processor cards while idle. Live source processing displays actual shared normalization producers, or a pass-through producer for a known acquisition with no normalization stage. Output processing displays actual virtual-device producers, retaining reported multi-stage chains.
Two consumers sharing channel 2.1's output fan out from the same processor; an independent channel 2.3 output has its own processor even when encoder settings match. Each producer retains its own encoder, resources, channels, and exact path membership. Missing producer identities remain explicitly unreported and session-scoped instead of implying reuse; retained producers without virtual-device attribution remain unassigned.
Encoder badges appear once on the responsible producer. **Copy / processing** indicates copied video with reported process resources, which can still involve remuxing, audio conversion, or packaging; **Pass-through** does not invent a separate transcoder process.
Dashed connections represent configured idle stages; solid connections show actual consumer routes or retained warm producers. Consumer cards and their connections expand and disappear with live sessions while configured devices remain visible; idle devices retain their configured processor placeholders.
Select any card by mouse or keyboard to inspect its current details and resources and highlight every reported stream route containing it, including all related producers and consumers. Shared producers highlight all their consumers without crossing unrelated channel routes through shared device cards. Select the same card again to clear selection and show the whole graph; there is no path dropdown. The original horizontal scrolling keeps cards readable, and refreshes preserve selection and scroll position.
Cards within each lane are ordered by their connections to reduce arrow crossings and keep consumers of the same producer together. Processing chains remain grouped in upstream-to-downstream order, and telemetry-only refreshes do not reshuffle cards. Some crossings can remain when shared devices connect multiple branches.
The graph lives directly in the Active Streams card, replacing the separate Active Paths section. Selected-node details use one header and consolidated metadata rather than repeating node, session, and stage identities. Single-session details combine endpoint, format, channel, sharing, session ID, active duration, stage metadata, resources, and input/output media tracks and bitrates. Shared nodes show common metadata and resources once while retaining each session's distinct diagnostics. Exact route membership keeps other channels out of shared-producer diagnostics. Multi-producer processing lanes explicitly label session-wide stage diagnostics rather than implying intermediate media was measured at each producer; resource costs remain scoped to the selected node. Every session shown in the details panel has its own Stop button, including sessions listed for intermediate or shared nodes. Each button stops only its associated session, not the shared producer or other viewers.
With multiple sessions displayed, **Stop All** beside the selected node's state badge stops that displayed set using the same session-specific requests, skipping sessions already stopping. It does not target unrelated sessions or stop shared producers directly. Newly arriving sessions are not included in an in-flight batch, and queued session controls are disabled until their stop requests finish.
Cytoscape.js maintains graph identity and path membership; SVG connections follow the actual HTML card bounds. Its bundle and license notice are hash-pinned browser assets, served locally with no runtime CDN dependency.
This proof of concept includes retained management-host CMAF outputs and the shared runtime graph's warm producers. Dedicated workers' active outputs use their existing reports, but their retained worker-local output inventory is not yet reported to the dashboard. Unknown source attribution is shown explicitly rather than guessed.

## Configuration

Runtime application settings are configurable through the web UI's settings page and persisted to `settings.json`.
Startup-only host budgets are documented in [Core processing and admission](../../Core/Lineup.Core/README.md#processing-and-admission).

| Setting | Description |
|---|---|
| Physical Devices | One or more named HDHomeRun or Lineup Link inputs, each with ordering and sparse transcoding overrides |
| Physical Transcoding Defaults | Global input-stage settings inherited by every physical device unless overridden |
| Guide Sources | Named SiliconDust or optional Lineup Link sources subscribed to all or selected compatible physical devices, plus refresh scheduling and guide-history retention |
| Guide Outputs | Built-in global XMLTV plus additional scoped XMLTV or JSON publications selected from enabled guide sources and virtual devices |
| Virtual Devices | Built-in Web Player plus virtual HDHomeRun and Lineup Link outputs, each subscribed to all or selected compatible physical devices |
| Virtual Transcoding Defaults | Global output-stage settings inherited by every virtual device unless overridden |
| XMLTV Output Path | Under Guide Outputs, controls where the built-in global XMLTV document is published. The default is `/guide/epg.xml`. Enter a directory directly or use the server directory browser. `Lineup:GuideOutputDir` optionally changes the initial default directory and remains available as the reset value; the saved output path is authoritative afterward. |
| Automatic Guide Refresh | Independent source schedules: SiliconDust uses randomized 20-28-hour intervals; Lineup Link uses `AutoFetchInterval` (24 hours by default). Failures retry after one hour |
| Virtual Tuner Audio | Preserve non-AC-4 audio codecs, or transcode those tracks to AC-3/E-AC-3 |
| AC-4 Audio | Preserve AC-4, or transcode it to AC-3 (default) or E-AC-3 |
| Virtual Tuner Video | Preserve the video codec (default), or transcode HEVC to H.264 for clients that do not support HEVC Live TV |
| General Interlaced Video | Preserve the source (default), or deinterlace each confirmed interlaced tuner source once to progressive H.264 at its source frame or field rate |
| Web Player Interlaced Video | Preserve the effective source (default), or apply per-session fallback deinterlacing in Watch |
| AC-4 Timestamp Compensation (Experimental) | Off by default; optional timestamp-aware filling/trimming during AC-4 encoding, with independent physical/virtual defaults and per-device overrides. Copy and unrelated codecs are unaffected; AC-4 encoding uses 48 kHz regardless. Save and start new playback for A/B testing |
| DRM-Protected Content | Return an explicit error (default), or stream a synthetic Content Protected slate |
| Disabled Channels | Return an explicit error (default), or stream a synthetic Disabled Channel slate without opening a physical tuner |
| Maximum Concurrent Streams | Limits logical consumers, not shared producers; multiple consumers sharing one pipeline each count toward the limit |
| Keep Warm Period | Keeps an unused shared output reusable for 0-300 seconds; the default of 0 releases it immediately, while nonzero values retain tuner capacity and transforms until expiry |
| Active Stream Refresh | Dashboard graph/details refresh interval; defaults to 1 second and 0 disables auto-refresh. Saved intervals remain unchanged. Automatic refresh pauses while the tab is hidden or Active Streams is collapsed |
| Collect Resources Continuously | Disabled by default; collect only while a Dashboard is visible. Enable to keep collecting and caching in-memory history without a visible Dashboard |
| Resource History In Minutes | In-memory Dashboard resource retention, 1-60 minutes; defaults to 5 and resets when Lineup restarts |
| Virtual HDHomeRun Devices | Friendly name, optional tuner cap, physical subscriptions, and a reachable advertised URL when served through the management host |
| Output Hosting | Per-output choice between the management UI host and an isolated dedicated listener; dedicated mode derives its client-facing URL from transport, bind address, and port, with advanced overrides for proxies, NAT, Docker, TLS, network, and worker controls |
| Network Discovery | Opt-in SiliconDust UDP (65001) and SSDP (1900) advertisement of enabled virtual HDHomeRun devices |
| File Logging | Opt-in rolling files with configurable minimum level, retention period, and per-file size limit; changes apply immediately when saved |
| Status API Privacy | Independently redact active-stream client addresses, device addresses, and URLs from `/api/v1/status` |

## Dashboard resources

The Dashboard's **Resources** panel shows Lineup's management host, dedicated workers, and owned media/lifetime-helper processes.
CPU uses **100% per logical core** (so 175% is 1.75 occupied cores), not a percentage of server or container capacity.
Memory is summed resident memory in MiB; shared pages can appear in more than one process, so this is not unique physical RAM.
Resource collection is independent of Active Streams auto-refresh, including when that refresh is disabled.

Visible Dashboard tabs share one sampler and bounded aggregate history. In visibility-only mode, hiding or leaving every Dashboard
stops collection after any outstanding visibility interest expires; disconnect interest expires within eight seconds.
Continuous mode retains collection without visible tabs, but does not refresh hidden browser charts.
History stays in memory, contains at most 1,800 aggregate samples, and clears on restart. Retention changes take effect without restarting streams.
Long charts share cached geometry and retain each display bucket's minimum and maximum, rather than smoothing away peaks or joining gaps.
The CPU, RAM and per-GPU cards include intermediate value labels, horizontal grid lines and a relative-time axis.
Time labels use compact seconds and minutes (for example, `15s`, `1m` and `1m 30s`), measured back from the latest sample at `0s`.
The linear time axis always spans the configured history duration, even while history is filling; unobserved history stays empty on the left.
Their legends use equal-width responsive columns so changing values do not shift the labels horizontally.
Primary Dashboard cards keep their icon, title and summary visible when collapsed. The Resources summary shows current CPU and RAM totals plus each GPU's whole-device activity;
unavailable or paused readings show N/A, never retained percentages. GPU percentages remain separate rather than being added across devices.
The CPU, RAM and GPU subcards each have an independent chevron control to hide their content while leaving their title and current summary visible.
CPU and RAM subheaders show Lineup totals; each GPU subheader shows that device's whole-device activity. Paused or unavailable readings show N/A.
The Resources section and its subcards save their expansion preferences in the current browser and restore them after navigation or a page reload, independently of other dashboard sections.
Ordinary navigation finishes already-queued preference writes, including choices made while saved state is still loading.
GPU preferences follow each device's internal identity, not its name or position. Missing preferences default to expanded; browser-storage failures are logged and do not disable the controls.
Each Physical Devices subcard also has an independent chevron that hides its body while keeping the device name and kind visible.
These preferences default to expanded and are saved in the current browser by device identity, surviving navigation, reloads, renaming, and reordering.
Nested upstream-device entries within Lineup Link cards remain unchanged.
Channels, Physical Devices and Active Streams refresh buttons sit at the bottom right of their card bodies and hide with the content when collapsed.

GPU cards show **whole-device** activity visible to the management host/container, including other applications and containers;
they are not Lineup-only, per-stream or per-stage attribution, and do not include GPUs on remote worker hosts.
The shared two-second sampler reads native counters, never launches `nvidia-smi`, `intel_gpu_top`, PowerShell or other polling helpers,
and does no driver work during dashboard rendering. Hiding every Dashboard also releases native monitoring resources unless continuous collection is enabled.
GPU readings share the same bounded history/window as CPU and RAM, with a fixed 0-100% scale.
Each card distinguishes GPU activity and the video metrics supplied by its provider. These measurements overlap and must not be added.
GPU activity is driver-specific: Windows and Intel show the busiest physical engine; NVIDIA and AMD use their driver's activity counter,
which need not include dedicated video work. Intel i915 cards show **Video encode/decode — shared** instead of separate encode/decode readings.
This is the busiest shared codec (`vcs`) engine, not a sum; video enhancement (`vecs`) is excluded from that codec value.
Expanded Intel cards list individual driver-named engine percentages, including enhancement and render engines, without inferring which codec work is encoding or decoding.
Other providers retain separate encode/decode readings only when those measurements have been observed; unsupported series are omitted.
Available metrics vary by GPU, driver and platform, even between adapters from the same vendor. Windows drivers may expose a generic codec engine under **Video / media**
instead of separate encode/decode counters. A missing category does not imply missing hardware support; a valid 0% reading remains visible.
Unavailable current readings show N/A without a percentage suffix, and retained history never supplies stale current engine percentages.

| Management host | GPU telemetry |
|---|---|
| Windows | Vendor-neutral GPU Engine performance counters, grouped by adapter and physical engine; process instances are summed only within the same engine |
| Linux NVIDIA | Installed `libnvidia-ml.so.1` (NVML); activity plus encode/decode when supported; containers need the NVIDIA driver utility capability |
| Linux AMD | DRM `device/gpu_busy_percent` when exposed; video-engine metrics remain unavailable |
| Linux Intel i915 | Driver-published engine busy PMU events via read-only performance-counter descriptors; shared encode/decode plus individual engines in expanded details |
| Other drivers/platforms, including Linux Xe without supported counters | Explicit N/A, not fabricated zero |

On Windows, card titles use DXGI adapter names matched to GPU-counter LUIDs, not adapter enumeration order.
The LUID remains the internal sample/history identity. Name discovery is cached; a failed lookup logs a warning and retains the counter-identity label without losing utilization readings.
Software adapters such as Microsoft Basic Render Driver retain their actual names; they are not additional physical GPUs.
Linux NVIDIA already supplies model names through NVML. Linux AMD and Intel currently use vendor/card or PMU labels rather than resolved model names.
An inaccessible NVIDIA adapter does not hide readable NVIDIA adapters. Previously identified failed adapters retain their names with unavailable metrics;
failures before an adapter can be identified still report a partial monitoring error without inventing a device identity.
Unraid Docker uses the Linux readers and sees only devices, libraries and counters exposed to that container; native Linux uses the same readers with host access.
Native macOS has no GPU telemetry provider. Docker Desktop on macOS runs in a Linux VM and does not expose the Mac GPU to these readers.

Streaming access alone does not guarantee monitoring access. Intel PMU access is often blocked by host/container performance-counter policy;
Lineup does not request elevation, privileged mode, change kernel settings or widen device permissions. Unsupported metrics, inaccessible counters,
warm-up and read failures show N/A or gaps with an explanation; actionable read failures are logged without repeating the same warning every tick.
GPU devices are discovered on first collection; changing exposed driver libraries/devices can require restarting the management host.

Select an Active Streams processing stage or topology node to inspect its current CPU, memory, process identity, role, and observation time.
Resource tables use fixed column widths in Streaming Flow's selected-node details, with horizontal scrolling on narrow screens.
Source and output processing details also show the cached host GPU activity, encode, decode, and video/media readings in a separate GPU table.
These are whole-device counters, including other applications, not GPU percentages attributed to the selected process or stream; remote worker GPUs are not included.
Shared codec-engine readings stay distinct from separate encode/decode counters. Unavailable metrics show N/A, and paused collection never displays retained percentages as current usage.
Shared source/output producers show their full process cost, not a fictional per-viewer allocation. Caption extraction appears separately when enabled.
Work inside a management/worker host is labeled shared-host work; decoding, filtering, encoding, audio and muxing inside one FFmpeg are not individually attributed.
Tuner hardware and viewer browsers are not measured. Warm-up, inaccessible/exited processes, missing or stale worker reports,
and intervals containing short-lived processes produce explicit unavailable/partial values or chart gaps, never fabricated zero CPU.
Dedicated workers begin reporting after their existing lease exchange receives monitoring demand; monitoring failure does not alter playback readiness or lease health.

## Versioned status API

`GET /api/v1/status` returns schema `3.0`, a stable JSON snapshot for remote diagnostics and external dashboards. It includes the application version and runtime
identity; every non-secret Core, Web, topology, adapter, transcoding, subscription, hosting, security, worker, logging, discovery, and privacy setting; every active
physical channel with global overrides, provider metadata, guide binding, candidates, and each virtual device's resolved channel projection and conflicts; guide
coverage, per-source import metadata, automatic-fetch state, and every configured publication output; physical-device and receiver runtime state; virtual-output
profiles and desired-versus-applied worker state; active streams with source/output tracks; shared graph nodes and capacity; and the complete configured and active
end-to-end streaming-flow projection used by the Dashboard.
The endpoint is passive: polling it does not fetch guide data, discover devices, refresh tuners, retune channels, or interrupt streams. Responses use
`Cache-Control: no-store`; unavailable sections report their own status and error without hiding the remaining state.

Operational status is **eventually consistent**, not one atomic system-wide snapshot. `observation` identifies the captured Core, Web and streaming configuration
revisions, reports configuration drift during the request, and bounds each section's read with UTC start/completion times. These intervals describe observation,
not freshness of cached source data. Physical devices separately expose the last attempt and last successful status/information observations.
`guide.generation` identifies the coherent statistics read, its contributing-import revision and its streaming configuration revision.
The independently read source-import inventory has its own observation time and revision; its scope includes all stored imports, so its revision is not comparable
to the contributing-import revision. Worker, stream, graph and flow sections may change between observations; collecting status never takes a global streaming lock.

`operations` is a reason-coded summary of those existing observations plus cached topology/source-refresh outcomes; building the summary performs no extra storage
queries, program scans, disk probes or provider calls. It reports `healthy`, `pending`, `degraded` or `unknown`, with configured resource IDs/names and UTC observation
times. `topology` includes desired, attempted and last fully applied revisions. Reasons identify failed/quarantined or pending workers, excluded/failed/missing/expired
guide sources, pending/unavailable publications and reached producer/processing/plugin-connection limits. Guide expiry means committed coverage has ended,
not an invented age threshold. Pending publication means a durable retry is required, not proof the old file is current; publication time is last successful
file-write time, not a per-file guide generation. Named refresh attempts are process-local completed observations tied to the source definition revision;
after restart or definition changes, unknown attempt history is not reported as success. Observation times are not invented worker/topology transition times.
No exception text, adapter payloads, endpoints or archive contents are added to this summary. Recent unexpected stream terminal reasons remain in
`streamingGraph.recentTerminations`. None of these operational conditions change `/health/live` or `/health/ready`.

Guide publications and statistics resolve topology, bindings, authoritative programs and contributing import metadata in one deferred SQLite read transaction.
Statistics use per-authoritative-channel SQL count/min/max aggregates, preserving equivalence, manual-unavailable and empty-channel coverage semantics without
loading program JSON. Guide/watch channel lists and playback protection checks use channel-only projections; interactive program reads remain time-windowed.

The endpoint is unauthenticated and follows Lineup's trusted-network model. Open **Settings → API** to redact active-stream client addresses, device addresses,
and URLs independently. Client addresses are redacted by default; device addresses and URLs are visible by default. Secret adapter values are represented only
by their field name and whether a value is configured. Plaintext credentials, token hashes, certificate passwords, enrollment secrets, and server filesystem paths
are never exposed. Use HTTPS and authentication at a reverse proxy before allowing access from an untrusted network.

Client-address redaction also covers consumer labels and endpoint details in streaming-flow nodes and path stages. Redacted labels use `Consumer`;
session and client IDs, channel information, media diagnostics, and non-consumer labels remain available. These API privacy flags do not alter the Dashboard's flow snapshot.

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

## Saving and applying settings

The Settings page keeps one **Save** operation, but Core (`settings.json`) and Web (`web-settings.json`) settings commit independently.
If Core saves and Web fails, the page reports that partial outcome without rolling back committed changes. Retrying retains the committed Core baseline;
newer edits by another operator produce a conflict instead of being overwritten. Use **Undo** to reload saved settings before resolving a conflict.
Scheduling timestamps do not invalidate an open draft, and header theme changes remain synchronized without discarding other unsaved edits.
TUI topology replacement, adapter editing, and access-grant changes also reject stale configuration.
Draft revisions compare the running host's live snapshots; they do not add cross-process file locking.

The Settings page stays quiet during normal configuration application. Failed or partial application displays a **Configuration status** warning
with the affected devices or outputs and **Retry**; revision hashes are not displayed in the normal settings view.
This operation covers physical lineup refresh and cached guide publication; dedicated output-worker health remains a separate runtime concern.
Timestamp, schedule, retention, and other non-topology updates do not refresh lineups or republish guides. **Retry** reapplies the desired topology
without rewriting settings. Outcomes are retained for the process lifetime; startup reapplies the persisted desired topology.
Failed publication preserves the previous output file, and initial setup reports success only after its required connectivity and application checks finish.

The Settings **Reset** tab can stage default settings for review or perform a Factory Reset. Factory Reset removes all files and directories beneath Lineup's application-data and transient-data roots, along with the configured XMLTV output, during a graceful restart. The browser displays a blocking restart screen and returns to first-run setup after detecting the new Lineup instance. XMLTV files configured outside Lineup-owned storage are preserved. A service supervisor such as Docker's `restart: unless-stopped` must restart the process after reset.

Lineup 3.0 uses application-settings schema `1`, streaming-topology schema `1`, database schema `1`, and Web-settings schema `1` as an intentional factory-reset cutover. Persisted state must match these versions exactly; an older, newer, invalid, or unversioned artifact enters the explicit factory-reset-required startup state before current state is created. No settings, topology, database, browser-profile, XMLTV, or Lineup Link token migration runs. Worker packages require IPC protocol `1`; update managed and external worker packages together with the trusted host.

Web startup validates the explicitly declared schema in `web-settings.json` before applying its preferences, including logging settings.
If the primary Web document cannot be loaded, a valid `web-settings.json.bak` can recover it; the first successful save preserves that backup rather than replacing it with the rejected primary.
If neither existing Web document is valid, normal workflows and Web-settings writes remain blocked until explicit recovery; the existing files are not silently replaced with defaults.

The Web `SettingsSaveCommand` accepts raw settings drafts and expected Core/Web edit revisions independently of the Blazor editor.
It owns topology/field validation, conflict checks inside serialized updates, independent Core-then-Web commits, and retryable logging application outcomes.
Results explicitly retain a committed Core revision when the Web write fails; this is not a two-file transaction. Setup connectivity/topology verification,
one-time secret display, notifications, browser theme application and navigation remain presentation-specific steps after persistence.
Independently synchronized header-theme changes are preserved during a queued save. This boundary does not introduce a command bus or a new remote mutation endpoint.

For a new or empty SQLite database, Lineup commits the channel and guide tables together with the required schema-version metadata in one transaction. Failed, canceled, or interrupted first-time initialization rolls back for a later startup to retry. Existing populated databases with missing or incompatible metadata remain rejected; initialization does not repair, migrate, or delete them.

Lineup-owned persistent data defaults to `/appdata`. The optional `Lineup:AppDataPath` setting overrides that root for custom deployments. This data includes settings and backups, the authoritative SQLite channel and guide database, rolling logs, ASP.NET Core Data Protection keys, and the restart-safe Factory Reset marker.

Transient CMAF fragments and subtitle sidecars default to `/transient`. When `Lineup:AppDataPath` is configured but `Lineup:TransientPath` is not, Lineup uses a `lineup` directory beneath the operating system's temporary path to keep disposable stream artifacts outside persistent application data. The optional `Lineup:TransientPath` setting (environment variable `Lineup__TransientPath`) overrides either default. The Compose files mount a `transient` named volume at `/transient`, matching the `appdata` and `guide` volume conventions. Custom deployments can replace that named-volume mount with a memory-backed bind mount or Compose `tmpfs`, or with disk-backed storage when transient data must not consume system memory.

Lineup routes application-owned files through explicit storage boundaries. `AppDataStore` owns persistent settings, database, reset, and log artifacts; `TransientDataStore` owns disposable CMAF and subtitle workspaces; and `XmltvPublicationStore` owns the configured public XMLTV destination, including destinations outside app-data. Browser preferences remain client-local through `BrowserDataStore`. Framework and process infrastructure such as SQLite, Serilog sinks, Data Protection, and FFmpeg continue to receive managed paths and perform their own I/O.

ASP.NET Core Data Protection keys are stored in the `data-protection-keys` directory beneath the app-data root. Persisting this directory through the Docker `/appdata` volume keeps antiforgery tokens and other protected browser state readable after a container recreation. Factory Reset deletes the key ring so previously protected state is intentionally invalidated.

## Operational health and stream recovery

`GET /health/live` returns HTTP 200 when the management HTTP pipeline is alive, including factory-reset recovery mode.
It does not query SQLite, build a status document, or contact tuner hardware. Both Compose deployments use this endpoint for their container health check;
an unhealthy Docker health status alone does not restart a container.

`GET /health/ready` returns HTTP 200 when management is usable, including first-run setup and external tuner outages.
It returns HTTP 503 while starting, stopping, or awaiting a required factory reset. Its small, non-cacheable response reports cached device-failure count
and age of the last successful guide fetch when known. Unknown guide age is null, not zero. Cached failures are reported as `degraded` without making the process
unready: do not restart Lineup because an external device is offline. Use readiness for traffic routing and liveness for process supervision.

Established streams are not spliced onto arbitrary alternate sources. Unexpected source EOF or failure terminates the current producer and its subscribers;
MPEG-TS clients receive the remaining media followed by EOF/disconnection, and browser playback can become unavailable or report a playback error.
Live Watch automatically reconnects after explicitly classified source EOF/failure or recoverable dedicated-worker loss, but only after playback has initialized.
It creates at most three fresh, independently owned sessions, re-resolving current routes after delays of 1, 2, and 4 seconds within one 60-second recovery deadline.
Brief recoveries do not replenish the budget: playback must remain healthy for 60 uninterrupted seconds, excluding paused/waiting/stalled playback.
The page displays **Reconnecting** and retains Stop, stream/profile overrides, audio/subtitle selections, volume and mute.
Stop, operator stop, navigation, disposal and a newer tune cancel recovery; late sessions are released rather than accepted.
Unknown sessions, arbitrary HTTP/network/player errors, protected/disabled/invalid requests, permanent configuration failures, and quarantined workers are not automatically retried.
Recovery can follow normal worker supervision but never explicitly restarts a failed worker or bypasses its restart policy. A failed retry continues only for explicitly transient source failures.
Passive per-viewer status does not renew inactivity leases; source-loss outcomes retain at most 2,048 aliases for two minutes and are forgotten on shutdown.
Watch Test and external MPEG-TS clients retain manual reconnect behavior. Seamless codec/timestamp/track continuity is not promised.
Stopping one viewer still leaves a shared producer running for other viewers.
The status API's `streamingGraph.recentTerminations` retains the last 64 unexpected producer completions with `source_eof` or `source_failure`,
producer identity, observation time and originating correlation ID. Intentional viewer stops and shutdown cancellation are not source-loss events.

Lineup exposes standard .NET meters `Lineup`, `Lineup.Streaming`, and `Lineup.Host`, plus activity source `Lineup`.
Measurements include operation duration/count by fixed stage and outcome, candidate acquisition/fallback, pending admission and rejection,
active/warm graph and CMAF producers, unexpected producer completion, worker restart attempts, retained buffering/eviction and cached guide age.
Graph stages and CMAF outputs are distinct resources, not interchangeable viewer counts. Unknown guide age emits no measurement.
Meter labels never contain viewer/channel IDs, addresses, tokens, URLs or raw failure text.
Use a .NET diagnostic collector or an explicitly configured metrics/tracing integration to subscribe; the existing OTLP **logging** configuration
does not automatically enable metrics or trace export.

API responses include `X-Correlation-ID`. Request log scopes, acquisition/media-readiness/producer activities and local broker request envelopes
preserve W3C trace identity; IPC carries only `traceparent`, not baggage or credentials. Active-stream status includes its correlation ID alongside existing
broker request, worker media-session and producer identities. Worker activation activities include the generation as a trace attribute, never as a metric label.
A reused producer retains its original trace; later viewers have independent request traces and are joined through producer/output identities, not falsely treated
as new producer starts.

## Logging

Console logging is always enabled and remains available through `docker logs lineup` or the host service manager. After initial setup, the **Logs** page shows a bounded, redacted view of recent structured events from the current process. It supports severity, category, and text filters plus pause and manual refresh controls. Because Lineup currently assumes a trusted local network, the Logs page is unauthenticated and may expose operational details such as device addresses; do not expose it to untrusted clients.

Guide refresh failures retain the underlying exception in source-correlated warning logs while returning a sanitized failure result to callers. Topology application also logs each failed guide-output publication by output identity; physical streaming remains available when guide publication fails.

Lineup uses the standard .NET `Logging:LogLevel` configuration for its startup default and category-prefix filters. **Settings → Logging → Override Logging Defaults** can replace that complete filter set at runtime. The editor starts with categories discovered from the effective startup configuration, including environment overrides, and supports additional custom prefixes. Exact categories and dotted descendants match, with the longest prefix taking precedence. Disabling the override immediately restores the startup filter set. The application filter applies to every output before any sink-specific minimum.

Open **Settings → Logging** to enable rolling files. Files are written in the application-owned `logs` directory beneath `Lineup:AppDataPath`. The minimum level, retention period, and per-file size limit are configurable. Enabling, disabling, or changing these options rebuilds only the managed file sink and takes effect immediately when settings are saved; console, in-memory, and external logging continue uninterrupted. **Delete Logs** removes all existing rolling files and safely starts a fresh file when logging is active. Factory Reset deletes this application-owned directory. The Logs page lists and downloads only retained `lineup-*.log` files from that directory.

If retained-log deletion fails, Lineup reports the error and restores the configured file sink; disabled file logging stays disabled. Clipboard denial or browser disconnection when copying an About or guide-output URL produces a localized status error without discarding unsaved Settings edits or displaying copy success.

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

## Localization conventions

- The Web UI's `Dashboard_RemainingDurationFormat` and `Dashboard_ChannelGapDurationFormat` resources are executable .NET `TimeSpan` formats, not ordinary translated text. Preserve `d`, `h`, and `m` numeric tokens and quote literal units, for example `h' hrs 'm' min'`. `UiLocalizationTests` exercises both formats with zero, hourly, and multi-day durations in every supported culture.

## Related documentation

[Project overview](../../../README.md) | [Contributor standards](../../../CONTRIBUTING.md)
