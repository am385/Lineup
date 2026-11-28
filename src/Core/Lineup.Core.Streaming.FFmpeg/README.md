# Lineup.Core.Streaming.FFmpeg

Portable media processing, transcoding, hardware encoding and synthetic slate policies. Deployment examples run on the Docker host unless stated otherwise.

- [MPEG-TS virtual outputs](#mpeg-ts-virtual-outputs)
- [Transcoding and shared stream graph](#transcoding-and-shared-stream-graph)
- [Tuning retained physical normalization](#tuning-retained-physical-normalization)
- [Optional GPU video encoding](#optional-gpu-video-encoding)
- [GPU access in Docker](#gpu-access-in-docker)

## MPEG-TS virtual outputs

The MPEG-TS proxy preserves every audio track and every standalone subtitle codec the MPEG-TS muxer can represent
(`dvb_subtitle` and `dvb_teletext`). By default it copies video and non-AC-4 audio while converting each AC-4 track to AC-3.
Language, title, and disposition metadata are retained where the container supports them. Embedded CEA-608/708 data is
retained with copied video and passed to the H.264 encoder when video conversion is enabled. Text/WebVTT, ASS, PGS, DVD,
and other subtitle formats that MPEG-TS cannot carry are reported in diagnostics rather than mapped.

When **AC-4 Audio** is **Copy**, both physical normalization and virtual MPEG-TS processing retain the original compressed
AC-4 payload, including when video is software/GPU encoded or another audio track is converted. Lineup scopes FFmpeg's
`-copyinkf` to copied AC-4 tracks, assigns their output PIDs explicitly, and restores the `AC-4` registration descriptor
in the output program maps. This compensates for FFmpeg's initial-packet gate and missing MPEG-TS registration without
changing the FFmpeg binary. It does not decode, resample, or retime copied audio, and does not add AC-4 support to clients
that cannot decode it. Encoded AC-4 and experimental timestamp compensation retain their separate behavior.

The shared producer repairs signaling before fan-out to viewers or dedicated workers. It validates PAT/PMT sections,
handles split sections and descriptor growth, and updates table lengths, CRCs, versions and continuity without changing
media/PCR bytes. Invalid or missing signaling fails explicitly rather than reporting video-only success. Table acquisition
or an incomplete section can retain at most 4,096 transport packets (752 KiB plus parser state); PMT section lengths remain
within the MPEG-TS 1,021-byte limit, with at most 256 active program maps.

Each new virtual MPEG-TS transform probes its own input, including when source metadata was already cached.
Like physical normalization, it allows up to 10 seconds of media analysis and 10,000,000 probe bytes.
Shared subscriptions start at the current producer position, not at a replayed header or keyframe; broadcast program tables
and codec initialization can recur beyond the former one-second/1,000,000-byte limits. Premature discovery could leave
AC-4 with no sample rate/channels and fail MPEG-TS header creation, or leave video parameters unknown.
These are bounded input-discovery limits, not a fixed sleep or an extension of Lineup Link's acquisition deadline.
Sparse initialization may increase startup latency; unavailable or malformed initialization can still fail explicitly.

## Transcoding and shared stream graph

Virtual Tuner Video can optionally transcode HEVC to H.264 for clients that cannot process HEVC Live TV, at the cost of CPU and generation loss. Virtual Tuner Audio can preserve non-AC-4 codecs or transcode those tracks to AC-3/E-AC-3. AC-4 can be preserved for compatible receivers or transcoded separately. Audio above 5.1 channels is capped at 5.1 when transcoded. Transcoding is configured through inherited physical and virtual device profiles; topology routes reject unsupported per-request HDHomeRun hardware profile parameters.

Software H.264 uses a shared automatic frame-threading policy across physical normalization, virtual MPEG-TS,
Web Player CMAF and channel slates. This improves encoding throughput at the cost of additional memory and
encoder buffering. Copy and hardware encoding, quality settings, MPEG-TS repeat headers and caption handling
remain unchanged.

## Tuning retained physical normalization

For software H.264 normalization, **VeryFast** is the lowest-CPU preset currently exposed by Lineup.
**Faster**, **Fast** and **Medium** spend additional encoding work on compression; their names do not
mean they are faster than VeryFast. A lower maximum bitrate is a bandwidth/quality constraint, not
a CPU-usage target. Keep CRF, resolution, source content and deinterlacing cadence visible when comparing settings.

**Source Field Rate** retains field-rate motion when deinterlacing confirmed interlaced video.
Changing to **Source Frame Rate** reduces the resulting frame cadence for that source; treat it as a
visible motion-quality trade-off, not an equivalent-output CPU optimization.

Compare supported settings in an isolated configuration with the eligible output copying video, so
a second encode is not charged to the physical normalizer. Repeat observations, verify the actual
encoder/filter commands, decoded playback, captions and shared-viewer cleanup, and retain the original
settings if a safer CPU improvement is not established. Do not disable caption-preservation guards to
make a hardware backend eligible.

## Optional GPU video encoding

Physical and virtual **Transcoding Defaults**, and their per-device overrides, include a **Video encoder** setting.
Software remains the default, including for existing settings files. The complete hardware setting inherits as one unit;
an explicit device override of **Software** disables inherited GPU encoding. Selecting a GPU does not force conversion:
copy-only and audio-only conversions remain unchanged. Hardware settings also participate in producer-sharing identities.
**Always transcode video** is an opt-in physical and virtual profile setting, with independent per-device overrides.
Physical encoding happens once in the shared source pipeline before multiplexing; virtual encoding happens for that output.
It encodes video as H.264 even when the source could otherwise be copied, using the selected encoder and video bitrate limit.
An explicit device override of **Off** disables inherited forced encoding without disabling other conversion policies.
Encoder controls are shown when the effective profile requests video encoding, including this option.
Enabling forced encoding at both stages performs two encoding passes. Leave it off at stages that should retain normal copy selection.
The bitrate limit uses encoder rate control with a buffering window; it is not an instantaneous network traffic limit.
Allow additional bandwidth for audio and transport overhead, including bursts while downloading HLS/DASH segments.

| Backend | Native platform | FFmpeg H.264 encoder | Optional device |
|---|---|---|---|
| Intel QSV | Windows, Linux | `h264_qsv` | Windows adapter index; Linux `/dev/dri/renderD128`-style render node |
| NVIDIA NVENC | Windows, Linux | `h264_nvenc` | Numeric GPU index visible to FFmpeg |
| VA-API | Linux | `h264_vaapi` | Render node; defaults to `/dev/dri/renderD128` |
| AMD AMF | Windows | `h264_amf` | Numeric adapter index |
| Apple VideoToolbox | macOS | `h264_videotoolbox` | System-selected device |

The GPU device picker offers **Default (automatic)**, detected device candidates and **Custom device** for manual entry.
Discovery runs on the Lineup management host/container, not the browser, and is independent of Dashboard monitoring.
Windows QSV and AMF candidates use their original DXGI adapter indices, filtered by vendor and excluding software adapters.
NVENC uses CUDA device ordinals, not DXGI or NVIDIA monitoring-library indices. Linux QSV lists Intel render nodes;
VA-API lists exposed render nodes with vendor labels. VideoToolbox uses the system device.
Discovery does not prove encoder capability or device permissions: the existing FFmpeg startup check still validates the selected configuration.
Saved device values remain intact even when discovery fails or cannot find the device, and manual entry remains available.
Device lists are discovered lazily, cached per backend, and refreshed by restarting Lineup; discovery never starts an encoding workload.

This first implementation accelerates **encoding**, not decoding or filtering. Deinterlacing, scaling and subtitle burn-in
remain CPU filters; VA-API explicitly uploads their output. Audio conversion, source/copy policy, captions and CMAF
keyframe cadence remain independent. Software presets/CRF are not GPU quality controls: use the backend's balanced,
speed or quality tuning and its advanced options. QSV and VA-API MPEG-TS encodes repack embedded captions after deinterlacing
and before hardware upload/encoding, keeping rate-correct A53 packets within Intel's SEI limits without disabling captions.
Docker and Windows packages bundle Jellyfin FFmpeg **8.1.3-2**, including its oversized QSV A53 caption SEI guard.
Caption normalization remains necessary to preserve captions instead of relying on that guard to drop oversized packets.
Caption-preserving QSV MPEG-TS encoding also retains `async_depth=1`: multiple in-flight caption payloads crashed the previously
bundled Intel runtime even after normalization. A conflicting advanced `async_depth` is rejected; QSV paths without embedded-caption
insertion retain their configured/default submission depth.
This requires a known output rate of 24, 24000/1001, 30, 30000/1001, 60, or 60000/1001 frames per second;
other or unknown rates and currently unverified caption encoders fail explicitly. Select Software or NVENC for those sources. CMAF keeps its parallel caption
extraction. QSV and VA-API Web Player transcodes explicitly disable redundant embedded A53 caption insertion, avoiding
broadcast-caption SEI payload handling in those encoders while retaining the separate WebVTT captions. VA-API's other
default SEI messages remain enabled. Copy-only video and other encoders retain their existing caption policy.

Jellyfin caches HDHomeRun live-TV probe metadata by channel, independently of Lineup's current output profile. Changing a
channel from forced H.264 encoding back to MPEG-2 pass-through can therefore leave Jellyfin copying MPEG-2 into a browser
session negotiated as H.264, producing audio without decoded video. Browser reloads do not clear that server-side cache.
Keep a stable output codec for a Jellyfin virtual device, or invalidate the affected Jellyfin live-TV probe cache when changing
its codec policy. Lineup does not change Jellyfin settings or automatically clear its cache.
HDR tone mapping, additional codecs and zero-copy/hardware decode are not implemented.

QSV Web Player transcodes use closed, frame-rate-based GOP scheduling instead of timestamp-forced keyframes:
the GOP and minimum keyframe interval are the ceiling of two seconds times the effective output frame rate.
If probing cannot supply a usable rate, Lineup retains a bounded 120-frame GOP and reports the missing rate;
two-second cadence is not guaranteed in that case. Already-transcoded, confirmed-interlaced QSV Web Player video
automatically uses source-field-rate deinterlacing when the saved policy is Preserve. Explicit frame-rate and
field-rate policies are honored; progressive, unknown-scan and copy-only paths are unchanged.
The automatic exception leaves saved settings intact and is visible in output diagnostics. Field-rate deinterlacing
preserves field motion by doubling the progressive output frame rate, with additional CPU filtering and encoding work.
This behavior is specific to QSV Web Player output, not general physical or MPEG-TS hardware transcoding.

VA-API uses its driver's default preset (Balanced); numeric preset scales differ between Intel and Mesa/AMD drivers.
Use an explicitly supported `compression_level` in advanced options instead of assuming a portable speed/quality number.

The advanced field accepts **one `option=value` per line**, without a leading dash, up to 16 options and 4096 characters.
Options are backend-specific and allowlisted; values are single bounded tokens, not shell text. For example, NVENC accepts
`preset=p6` and `cq=23`. It cannot change inputs, outputs, stream maps, protocol access or arbitrary initialization commands.
Structured device selection supplies the initialization flags. A driver may still reject an otherwise well-formed
backend option; that is reported as a startup failure rather than a successful software stream.

Native applications need a GPU supported by the selected encoder, a compatible FFmpeg build, and the vendor's driver and
userspace runtime. The service identity must have permission to access the device; running FFmpeg successfully in an
interactive desktop account does not prove a system service can do so. The `-hwaccel` flag alone selects hardware
**decoding**, not the H.264 encoder. Listing `ffmpeg -encoders` or `ffmpeg -hwaccels` proves build support, not usable hardware.
Lineup checks encoder availability and performs a bounded synthetic encode in the actual host/worker launch environment
before consuming live input. Settings load/save never requires a GPU.

Startup fails with actionable diagnostics by default. **Allow software fallback before playback** is opt-in. It permits
software when the bounded GPU check fails or when the actual encoder reports a recognized initialization failure before
publication, and only after the previous process has been released. Input, network, muxer, cancellation and cleanup errors
are not general fallback permission. Existing source-audio fallback remains separate. Published streams never silently
restart with a different encoder. Requested encoder, successful checks and fallback are logged; management-hosted and dedicated Web Player
responses and active-stream diagnostics also report the requested/selected encoder and whether fallback occurred.
Management-hosted retries release the failed input and processor reservation and remove stale packaged media before replacement;
independently owned caption extraction remains active. Copy-only video plans do not require hardware verification.
The Dashboard's Source Processing and Output Processing nodes and selected-node diagnostics show their respective runtime encoders, for example
**GPU encoding · h264_amf**, **Software encoding · libx264**, or **Video copied (copy)**, with an explicit software-fallback
badge when applicable. These badges use producer-reported outcomes, not the configured GPU preference. Stages that do not
report an encoder display **Video encoder not reported**, never an inferred GPU label. Look under **Active Streams → Source Processing / Output Processing**,
and select a processing node for its session-specific diagnostics. Watch's **Stream Information** card shows separate
**Source Processing** and **Output Processing** encoder rows using the same stage projection. Both local Watch playback and
dedicated Web Player workers report the launched encoder after readiness, including viewers joining a shared producer.
Physical and MPEG-TS output transforms retain their actual hardware/software/fallback outcomes in the shared graph.
Each active path follows its exact producer ancestry; a GPU used by another channel on the same device is not attributed to it.
Copied video may still accompany audio conversion or remuxing; a video-copy badge does not mean all media processing is bypassed.
Viewers of the same physical source share one Web Player encoder when their effective media and permitted startup fallback
plans match. Browser identities, compatibility-test timestamps, DASH versus HLS playback, and Auto versus Override provenance
do not independently create another encoder. Shared track labels describe the media rather than the first viewer's policy;
per-viewer stream diagnostics retain the requested policy labels. Different encodings, selected tracks, subtitle processing,
source generations, or fallback contracts remain separate producers.

## GPU access in Docker

The default Compose files remain CPU-only. Device exposure and Lineup's encoder selection are **two separate steps**.
Containers need the same driver/runtime support as native execution; exposing a node does not install a working driver.

- **Linux Intel/AMD:** use `docker-compose.gpu-dri.yml` with either default Compose file. Set `LINEUP_RENDER_NODE` if needed
  and set `LINEUP_RENDER_GID` to the numeric group owning that render node (`stat -c '%g' /dev/dri/renderD128` on the host).
  Then run `docker compose -f docker-compose.yml -f docker-compose.gpu-dri.yml up -d` and select QSV or VA-API in Lineup.
  The selected node keeps the same path inside the container. Use a compatible Intel media/VA-API or Mesa driver in the
  image; ARM and older Intel devices may require different drivers. Do not use privileged mode or world-writable devices.
- **NVIDIA:** install a supported host driver and [NVIDIA Container Toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/latest/install-guide.html),
  then use `docker compose -f docker-compose.yml -f docker-compose.gpu-nvidia.yml up -d`. `LINEUP_NVIDIA_GPU` selects the
  host GPU index or UUID (default `0`); the override grants `video,compute,utility` driver capabilities. Select NVENC and
  the container-visible GPU index in Lineup, not necessarily its original host index.
- **Docker Desktop on Windows:** GPU access is documented for NVIDIA through WSL2; it is not a promise that a particular
  image, driver and NVENC build work. See [Docker GPU support](https://docs.docker.com/desktop/features/gpu/).
- **Docker on macOS:** ordinary Linux containers cannot use the host's VideoToolbox API. Use native macOS execution for
  VideoToolbox; Linux-container GPU support must not be inferred from native availability.

The pinned Linux amd64 Jellyfin FFmpeg package includes the AMD RadeonSI and Intel iHD/i965 VA-API userspace drivers.
The host still supplies the GPU and kernel driver. NVIDIA's matching driver libraries are exposed by the NVIDIA
Container Toolkit. The runtime image defaults `NVIDIA_DRIVER_CAPABILITIES` to `video,compute,utility`, so a separate
container variable is not required. This default does not install or activate the NVIDIA runtime or expose a GPU.

### Unraid container settings

These are deployment settings, separate from Lineup's transcoder settings. Enable Unraid's advanced container view to
edit **Extra Parameters**.

| GPU | Extra Parameters | Container variables | Lineup encoder |
|---|---|---|---|
| AMD | `--device=/dev/dri` | None normally required | VA-API |
| Intel | `--device=/dev/dri` | None normally required | QSV or VA-API |
| NVIDIA | `--runtime=nvidia` | `NVIDIA_VISIBLE_DEVICES=<GPU-UUID>` | NVENC |

Prefer exposing only the selected render node, such as `--device=/dev/dri/renderD128`, when access to the whole directory
is unnecessary. Select that same container-visible path in Lineup. A non-root container user also needs the render node's
numeric group, using `--group-add=<gid>` or the DRI Compose override. Do not solve permission failures with privileged mode
or world-writable devices.

For AMD/Intel, first configure the appropriate host graphics driver and verify that the render node exists on the Unraid
host. The older `modprobe i915` advice applies to supported Intel GPUs, not AMD or every Intel generation; host-driver
setup belongs on Unraid, not inside the container. NVIDIA requires Unraid's compatible NVIDIA driver/runtime setup.
The NVIDIA Compose override uses Docker GPU reservations instead of the Unraid `--runtime`/`NVIDIA_VISIBLE_DEVICES`
configuration and already requests the `video` driver capability needed for NVENC.

**AMD AMF is the Windows-native option in Lineup; choose VA-API for AMD Linux containers.** Docker Desktop's documented
Windows/WSL2 passthrough supports NVIDIA, not this AMD `/dev/dri` deployment path. Working AMF encoding in a Windows
debug session does not demonstrate GPU access from its Linux containers.

### Verify actual encoding inside the deployed container

Run the matching command on the Docker host after configuring passthrough. These checks use synthetic video, consume no
tuner, produce no media files, and exit after two frames. Replace `lineup` if the deployed container has another name and
adjust the render-node path or container-visible NVIDIA index when necessary. Use the same container user as the service.

AMD or Intel VA-API:

```sh
docker exec lineup ffmpeg -hide_banner -loglevel error \
  -init_hw_device vaapi=lineup_hw:/dev/dri/renderD128 -filter_hw_device lineup_hw \
  -f lavfi -i testsrc2=size=128x128:rate=25 -frames:v 2 \
  -vf format=nv12,hwupload -c:v h264_vaapi -b:v 1M -f null -
```

Intel QSV:

```sh
docker exec lineup ffmpeg -hide_banner -loglevel error \
  -init_hw_device vaapi=lineup_va:/dev/dri/renderD128 \
  -init_hw_device qsv=lineup_hw@lineup_va -filter_hw_device lineup_hw \
  -f lavfi -i testsrc2=size=128x128:rate=25 -frames:v 2 \
  -pix_fmt nv12 -c:v h264_qsv -b:v 1M -f null -
```

NVIDIA NVENC:

```sh
docker exec lineup ffmpeg -hide_banner -loglevel error \
  -f lavfi -i testsrc2=size=128x128:rate=25 -frames:v 2 \
  -c:v h264_nvenc -gpu 0 -b:v 1M -f null -
```

Exit code zero verifies a real encode with the explicitly named backend; these commands do not fall back to software.
Missing devices, permission errors, missing driver libraries or unsupported hardware are failures, not successful GPU
verification. Encoder listings and `nvidia-smi` alone do not prove that encoding works. Finally, force video encoding
through Lineup with software fallback disabled and confirm the actual encoder in the Dashboard or Watch stream information;
this additionally checks the application's host/worker path. A video-copy stream does not exercise the GPU.

Media processes are owned by their launching host or dedicated worker. Disconnect, cancellation, startup failure and normal
shutdown terminate and await owned media before releasing admission; worker recovery waits for predecessor cleanup.
OS lifetime containment also reaps media when an owner crashes, including host-launched FFmpeg/FFprobe and worker media.
Cleanup failure remains an error/quarantine, never permission to start overlapping GPU sessions. Driver or firmware hangs
cannot be universally repaired by Lineup: it never resets a shared GPU or kills unrelated processes by name.

Hardware certification remains manually invoked using the [streaming-sweep skill](../../../.github/skills/lineup-streaming-sweep/SKILL.md), outside the solution and
CI. Deterministic settings/planner/process tests are not certification of a specific GPU, driver or Docker host.
See [FFmpeg hardware devices](https://ffmpeg.org/ffmpeg-doc.html#Hardware-Devices),
[FFmpeg video encoders](https://ffmpeg.org/ffmpeg-codecs.html#Video-Encoders), and
[Docker Compose GPU reservations](https://docs.docker.com/compose/how-tos/gpu-support/).

Jellyfin FFmpeg's AC-4 support is decoder-only and does not support every object-based or Dolby Atmos AC-4 presentation. Unsupported presentations are reported as stream errors rather than silently copied or discarded. Watch copies eligible source audio and starts a configured fallback rendition only if source packaging or playback fails.

When an HDHomeRun reports DRM error 811, API streams return a protected-content error by default. The Settings page's Streaming tab can instead enable a synthetic **Content Protected** slate. The fallback is generated locally as H.264 video with silent AAC audio in the requested MPEG-TS or CMAF format; protected programming is never decrypted. Synthetic DRM and disabled-channel slates count toward **Maximum Concurrent Streams** but do not consume physical tuner capacity. Web-only refresh settings for active-stream information are configured on the Dashboard tab.

Synthetic slate channel labels remain literal FFmpeg drawtext values, including quotes, filter delimiters, whitespace and percent expressions.
Labels cannot introduce filters or `textfile` options, and drawtext expansion is disabled.

The slate policy also applies when protection is discovered during acquisition, even if cached DRM metadata is missing or stale.
Equivalent sources are tried first: a healthy source wins, and only an all-protected acquisition failure becomes a slate.
Mixed protection and availability failures remain explicit errors rather than being presented as protected content.

Shared FFmpeg transforms, MPEG-TS slates, and management/dedicated caption processes continuously drain diagnostics while retaining only the last 16,384 characters and at most fifty non-empty lines. Long warning lines cannot accumulate lifetime stderr in the trusted host; nonzero exits retain their exit code and recent diagnostic tail.
Unexpected exits after Web Player publication are reported at Warning with the exit code, drained diagnostic tail, and
any independently observed source-termination reason. Intentional stops are not reported as unexpected encoder exits.

## Related documentation

[Project overview](../../../README.md) | [Contributor standards](../../../CONTRIBUTING.md)
