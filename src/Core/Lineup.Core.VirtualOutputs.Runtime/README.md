# Lineup.Core.VirtualOutputs.Runtime

Trusted isolated-output activation, package validation, supervision, local IPC and capability ownership.

- [Hosting, ownership and resource policies](#hosting-ownership-and-resource-policies)
- [Resource-policy enforcement](#resource-policy-enforcement)
- [Plugin connection admission](#plugin-connection-admission)
- [Package validation and output recovery](#package-validation-and-output-recovery)
- [Broker cleanup and lease ownership](#broker-cleanup-and-lease-ownership)
- [External enrollment and managed liveness](#external-enrollment-and-managed-liveness)

## Hosting, ownership and resource policies

Network-facing Lineup Link, virtual HDHomeRun, and Web Player media outputs can be served through the management UI host or through one isolated worker process per virtual device. Management-hosted mode reuses the existing Lineup HTTP/HTTPS ports for simpler local deployments. A management-hosted virtual HDHomeRun needs its externally reachable management URL in **Advertised base URL** when network discovery is enabled. Dedicated mode gives the output its own sockets, firewall boundary, TLS and network policy, and process lifecycle. Dedicated workers receive instance-scoped capabilities over authenticated named-pipe or Unix-domain-socket IPC. A separate shared HDHomeRun discovery worker owns UDP 65001 and SSDP 1900 and receives only bounded advertisement snapshots for enabled virtual devices. Web Player presentation remains in Blazor in both modes; its selected host owns CMAF/HLS/DASH generation and delivery. Worker packages are hash-verified before launch, bootstrap credentials are one-time and generation-bound, leases fail closed, and TLS certificate bytes are delivered through activation stdin rather than exposing trusted-host certificate paths. The Web and TUI hosts both run the same trusted supervisor for outputs configured in dedicated mode; `Lineup:VirtualOutputs:WorkerPath` overrides the packaged `virtual-output-workers` directory.

Managed workers receive a unique temporary directory beneath the trusted host's operating-system temporary path through their sanitized environment. This keeps dedicated Web Player media artifacts writable for ordinary Windows users without inheriting the host's user profile or unrelated environment variables. The launcher removes the directory after terminating and disposing the worker, including failed launches, and logs cleanup failures with the affected path. These worker directories are separate from the management host's `Lineup:TransientPath` setting.

Worker lifetime containment is always active, including when CPU/memory limits are zero: Windows uses a kill-on-close job and Linux launches each worker in its own session with `setsid` from util-linux (included in the Docker image). Descendants are terminated before output streams are joined, even after a worker-only crash. Forced termination and output teardown share the configured drain timeout as a bounded cleanup budget, with a one-second minimum when graceful draining is disabled. Concurrent disposal awaits the same cleanup task. The supervisor finishes cleanup before scheduling recovery; failed cleanup is logged and quarantines the output instead of automatically launching another worker. This also applies to expired leases and missed bootstrap registration: cleanup failure does not stop healthy sibling outputs or end supervision. Activation generations remain monotonic across revocation within the host lifetime, and delayed status reports cannot overwrite a replacement or recreate removed output status.

## Resource-policy enforcement

Nonzero worker CPU/memory policies are preflighted before saving changed topology and before launching a worker. Zero remains
opt-in/unlimited and requires no resource-controller privileges. Unsupported enforcement fails closed with an actionable
configuration/runtime error; it never silently launches an unlimited worker or repeatedly spends its restart budget.
An existing unsupported deployment can still load its settings and perform unrelated timestamp updates; correcting the output
configuration permits activation again without a factory reset.

On Linux, use cgroup v2 with a writable delegated parent, enable the requested `cpu`/`memory` controllers for its children,
and set `LINEUP_CGROUP_ROOT` to that parent when `/sys/fs/cgroup` is not the delegated location. Lineup probes a temporary child
and requires writable kernel-created controller and process-membership files; it does not enable controllers or make the
container privileged. Actual worker attachment remains fail-closed if permissions change after preflight. Linux memory
limits cover the worker cgroup and descendants; its CPU quota is a percentage of one CPU. Windows uses a job CPU-rate cap
and **per-process** memory limits, not one aggregate deployment-memory cap. Job setup is probed first, and a failed process
attachment terminates the worker and reports a permanent deployment-policy error.
Externally launched workers must enforce limits in their external supervisor and leave Lineup's host CPU/memory policy at
zero, because the host does not own those processes. None of these worker policies constrain management-host FFmpeg or
the shared graph; use deployment/container limits for a deployment-wide CPU or RSS ceiling.

## Plugin connection admission

**Streaming > Deployment-wide plugin connections** sets `Streaming.MaximumPluginConnections` for all management-hosted and dedicated plugin outputs in one Lineup deployment. Zero is unlimited.
One TCP connection consumes one slot from its first eligible plugin request until disconnect, including idle keep-alive time; concurrent HTTP/2 requests on that connection share the slot.
Management UI/settings/status/enrollment, playback stop/session inspection and synthetic Watch Test routes are exempt, even on a connection whose plugin request was refused.
Unadmitted idle sockets and TLS handshakes do not consume this budget; it is not transport-level denial-of-service protection.
Saturation returns HTTP 429; an unavailable admission control plane returns HTTP 503. Lowering a limit does not disconnect existing clients: new connections are refused until usage falls below it.
The status API's `pluginConnections` reports authoritative usage, management/worker attribution and `isDraining` while usage exceeds a reduced limit.
Dedicated workers retain one authenticated IPC ownership channel per admitted TCP connection, outside broker-call and media-stream quotas.
Losing ownership aborts the public connection; revocation retains its slot until ownership cleanup completes. Workers never fall back to independent local limits.
Independent Lineup control planes are separate deployments, not a distributed quota cluster. Streaming schema 1 and worker IPC protocol 1 are the initial Lineup 3.0 contracts; unreleased 3.0 development changes do not introduce additional version levels. The intentional Lineup 2.0-to-3.0 factory-reset boundary remains; no migration is provided.

## Package validation and output recovery

Worker resolution isolates malformed, invalid, unreadable, or version-incompatible package manifests and logs each rejection with
the manifest path and failure reason, allowing independent healthy workers to start. An unresolved adapter reports the rejected-package
diagnostics explicitly; duplicate valid matches and unsupported platforms remain errors. Strict full-catalog validation still fails
on any invalid package. Repair or remove rejected packages rather than treating a healthy worker's startup as proof that every package is valid.

Output reconciliation cleans up only identities owned by the configured-output supervisor, including failed activations and externally supervised outputs. Removing, disabling, or returning an output to management-hosted mode revokes that output's credentials and removes its enrollment and status without revoking the independent shared HDHomeRun discovery worker.

Dedicated-output transitions run up to four at a time, with each output serialized independently. Queued transitions use the latest
saved settings; slow starts or drains do not hold a deployment-wide liveness lock. Shutdown drains all owned outputs concurrently
within one shared 30-second grace period, or a shorter host shutdown deadline, then forces remaining owned-process cleanup.
Shorter configured per-worker drain deadlines still apply. The grace period bounds graceful waiting, not operating-system process
termination or final resource disposal; cleanup is attempted for every worker even if another worker fails to stop.

In **Settings > Virtual Devices**, a failed dedicated output exposes **Revalidate and retry worker** through the existing management
interface. It uses saved settings, never saves the editor's draft, and refuses running workers. The action reruns package, provenance,
resource-policy, and certificate checks, resets only the selected failed output's restart allowance, and preserves its prior failure
and recovery-attempt time in runtime status. Subsequent failures follow the unchanged automatic backoff and quarantine policy.
Use **Refresh** to check reported readiness and current diagnostics. External-process recovery issues a fresh enrollment; it never
launches or terminates an externally owned process. Once accepted, recovery belongs to the host and continues if the requesting
settings page is closed. Healthy siblings and the separately owned shared discovery worker are not restarted.

Managed child disposal remains mandatory when host shutdown cancels an in-progress drain or forced termination reports a failure. Cleanup terminates any remaining child before the interrupted stop finishes, without relying on later worker lease expiry; caller cancellation and stop failures still propagate after cleanup.

## Broker cleanup and lease ownership

Orphaned broker streams are cleaned independently: a disposal failure is logged with its stream, request and virtual-device identity
without disabling later sweeps. Final broker shutdown attempts every remaining stream and reports incomplete disposals together.
Unexpected orphan-maintenance faults surface through the IPC hosted service instead of remaining hidden until shutdown.

Broker stream acquisition and media copying are canceled when their owning IPC connection disconnects, including before acceptance
or while an upstream read is idle. Cleanup releases the request's broker-call budget; a stream returned after cancellation is still disposed.

Malformed certificate references or unreadable fingerprint metadata fail only the affected output, retire its obsolete worker/enrollment,
and report a safe configuration error without exposing certificate paths in public status. Correcting configuration permits reconciliation;
independent outputs keep running.

Worker lease renewal holds the session-authority lock from authorization through lease mutation, so concurrent revocation or
replacement cannot update another activation or cause a missing-activation lookup. Old, expired, stale-generation, or
capability-denied requests retain the explicit rejection/drain response; accepted renewals extend only their owning policy lease.

External worker lease expiry is checked periodically without requiring an enrollment download. An expired registered worker becomes
failed/unavailable until re-enrollment and readiness restore it; Lineup does not launch, terminate or automatically restart the externally
owned process. Delayed registration, renewal or health status from an older generation cannot overwrite a newer generation's status.

## External enrollment and managed liveness

Configured worker CPU and memory limits use Windows Job Objects or Linux cgroup v2. Linux deployments requesting limits must grant Lineup permission to create child cgroups; activation fails explicitly rather than silently running without the requested boundary.

Managed mode starts verified packaged workers automatically. External mode creates a one-time enrollment descriptor for a local service manager; retrieve it once from the management host with `POST /api/virtual-outputs/{virtualDeviceId}/enrollment` over a loopback connection and deliver the JSON through the worker's standard input. The descriptor contains a short-lived secret and must not be logged, stored in source control, or sent over the public output network.

Managed dedicated-output workers must complete bootstrap delivery and register before their one-time credential expires (one minute by default).
The supervisor checks managed-worker liveness every second. A missed registration deadline or an inactive/expired worker session revokes the
worker's access and forcibly stops the process, even when it has not exited on its own. Recovery uses the configured restart window, backoff,
and maximum restart count; an exhausted worker remains failed until its configuration changes, it is disabled and re-enabled, or
an operator explicitly revalidates and retries that failed output from Settings.
Healthy lease renewals do not recycle the worker. External-process outputs remain the responsibility of their external process supervisor.

## Related documentation

[Project overview](../../../README.md) | [Contributor standards](../../../CONTRIBUTING.md)
