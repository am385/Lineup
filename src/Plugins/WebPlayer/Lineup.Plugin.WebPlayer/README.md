# Lineup.Plugin.WebPlayer

Watch, browser compatibility, shared CMAF presentations, captions and output-copy policy. Portable FFmpeg planning lives in Lineup.Plugin.WebPlayer.FFmpeg.

- [Web Player and Watch](#web-player-and-watch)
- [Watch Test](#watch-test)
- [Avoiding an unnecessary Web Player video encode](#avoiding-an-unnecessary-web-player-video-encode)
- [Optional embedded-caption extraction](#optional-embedded-caption-extraction)

## Web Player and Watch

**Watch** at `/watch` creates one two-second fragmented-MP4 presentation and exposes the same initialization and media
fragments through both a DASH MPD and HLS playlists. With a
completed browser playback profile, Auto protocol selects a measured protocol and Auto video/audio chooses a measured
source-copy path first. Without a current profile, Watch uses its configured DASH-first and user-selected behavior. Explicit
DASH/HLS, Source, and Fallback selections remain overrides. Shaka Player provides the initial cross-browser playback
implementation. At App Default quality, eligible H.264 or HEVC source video is copied when source playback was measured or
Source video is explicitly preferred, without simultaneously encoding H.264. If source video unexpectedly fails in the
browser, Watch clears the contradicted profile and restarts that browser's stream once with H.264. Explicit H.264 Fallback,
scaled quality settings, and bitmap subtitle burn-in produce only H.264. Every source audio track produces one copy-first
or fallback rendition; Lineup does not continuously encode a duplicate fallback beside a supported source. If real
playback contradicts the saved profile or FFmpeg cannot package a source codec, the session performs one bounded restart
using the configured fallback. Fallback Audio choices are ordered from highest
quality/lower browser support to broadest browser support: EAC3 up to 5.1, AC3 up to 5.1, AAC up to 7.1, AAC up to 5.1, and
AAC Stereo. AAC Stereo is the default. FFmpeg encodes channel-based E-AC-3 but cannot create E-AC-3 JOC/Atmos; existing
E-AC-3 Atmos metadata can be retained only when the Source rendition is copied successfully.
Dedicated Web Player hosting also retries a recognized source-audio MP4 muxer failure once with fallback audio, within the original
20-second media-readiness budget. The retry retains the brokered source, clears partial media/caption output, and reports the final audio
plan; unrelated failures and cancellation do not trigger recovery.

The HEVC-to-H.264 topology default applies only when the effective video entering the CMAF output stage is HEVC.
Progressive H.264, including video already converted by the physical stage, remains copy-first unless another output policy
requires encoding. Explicit video overrides retain precedence in both management and dedicated hosting.

HTTPS Watch selects the first dedicated binding with an HTTPS public URL, even when an HTTP binding appears first.
This uses the public URL rather than internal listener transport, supporting HTTPS reverse-proxy termination.
If no HTTPS public URL is configured, startup reports a configuration error without issuing a media request; HTTP loopback is
not an exception. HTTP Watch retains the first valid HTTP(S) public binding in configuration order.

Startup observes upstream input failures as well as FFmpeg readiness. Source and processor failures return sanitized
HTTP 502 responses (`media_source_failed` or `media_process_failed`) rather than waiting for a generic startup timeout.
Retained logs correlate the media session, broker request, stream, startup stage, exit codes, and bounded stderr tails
(at most 50 non-empty lines and 16,384 characters per tail); these details are not included in public error responses.
Audio probing refreshes provisional video-only observations when audio is advertised or its availability is unknown.
Stage inventories retain `AudioDiscovery`: `Detected`, `DeclaredAbsent`, `ExpectedMissing`, or `Unknown`.
Advertised-but-undiscovered audio, or audio lost between raw and effective metadata, fails CMAF planning explicitly with
`media_audio_missing`; a declared video-only source remains supported. Unknown absence remains provisional, not proof of
a video-only broadcast. Startup waits for initialization resources and two fragments for each planned representation;
this verifies resource readiness, not audible sound, browser decoding, or lip-sync.

Media responses acquire their backing file before returning it, so removal cannot invalidate an already-open response.
Incomplete DASH publication snapshots return HTTP 425 `media_manifest_not_ready` instead of malformed successful manifests.
Dedicated-session inactivity uses a monotonic clock and a minimum 30-second lifetime, covering the ten advertised
two-second segments plus five extra retained segments. This does not retain closed sessions indefinitely or guarantee
new requests after an explicit stop or terminal producer cleanup.

In both management-hosted and dedicated modes, Watch viewers with identical effective playback requests (apart from client ID), profiles, and exact physical
source generations share one CMAF producer within their output host. Each viewer retains its own manifest/subtitle URLs, client diagnostics,
inactivity lifetime, and admission-limit slot. Stopping or expiring one viewer leaves the others attached; the final viewer's
release follows the configured keep-warm policy. Different media plans, bitmap-subtitle selections, exact physical tune/runtime generations, and output hosts remain isolated.
Warm retention is best-effort: tuner or media-resource pressure can retire a producer only after its final viewer leaves.
Retirement waits for owned process, input, and capacity cleanup before admitting replacement work; active shared prefixes are never preempted.
An idle warm producer has no active viewer admission slots. Dedicated mode retains its canonical broker input separately from the initiating viewer;
additional viewers still have independently admitted, drained broker inputs, not additional FFmpeg producers. These active and warm input leases remain
subject to the worker's outstanding broker-call budget. Worker `/health` and passive `/media/sessions` inventories distinguish viewers from producers;
inventory and broker-status polling do not refresh media inactivity.

Watch hides manual stream policy controls behind **Override stream settings**. Each protocol, quality, video, audio, and
fallback-audio setting can independently use **Browser profile** or a manual value. Overrides are applied to a request-scoped
effective capability matrix, so an audio-only override preserves measured video, protocol, and subtitle decisions and never
modifies the browser's saved Watch Test profile. Shaka's in-player menu switches active audio renditions and text subtitles
without restarting the shared session; the bitmap-subtitle selector remains available independently of the override panel.
Every standalone text subtitle and detected embedded-caption track is prepared as a transient WebVTT sidecar so Lineup's Shaka
captions menu can offer all of them without restarting the shared session. The page-level
subtitle selector lists only bitmap burn-in choices. Standalone text conversion runs in the main media process; enabled embedded-caption extraction
adds a separate decode/demux process per shared CMAF producer, even when captions are hidden. Measure that process separately rather than
assuming that GPU encoding offloads it. Bitmap subtitles remain explicit burn-in choices because each simultaneous bitmap rendition
would require another video encode.
Dedicated Watch metadata and remote stop notifications are associated with the returned worker media-session ID, not the broker stream ID.
Management-hosted playback retains client-ID association. Old or unrelated worker sessions cannot displace current track information or
stop replacement playback; dedicated broker ownership is also released if browser-side server shutdown fails.
Dedicated Web Player listeners expose the incremental subtitle offset header to cross-origin Watch clients, so captions continue beyond
the first 64 KiB. Each subtitle track advances by the received byte count; a missing or inconsistent offset header produces one browser
console warning per track without resetting its read position.
Captions that the tuner and FFmpeg do not expose or detect remain unavailable. Shared DASH and HLS presentations are served through the
canonical `/api/stream/cmaf` API.

Encoded video schedules keyframes relative to the preceding forced keyframe, keeping segment cuts at least
two seconds apart even at fractional frame rates such as 29.97 fps. This avoids skipped DASH cut points and
unexpected roughly four-second segments; source-copy renditions retain their original keyframes.
The opt-in streaming sweep verifies retained MP4 timestamps against DASH timelines for both DASH and HLS.
Independent audio/video segment numbers and buffered endpoints are diagnostics, not lip-sync measurements.

Software H.264 CMAF uses automatic frame threading for better throughput, with additional encoder memory and
buffering. This applies in management and dedicated hosting, including software retries; copy and hardware
encoding remain unchanged.

Watch and Watch Test stop their owned server viewer before awaiting browser-player destruction, so a delayed
or failed Shaka teardown cannot skip the server stop. Stop requests retain `keepalive` for navigation and
check HTTP responses; HTTP 404 means the viewer is already absent, while other failures are surfaced.
Failed server stops retain their captured ownership for a subsequent stop/disposal attempt. Cleanup remains
viewer-scoped and does not stop another browser's shared-output viewer.

## Watch Test

**Watch Test** at `/watch-test` keeps its manual, tuner-free exact-presentation player and adds an explicit **Run All**
playback validation suite. The suite measures DASH and HLS, H.264, HEVC Main and Main 10, AAC through 7.1, AC-3/E-AC-3 through 5.1,
WebVTT sidecars, and subtitle burn-in as independent cases rather than an impractical full cross-product. Every generated
presentation contains one video and one audio rendition and never retries another protocol or codec. Its audio contains a
low-volume repeating tone sequence that activates one channel at a time. Manual Exact Test Selection
starts with audio enabled for speaker-order verification; if the browser blocks audible autoplay, use the native Play control.
WebVTT and burn-in subtitle modes identify the active channel in sync with its tone; None omits the label. Automated full-suite
and row tests remain muted. Each synthetic presentation is bounded to ten minutes so its repeating audio, WebVTT timeline, and
burned-in labels share the same lifetime.
Browser Media Source claims are stored separately from observed Shaka playback and do not authorize source copy by themselves. A completed,
versioned profile is saved in browser-local storage and sent only with that browser's tune requests; canceled or incomplete
runs keep the previous completed profile. AC-4 remains claim-only because bundled Jellyfin FFmpeg has an AC-4 decoder but no
AC-4 encoder. Auto planning copies measured-compatible source media and otherwise emits one best-supported transcode,
prioritizing channel preservation before codec quality. Profiles with an old schema or invalid catalog data are ignored or
rejected, and a runtime contradiction clears the profile so it can be rerun. Watch Test never modifies global
transcoder settings.

Future CMAF work includes an adaptive video bitrate ladder, LL-HLS and low-latency DASH evaluation, and a native Safari HLS
path. Native Safari, Dolby audio, AirPlay, background playback, and power behavior require validation on real macOS and iOS
hardware.

WebVTT sidecars live only beneath the application-owned `lineup-subtitles` temporary directory. They are removed on
disconnect, stop, application startup, and Factory Reset. If captions do not appear, inspect the active-stream details and
logs: captions embedded in video but not exposed by the tuner or FFmpeg as an addressable subtitle stream can be preserved
in MPEG-TS but cannot be synthesized as a Watch text track.

## Avoiding an unnecessary Web Player video encode

Physical and virtual video policies are independent, even for one viewer. If physical processing already produces
compatible progressive H.264, the Web Player can normally copy that video into CMAF rather than encode it again.
This keeps physical normalization and its shared output intact; it does not bypass either processing stage.
Factory defaults do not force video encoding. Check the effective inherited settings before changing a saved configuration.

| Setting | Configuration to review |
|---|---|
| Physical device video profile | Keep the normalization, deinterlacing and caption-preserving encoder required by the source and other consumers. |
| Virtual Devices > Web Player > Always transcode video | Set an explicit device override to **Off** when this output should use normal copy selection. Merely clearing the override can inherit **On** from virtual defaults. |
| Watch quality | Use **App Default** for an eligible video-copy path; an explicit scaled-quality choice requests encoding. |
| Watch video policy | Use **Auto** with a completed browser profile that supports the effective source, or **Source** after verifying browser playback. **H.264 Fallback** requests encoding. |
| Web Player video encoder | Keep the desired encoder for cases that still require conversion. Selecting a GPU does not force an encode, and an eligible copy path does not use it. |

Make the change in an isolated test configuration first, after recording the original values and stopping its viewers.
Leave unrelated physical, audio, caption, bitrate and encoder settings unchanged so the comparison isolates the output's
forced-video policy. A video-copy path does not apply the output encoder's bitrate limit; the physical/source bitrate
still determines the copied video's bandwidth.

Check the effective FFmpeg commands and decoded playback, not just the saved settings or the number of FFmpeg processes.
An eligible output uses `-c:v:0 copy`, while a required physical normalizer may still use an encoder such as `libx264`.
MPEG-TS-to-CMAF packaging, audio conversion and the separate embedded-caption-to-WebVTT extractor do not by themselves
mean that video is encoded twice. These operations can still consume CPU when the video is copied or GPU-encoded.

Repeat matched forced-encode/copy comparisons and check additional channels and both management and dedicated hosting
before adopting the setting. Preserve resolution, frame cadence, captions, audio, sustained decoded playback and cleanup;
use the [manual streaming harness](../../../../tests/Integration/Lineup.Integration.Streaming.Sweep/README.md) for playback validation.
Output encoding remains expected for explicit forced encoding, H.264 fallback, scaled quality, bitmap subtitle burn-in,
required deinterlacing of the effective input, or source video that cannot be copied/signaled for CMAF and the browser.
If source playback fails, retain the existing compatibility fallback rather than forcing copy for every channel.

## Optional embedded-caption extraction

**Settings > Virtual Devices > Web Player > Extract embedded captions** controls extraction for the whole built-in Web Player,
in both management and dedicated hosting. It is **Off** by default, including older configurations whose adapter object is `{}`.
Enable it explicitly when embedded-caption accessibility is needed; existing explicit saved choices are preserved.
The persisted Web Player adapter property is `"ExtractEmbeddedCaptions": true` to enable it; adapter-settings clients use the
boolean field `extractEmbeddedCaptions`.

When disabled, new presentations do not start an embedded-caption extractor or feed its input, and do not advertise embedded captions
in Watch or subtitle sidecars. Merely hiding captions in the browser does **not** disable extraction. Disabling extraction removes
embedded-caption accessibility for every Web Player viewer; standalone text subtitles and bitmap burn-in choices remain available.
Physical caption preservation, transcoder capability guards and other virtual outputs are unchanged.

An unexpected embedded-caption extractor exit is reported at Warning level with its session, exit code, source-termination reason
when known, and bounded FFmpeg diagnostics. Intentional playback shutdown does not report an extractor failure; this diagnostic does not automatically restart extraction.

Save settings and stop/restart playback to use the new policy. Existing management presentations retain their captured policy;
new presentations cannot reuse an incompatible warm producer. Dedicated listeners follow their existing settings-reconfiguration
lifecycle and may stop active playback. This is not a per-viewer switch or a hot-kill of an active caption process.

Management-hosted Watch Test startup owns its encoder and transient files until the manifests, fragments, and codec metadata are ready and its viewer is registered. Cancellation, readiness timeout, encoder exit, and startup errors terminate the encoder and remove its files even before registration. Successful presentations remain available until their viewer stops or expires.

Both management-hosted and dedicated shared Watch startup own readiness and their bounded deadline at the producer, not at the initiating HTTP request.
Management-hosted request and producer deadlines use the injected clock while retaining independent cancellation lifetimes.
Canceling or stopping one pending viewer releases only that viewer; remaining viewers still receive the ready presentation or the producer's terminal failure.
Dedicated startup preserves a known producer failure when cleanup cancels its viewers, retaining the specific process-failure or timeout response; explicit request cancellation remains cancellation.
Releasing the final viewer cancels pending work and cleans its encoder, input, and files when keep-warm is zero. Dedicated host-initiated stops are
acknowledged through worker status polling, normally within one second, without closing another viewer's shared input.
Management-hosted Dashboard and Watch stop requests dispatch observed asynchronous cleanup without blocking the interactive circuit;
service disposal joins that cleanup. Graceful CMAF shutdown releases every viewer before disposing its shared producer, including
independently admitted viewer inputs and status monitors.

Long-lived management playback ownership lives in a typed application service, not an MVC controller; dedicated playback uses a typed worker runtime.
Both reuse the same host-neutral viewer/producer coordinator and portable CMAF planning primitives. HTTP adapters only map requests, results, media files,
and public URLs. Portable FFmpeg and Web Player worker dependencies do not include Core application services, EF, or SQLite; trusted graph, settings,
guide, and subtitle-storage integration stays in `Lineup.Core.Hosting.Streaming`.

## Related documentation

[Project overview](../../../../README.md) | [Contributor standards](../../../../CONTRIBUTING.md)
