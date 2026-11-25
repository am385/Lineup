# Lineup.Plugin.LineupLink

Authenticated Lineup-to-Lineup channel, guide and MPEG-TS sharing, including access grants and dedicated-listener policy.

- [Lineup Link](#lineup-link)

Each Lineup Link virtual device can issue multiple named access grants so separate remote consumers do not share one bearer token. Grants can be independently rotated, expired, revoked, and permanently purged while sharing the virtual device's channels, transcoding, listener, and security policy. New outputs contain no grants and therefore deny every remote request until the first grant is created. Create separate Lineup Link virtual devices when consumers need different output behavior or network isolation. The remote output stores only SHA-256 token hashes; each consuming physical device stores its usable token in adapter configuration. Connection bundles reveal the raw token only when a grant is created or rotated and must be handled as secrets.

A management-hosted Lineup Link output may export a bundle before **Public Lineup URL** is known. The bundle then contains a null address, and the consuming instance leaves its physical-device URL empty for later entry. Any supplied URL must be an absolute HTTP or HTTPS address that matches the output's transport policy. HTTPS uses normal operating-system certificate validation and can add an exact SHA-256 certificate pin. HTTP remains authenticated but unencrypted and should be limited to trusted networks.

Dedicated Lineup Link connection bundles use the same effective client-facing URL as the saved listener: the automatically derived address unless a manual override is enabled, with any published-port override applied. Export, copy, and display use this URL, including redacted display after reloading saved grants.

Lineup Link manifest, lineup, and guide requests have a 30-second total timeout covering response headers and body, including error responses. Timeouts, malformed JSON, and unsupported remote protocol versions are reported as recoverable remote request failures so other configured physical devices can still activate; caller cancellation remains cancellation. Live MPEG-TS streams do not have this timeout. If a remote stream rejects a revoked, expired, or otherwise invalid access grant with HTTP 401 or 403, Lineup reports a non-retryable failure for that source and can continue to the next equivalent source. Device probing still reports rejected credentials explicitly.

For management-hosted Lineup Link outputs, **Require HTTPS** selects which management-host transport is accepted and exported. A dedicated Lineup Link output derives this requirement from its client-facing public base URL rather than its internal listener transport. This permits an HTTPS public URL to terminate TLS at a trusted reverse proxy while the dedicated worker listens over private HTTP. The optional connection-bundle certificate pin is independent of the dedicated listener's server-certificate reference.

## Lineup Link

A provider instance can add a **Lineup Link** virtual device and export its published lineup, optional guide, and post-output-transcode MPEG-TS streams through `/api/lineup-link/v2/virtual-devices/{virtualDeviceId}` when management-hosted or the equivalent path on its dedicated listener. A consuming instance imports the generated connection bundle into a **Lineup Link** physical device and may add a separately managed Lineup Link guide source. The virtual output uses Lineup's existing shared acquisition and MPEG-TS transformation graph, so consumers requesting the same effective source and output profile reuse the physical stream, physical normalization, and virtual transform.

An output configured to **Copy** AC-4 retains its compressed audio and MPEG-TS identification even when video is encoded
or other audio tracks are converted. The shared provider pipeline preserves copied AC-4 packets and repairs their program-map
registration without a modified FFmpeg binary. The receiving instance still needs an AC-4-capable decoder or an appropriate
audio conversion policy. Update the providing instance to obtain this fix: updating only the receiver cannot recover audio
packets discarded by an older provider. This does not change remote source-acquisition deadlines or experimental resampling.

**Dashboard > Physical Devices** shows source signal in the main table for each active Link stream slot.
The provider associates observations with the stream's actual source channel and an opaque stream session identity, not the local slot
index or exported channel number. Only the authenticated access grant's active streams are included. Unknown, failed, or ambiguous
source observations display N/A; measured zero remains 0%. Older peers without per-stream signal support remain usable.
An unused Link slot does not mean a remote hardware tuner is idle.
The host's **Settings > Virtual Devices > Lineup Link > Expose upstream tuner status** switch is off by default, including for existing
configurations that omit it. Enabling it additionally publishes the **Upstream tuner status (read-only)** table, including every reported
receiver on each enabled, compatible subscribed physical source and tuners occupied by other consumers. Disabling it removes this inventory
from responses, not just from the client UI; signal for the grant's own active streams remains available.
Local slot ownership and advertised stream capacity remain unchanged; hardware observations do not reduce admission capacity or disable stream sharing.

Management-hosted and dedicated-worker manifests publish these optional observations from the provider's existing physical-status cache;
manifest requests never poll tuner hardware. Device names, adapter kinds, receiver/channel activity, available signal measurements,
UTC observation times, and safe current refresh errors are shared, but stream targets, addresses, credentials, descriptive configuration,
unsubscribed devices, and remote control operations are not. Missing signal measurements remain unavailable rather than zero.
Failed refreshes retain explicitly labeled last-known observations; devices without an observation do not acquire invented idle receiver rows.
Receiver-status failures are tracked independently of descriptive-information refreshes: an information-only refresh neither suppresses
healthy signal nor clears a failed receiver observation. Only a successful receiver-status refresh restores signal publication.
Refresh frequency follows the provider's physical-status schedule and the consuming instance's Link status refreshes.
Update both instances, including the provider's matching dedicated worker package where applicable, to display upstream telemetry.
Disabled or unsupported upstream inventory does not add an extra dashboard section; manifest bodies retain the 1 MiB control-response limit.

A Lineup Link guide source subscribed to multiple remote outputs merges their root supplemental metadata into one envelope before import. The first subscribed output wins conflicting root attributes with the same qualified name; all preserved root elements remain in subscription order. Channel and program metadata retain their remote-output-qualified identities. Invalid root envelopes fail the refresh without replacing the previously committed guide, rather than deferring failure until XMLTV or JSON publication.

Management and dedicated Lineup Link guide exports use the same mapping and merge contributing root metadata into one valid envelope. The first contributing source wins conflicting qualified root attributes, all preserved root elements retain source order, and channel/program metadata remains intact. Zero envelopes produce no root metadata; multi-source exports can be imported by another Lineup instance without concatenated XML roots.

Guide exchange supports at most **2,000 channels, 100,000 programs, and 128 MiB of UTF-8 JSON per response**.
The byte limit includes the IPC envelope where applicable; metadata size and transport representation determine how much coverage fits.
These are transfer safeguards, not limits on the local guide database or file publications. Management and dedicated Lineup Link exports
preflight the complete response and return HTTP 503 with `guide_limit` rather than truncate an oversized schedule.
Dedicated guide IPC retains its existing length-prefixed JSON shape but serializes and reads directly from the stream instead of
building a second JSON document and full byte buffer. Each response uses one coherent database/configuration generation; there is no paging
or mixing of generations. Original snapshot objects and imported guide objects still consume memory proportional to the accepted guide.

Lineup Link ingestion checks declared and actual body bytes, including chunked responses, and counts channels/programs incrementally
before accepting the guide. Oversized, malformed, truncated, or canceled responses fail the refresh without replacing the committed guide.
Manifest/lineup bodies are limited to 1 MiB and error bodies to 64 KiB; the existing 30-second control deadline and live-stream behavior are unchanged.
Reduce the output's selected channels, source guide coverage, or metadata size when a guide exceeds the transfer envelope.

Lineup Link virtual devices cannot subscribe to Lineup Link physical devices, preventing recursive links. Under the Web host, each output can reuse the management host's ports or run in its own dedicated worker. The TUI can configure and supervise dedicated output workers but does not expose the Web management routes. New outputs start with no access grants, so every remote request is denied until a user explicitly creates one named grant per remote consumer and copies or exports its one-time connection bundle. Rotate only the affected grant when its raw token is unavailable or may have been exposed, or revoke it without interrupting other consumers on the same virtual device. Revoked grants may be permanently purged after confirmation when their retained identity and lifecycle history are no longer needed, including the final grant to return the output to its deny-all state.

## Related documentation

[Project overview](../../../../README.md) | [Contributor standards](../../../../CONTRIBUTING.md)
