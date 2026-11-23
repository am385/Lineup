# Lineup.Core

Provider-neutral topology, shared stream ownership, admission, guide storage and publication.

- [Processing and admission](#processing-and-admission)
- [Physical and virtual tuner topology](#physical-and-virtual-tuner-topology)
- [Guide binding, import and publication](#guide-binding-import-and-publication)
- [Shared-stream cleanup](#shared-stream-cleanup)

## Processing and admission

The Physical Devices and Virtual Devices tabs provide inherited Video and Audio transcoder profiles. Physical deinterlacing normalizes confirmed interlaced input once before the shared multiplexer, while virtual-device settings create consumer-specific output targets afterward. Source-frame-rate mode converts 29.97-frame interlaced video to 29.97p (or 25 to 25p) with lower CPU use; source-field-rate mode preserves temporal motion as 59.94p (or 50p) at higher CPU cost. Progressive sources retain the copy-first path unless another enabled conversion requires transcoding.

Lineup builds each live stream as a reference-counted graph: exact physical acquisition, optional physical normalization, and virtual output packaging. Consumers share the longest byte-identical prefix, including across different virtual devices, and branch only where their effective output settings differ. Physical failover is resolved to an exact device before its profile is applied, so a fallback source shares with other consumers of that source and uses its own overrides. FFprobe metadata is also shared for the lifetime of the acquisition. The Dashboard's **Active Streams** graph shows actual runtime acquisitions, processing, reusable outputs, and consumer branches. The status API exposes active graph nodes, stable node and parent identities, processing stages, receiver counts, warm state, subscription totals, and physical-capacity use.

Stream admission reserves both a logical viewer slot and a pending-startup slot **before** source acquisition, probing, or
transcoding. Overload is rejected immediately with HTTP 429 (`stream_limit` for Lineup Link); dedicated workers receive the
existing `ResourceLimit` broker rejection. Existing viewers are not displaced. Producer sharing is unchanged: each admitted
viewer still needs its own slot, even when joining an existing producer. Pending startup reservations count toward
**Maximum Concurrent Streams**, and that setting's zero/unlimited behavior and physical tuner limits are unchanged.
Startup slots are released on readiness, failure, cancellation, or cleanup; dedicated Web Player slots remain held through
worker media planning and readiness, not just the broker's physical-stream response.

The host also enforces these finite, startup-scoped defaults:

| Host budget | Default | Configuration key |
|---|---|---|
| Concurrent pending stream startups | 8 | `StreamingResources:MaximumPendingStarts` |
| Queued payload per graph subscription | 8 MiB | `StreamingResources:SubscriberBufferMib` |
| Distinct queued payload across the graph | 128 MiB | `StreamingResources:GraphBufferMib` |
| Maximum unread media age | 5 seconds | `StreamingResources:MaximumBufferAgeSeconds` |
| Overall physical-source acquisition | 20 seconds | `StreamingResources:AcquisitionTimeoutSeconds` |

These are host configuration, **not** persisted `settings.json` fields or Settings-page controls. Set them in the host's
`appsettings.json` and restart the host, or use equivalent environment variables such as
`StreamingResources__MaximumPendingStarts=8`. For example:

```json
{
  "StreamingResources": {
    "MaximumPendingStarts": 8,
    "SubscriberBufferMib": 8,
    "GraphBufferMib": 128,
    "MaximumBufferAgeSeconds": 5,
    "AcquisitionTimeoutSeconds": 20
  }
}
```

Zero does not disable these safety budgets: invalid configuration fails explicitly. Pending startups support 1-1024,
buffer budgets 1-1,048,576 MiB, and age/acquisition budgets 1-300 seconds. A partially read array remains accounted until its
last byte is consumed or its owner is released; sharing the same array across readers counts it only once globally.
The existing 2048-entry ceiling remains as an additional metadata safeguard. Slow readers exceeding their byte, entry,
or age allowance are disconnected with a logged stream failure; aggregate pressure evicts the largest queued backlogs
instead of blocking unrelated producers. Age expiry runs even when a producer stops sending data. Ordinary EOF preserves
unread data within the budgets; errors, shutdown, and disposal release it immediately.
Temporary startup reservations are actively drained during probing until the replacement output is subscribed, preventing
long probes from turning an intentionally held reservation into a slow reader and unnecessarily reopening the source.

Additional deployment-wide media admission limits are **disabled by default** and use the same restart-scoped host
configuration (including `StreamingResources__MaximumProducers` environment-variable syntax):

| Media admission | Default | Configuration key |
|---|---|---|
| Retained acquisitions, graph transforms, and CMAF outputs | 0 (unlimited) | `StreamingResources:MaximumProducers` |
| Running FFmpeg/FFprobe jobs | 0 (unlimited) | `StreamingResources:MaximumProcessingOperations` |
| Required free space on each actual media workspace volume | 0 (disabled) | `StreamingResources:MinimumMediaFreeSpaceMib` |

Counts are shared by management and dedicated workers, not per listener. Each independently retained graph acquisition,
transform, or CMAF output consumes one producer reservation, including warm and synthetic Watch Test outputs. Every actual
FFmpeg/FFprobe process consumes one processing reservation, including remuxing, probing, caption extraction, fallback
slates, and compatibility tests. A pipeline may therefore need several producer and processing reservations; these limits
are neither viewer limits nor weighted CPU estimates. Equivalent joins reuse existing reservations but retain independent
viewer admission. Count pressure first asks owners to retire zero-viewer warm work and awaits cleanup, then explicitly
rejects if capacity remains unavailable. Processing admission has a ten-second deadline.

Dedicated media reservations use authenticated ownership channels. Remote retirement checks the worker's authoritative
viewer count; revocation or control-channel loss stops dedicated playback rather than continuing with an unaccounted local
fallback. Reservations remain charged through actual producer cleanup or OS process exit. Additional viewers still retain
their independently admitted full-rate broker input and drain it; this change does not introduce a viewer-only IPC lease.

Free space is checked on the actual management/worker output volume before new media allocation, not on a configured
path supplied by a worker to the trusted host. Insufficient space or an unreadable volume rejects allocation explicitly.
Volume selection uses the longest exact mount root or separator-delimited ancestor; similarly prefixed sibling mounts do not supply its free-space reading.
This is an admission watermark, **not** a disk quota, a reservation of future bytes, ongoing enforcement on active writers,
or permission to delete active output files. Concurrent writers can still exhaust disk after admission. The status API's
`mediaResources` section exposes current producer/processing usage, effective limits, the free-space watermark, and
`enforcementScope: "deployment"`. Negative media limits are invalid.

Graph diagnostics expose `BufferedBytes` and `BufferEvictions`. The `Lineup.Streaming` .NET meter additionally publishes
`lineup.streaming.buffered_bytes` and `lineup.streaming.buffer_evictions`; eviction reasons are the bounded values `bytes`,
`entries`, `aggregate`, and `age`. These limits measure **queued payload**, not process RSS, queue metadata, HTTP/socket buffers,
CMAF files, FFmpeg memory, or total deployment memory. Worker process policies are a separate, opt-in boundary.

Changing a physical device's adapter or connection configuration, disabling it, or removing it disconnects all viewers using that device,
including shared transforms and warm streams. Other devices keep running. Previously resolved routes cannot reopen the retired adapter.
The replacement is published after old graph resources finish cleanup; physical capacity stays reserved until its owned source is actually disposed.
New acquisitions, derived transforms and probe metadata use the replacement runtime's identity, while capacity remains aggregated by configured device.
Once viewer retirement begins, cleanup and replacement publication finish even if the configuration caller cancels; cancellation before that point preserves the prior catalog.
Name, ordering and nonconnection profile edits do not replace the physical adapter or disconnect its viewers. Status reports the current configured name.

The built-in XMLTV output accepts relative filenames and absolute filesystem paths; the Web editor presents its containing directory. Lineup's web settings are unauthenticated and are intended for a trusted local network; do not expose the application to untrusted clients when arbitrary output paths are enabled.

Published virtual-channel protection considers all enabled, subscribed equivalent sources: a channel is not marked protected merely because its preferred source is protected when a clear or unknown equivalent remains. Other published metadata retains preferred-source precedence. Management and dedicated Lineup Link outputs use this same projection; protected-only outputs retain the configured error/slate policy.

Physical source selection has one configurable overall acquisition allowance (20 seconds by default), including remote stream
headers and error-body parsing. Each candidate receives a share of the remaining allowance, capped at ten seconds, so stalled
preferred sources leave time for healthy fallbacks. Exhausting the overall deadline prevents starting later sources and reports
`source_acquisition_timeout`. A candidate timeout or invalid stream response releases that candidate's subscription; other
subscribers to the same pending acquisition are not canceled. Source adapters must honor cancellation; cleanup retains
physical-capacity ownership until the source actually stops rather than abandoning a noncooperative adapter. Once acquired,
a live stream has no acquisition time limit. Explicit caller cancellation stops selection instead of trying another source.
The separate CMAF presentation-startup/readiness timeout remains in force.

## Physical and virtual tuner topology

Open **Settings → Physical Devices**, **Guide Sources**, and **Virtual Devices** to configure the input, guide, and output topology. A virtual HDHomeRun device
subscribes to every enabled physical device by default, or can select a subset. Its advertised tuner count is the sum of subscribed finite physical
capacities, subject to its optional cap.

Dedicated HDHomeRun listeners retrieve the current manifest for each `/discover.json` request, so changes to subscribed physical capacity are reflected without restarting the output worker. Clearing the optional tuner-count cap restores aggregate advertised capacity, bounded by the protocol maximum of 255. Clearing an explicit Device ID restores the stable checksum-valid identity derived from that virtual device.

The shared UDP/SSDP discovery supervisor also checks current physical-capacity snapshots every five seconds, including after initial activation and later capacity changes without a settings edit. It replaces the discovery worker only when the effective advertisements or worker policy change; unchanged or unsubscribed capacity does not cause a restart.

Lineup retries incomplete physical-lineup initialization on a later tune for a missing source, without requiring a settings save, including when status polling has already recovered device activation. Already-active sources with committed channels remain routable without awaiting a stalled sibling retry, including a retry running for another viewer. Background device refresh continues recovery attempts. Unchanged healthy runtime devices are retained across partial-activation retries; changed, disabled, or removed definitions are still reconciled. Fully successful initialization is reused.

The Dashboard and runtime status API report every enabled physical device, including failed activations. An inactive device has a safe activation error and no receiver-control or restart actions. Its last successful status and capacity are retained when the adapter kind is unchanged; without a prior snapshot, capacity is zero and no observations are fabricated. Successful refresh clears the error, and disabled or removed devices are excluded.

Hosted physical activation and lineup publication are fenced against successfully saved settings. A tune or refresh that captured older
settings cannot replace a newer catalog or retire its active streams; obsolete tune resolutions restart against current settings.
Settings saves wait for an already admitted catalog or lineup publication to finish before superseding its configuration.

HDHomeRun status and control initialization reuse the successful activation discovery result instead of fetching device discovery a second time.
Explicit device-information refreshes and fresh guide authorization requests still query the device; failed activations remain eligible for retry.

Physical-device activation, lineup, status and information requests share a bounded host-wide network allowance. The defaults are four concurrent operations and
a ten-second deadline per admitted operation, configured at startup using `PhysicalDeviceRefresh:MaximumConcurrentOperations` and
`PhysicalDeviceRefresh:OperationTimeoutSeconds` (environment variables use `__` instead of `:`). Valid ranges are 1-1,024 concurrent operations and 1-300 seconds;
invalid startup values are rejected. Waiting for admission does not consume the network deadline.
Adapters must honor cancellation; an uncooperative adapter retains its slot until it exits rather than permitting unbounded abandoned work.
Equivalent in-flight refresh requests coalesce; canceling one caller does not cancel remaining callers. Healthy lineup/status results publish independently,
failed attempts retain prior successful observations, and obsolete configuration/runtime results cannot overwrite current state.
Catalog replacement and retirement remain atomic; initial activation therefore completes before refresh publication starts, and retirement cleanup retains its
existing non-cancelable ownership boundary.

For management-hosted virtual HDHomeRun devices, the first enabled device is available at the root routes:

- `/discover.json`
- `/lineup.json`
- `/lineup.xml`
- `/lineup.m3u`
- `/device.xml`
- `/auto/v{channel}`

Every management-hosted device also has stable scoped routes under `/hdhomerun/{virtualDeviceId}/`. A dedicated virtual HDHomeRun listener exposes only its
device at the listener root. Device IDs are stable checksum-valid HDHomeRun identifiers. The Settings page displays copyable manual setup URLs and the built-in XMLTV guide URL at `/api/xmltv`.

## Guide binding, import and publication

Each named SiliconDust guide source subscribes to every compatible enabled HDHomeRun by default, or to a selected subset. For every request, Lineup retrieves
fresh `DeviceAuth` values in deterministic physical-device order and concatenates them without separators for one combined SiliconDust XMLTV request.
Authorization is never persisted or logged. Imports are isolated and transactional per guide source. A physical source channel binds to at most one
authoritative guide channel from a source currently subscribed to its device. Lineup automatically uses a unique provider-key match, then a unique original
provider-number match; global channel number overrides do not affect guide matching. Distinct XMLTV station identities that share a number remain separate
candidates, so number conflicts require an explicit selection on the Channels page instead of being silently merged. Manual selections override automatic
matching and remain unresolved if the selected guide channel disappears or its source stops subscribing to that device until the selection is changed or cleared. Use
**Refresh Channels** on the Dashboard to update the persisted tuner lineup independently, or enable the guide-fetch option that refreshes channels first.
A guide fetch refreshes only the guide sources enabled in the saved streaming topology; disabled sources are not queried. It applies the saved
channel snapshot without otherwise querying the tuners. Past programs are retained for 24 hours by default, configurable under
**Settings → Guide Sources**. Channels disabled on the Channels page remain visible in Lineup's Channels and Guide
pages, but are excluded from published XMLTV and virtual JSON, XML, and M3U lineups. Their MPEG-TS and Watch URLs return an error by default or a
synthetic slate when configured. Global channel number/name overrides resolve conflicts once at the physical source, so every subscribed virtual output,
guide view, and XMLTV publication uses the same canonical identity. Equivalent source groups provide ordered physical failover.

Set device-wide source priority under **Settings → Physical Devices → Global Settings → Device priority** using **Move up**
and **Move down**, then **Save Settings**. Priority 1 is preferred among enabled, subscribed, compatible sources; disabled
devices retain their positions but are skipped. **Undo** discards unsaved ordering changes. The device tabs follow this same order.
For duplicate global channel numbers, the first eligible source wins and ungrouped duplicates remain conflicts. Explicitly group
equivalent sources to enable startup fallback in this order; reordering does not switch an already-playing stream.
Priority edits and settings reloads preserve initialized source devices when their connection settings are unchanged;
JSON whitespace and property ordering do not reactivate devices or discard their loaded channel maps.

On **Channels**, open **Same-channel sources** to preview saved number conflicts, preferred metadata, authoritative guide selection, and ordered
source candidates for the global Guide/Watch projection and each enabled virtual output. Output previews apply adapter compatibility, subscriptions,
and enabled states; they are not live-health checks. Matching numbers or names never automatically establish equivalent content.
Explicitly confirm **Treat as the same channel** to create a group, or open a group member to add ungrouped sources with the same saved global number.
Numbers must be changed and saved separately; remove a source from its group before renumbering. Existing groups cannot be merged.
Groups are labeled with effective channel identities, not editable GUIDs. Membership changes are atomic, reject stale previews, and preserve
device-wide priority and individual guide bindings. Removing a member requires confirmation and leaves the other members unchanged.
Failover is attempted at stream startup, not by splicing an active stream. All enabled guide outputs are republished through the normal publication path;
publication failure is reported separately from the already committed edit, while the previous successful publication remains available.

Successful guide imports update matching programs and remove missing current or future programs within the source's reported coverage window.
Repeated programme identities (guide channel, start time, end time, and title) are stored once; the last occurrence supplies metadata and the import's programme count reflects unique identities.
Shared channel-number sorting puts parseable dotted numeric identifiers first, case-insensitive text identifiers next, and empty values last, with a lexical tie-break for equivalent numeric forms.
Retained ended programs and future coverage outside that window survive narrower refreshes. Expired history is pruned according to the configured
retention policy, including expired programs sent again by the source. Channel changes, program merging, pruning, and last-import metadata commit
together for that source without changing another source's data.

The Web host also prunes expired ended programs across **all** cached sources at startup and hourly, even with automatic fetching disabled or failing.
This independent maintenance never removes future programs, guide channels, manual bindings, or import metadata, including data belonging to disabled or removed sources.
It republishes enabled guide outputs from cache without contacting providers. The TUI retains import-time pruning and manual refresh; it does not start this Web maintenance worker.

An enabled guide source that cannot activate still contributes its committed cache to virtual guides when its configured subscription is compatible.
The refresh reports the activation failure; disabling or removing the source, or changing its subscription, immediately restricts that cached projection.
Recovering the source updates its committed cache on the next successful refresh.

Publishing cached XMLTV or JSON outputs, including on-demand guide downloads and topology-change publication, does not construct or refresh guide-source runtimes.
It validates the current topology and applies the same source eligibility and output selections used by guide queries. Explicit and scheduled refreshes still activate sources;
refresh-and-publish refreshes once before publishing the resulting committed cache.

Channel identity, enablement, and manual guide-binding changes invalidate and republish every enabled guide output, including custom-only JSON or XMLTV outputs
that have never been published. Invalidation is persisted before committing the channel change. Failed publications preserve the previous file and retry from
committed cache after one hour, across restarts and even when automatic source fetching is disabled. Partial publications also remain pending.
An independent retry worker checks due publications every 30 seconds, so a stalled source download cannot delay cached-output recovery.
Cached publication is not blocked by a source's network refresh, and refresh completion publishes the latest saved topology rather than an obsolete captured configuration.

Guide queries isolate sources with unavailable adapters, invalid physical-device subscriptions, or no compatible enabled devices.
They return the remaining eligible data with structured source diagnostics (`AdapterUnavailable`, `InvalidSubscription`, or `NoCompatibleDevices`),
including when every source is excluded. Ineligible cached imports remain stored but are not projected. Disabled and unselected sources do not degrade an output.
The Guide and Dashboard display excluded sources and actionable warnings; `/api/status` reports the guide as `degraded` with `sourceDiagnostics`
alongside the remaining counts and coverage. Publication results distinguish successfully written partial guides from complete guides, and the TUI warns accordingly.
XMLTV and JSON guide document formats are unchanged. Cancellation, storage failures, and unexpected provider errors remain failures rather than partial-query success.

Automatic guide refresh persists an independent schedule for each source. Successful SiliconDust refreshes retain the required randomized 20-28-hour cadence;
Lineup Link uses the configured positive `AutoFetchInterval` (24 hours by default), which unrelated Settings saves preserve.
Only due sources are fetched, with at most four concurrent source refreshes. Each failed source retries after one hour without shortening successful sources' schedules.
Source schedules are saved before output publication, so a failed publication retries cached data rather than refetching healthy providers.
Failed or partial publications remain observable failures and preserve the last fully successful automatic-refresh timestamp.
Operational source schedules and output retries do not invalidate open Settings drafts or increment persisted schema versions.

Lineup stores the fields it queries as normalized SQLite values and preserves additional root, channel, and program XMLTV metadata alongside them.
Published XMLTV retains provider source provenance, localized and repeated values, official optional fields, and provider extensions while identifying
Lineup as the document generator. The Guide program-details dialog projects recognized official program metadata into typed ratings, credits,
languages, identifiers, status, and advanced technical details. Raw supplemental XML and uninterpreted provider extensions remain internal.
XMLTV publications are indented to preserve program artwork in Jellyfin readers that incorrectly skip the node following language metadata.
Each XMLTV Guide Output has an **Include unavailable program placeholders** switch, disabled by default.
Channels without guide data remain in XMLTV, but no synthetic `Not Available` program is published unless this switch is enabled.
This prevents multi-day placeholders from appearing ahead of real shows in Jellyfin's **On Now** listings, which prioritize program start dates.
After updating Lineup or changing the switch, republish the guide and refresh Jellyfin's guide data to replace previously imported placeholders.

## Shared-stream cleanup

Lineup's Core stream graph atomically limits each physical device to its discovered capacity. Receivers of an identical physical acquisition share one
tuner slot; equivalent physical and virtual transforms are also reference-counted. Different acquisitions consume separate slots. CMAF viewers receive
independent aliases to a shared output, so stopping one browser releases only its reference and final release stops the process and removes artifacts.
The host-wide stream limit also applies to synthetic DRM and disabled-channel slates even though those slates do not consume physical tuner capacity.

A reconnect to a draining shared stream waits for its physical and transform cleanup before creating a replacement; canceling that wait
does not interrupt the owned cleanup. Draining acquisitions retain their tuner slots until source disposal completes, so rapid stop/reconnect
cannot overlap same-channel physical opens or bypass capacity limits.

Graph shutdown normally drains transforms before stopping physical acquisitions. Cancellation bounds that wait but still signals
every acquisition to stop; graph-owned cleanup continues and retains capacity until the owned source is disposed.

## Related documentation

[Project overview](../../../README.md) | [Contributor standards](../../../CONTRIBUTING.md)
