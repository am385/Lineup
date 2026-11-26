# Testing Lineup

Shared test conventions and specialized validation. These project-specific requirements supplement the canonical root CONTRIBUTING.md. Commands run from the repository root.

- [Running Tests](#running-tests)
- [Tests](#tests)

## Running Tests

```bash
dotnet test
```

The browser subtitle poller's deterministic JavaScript regressions use Node.js's built-in test runner, with no npm dependencies:

```powershell
node --test tests\Apps\Lineup.App.Web.Tests\Browser\subtitle-polling.test.cjs
node --test tests\Apps\Lineup.App.Web.Tests\Browser\shutdown.test.cjs
node --test tests\Apps\Lineup.App.Web.Tests\Browser\dashboard-resources.test.cjs
node --test tests\Apps\Lineup.App.Web.Tests\Browser\streaming-flow-graph.test.cjs
```

Run these tests when changing incremental caption delivery. They execute the production player script with controlled responses and timers;
they do not launch a browser or require tuner hardware. The dedicated worker's CORS response-header regressions also run in the normal .NET gate.
The shutdown regressions exercise owned stop routing, repeated-stop HTTP 404 handling, explicit HTTP/network failures,
player destruction failures and isolation from replacement players.
The dashboard-resource regressions cover visibility demand, non-overlapping callbacks, in-flight visibility changes and timer/listener cleanup.
The streaming-graph regressions use the real cached Cytoscape bundle to cover card-to-card SVG connections, graph reconciliation,
path highlighting, scroll preservation, resizing, and visibility-aware refresh/disposal. Build the Web app first to populate the hash-verified browser-asset cache.

Slate parser regressions additionally support native FFmpeg through `LINEUP_TEST_SLATE_FFMPEG`, an explicit executable path with drawtext support.
With that variable set, `ProtectedContentSlateServiceTests` renders one synthetic frame per case and checks literal text, disabled expansion and filter isolation;
it requires no tuner, network source or application deployment. Native cases explicitly skip when no executable is configured.
Run these cases on both Windows and Linux when changing FFmpeg slate text encoding; fake process tests do not exercise the parser.

Native MPEG-TS startup regressions use `LINEUP_TEST_MPEGTS_FFMPEG`, an explicit FFmpeg executable with lavfi, libx264 and AAC support.
With that variable set, `FfmpegStreamDiagnosticsTests.NativeMpegTs_MidstreamJoin_DiscoversParametersBeforeOutput` generates a
local transport with sparse program tables and video initialization, joins after its initial headers, and verifies complete
input discovery and decoded audio/video for Copy and forced H.264. No broadcast recording, network, GPU or tuner is required.
These cases explicitly skip when the executable is not configured.

Ordinary CI and development/release publishing share `.github/workflows/validate.yml`. Both Linux and Windows run the full Release build/test gate,
the browser regressions, formatting verification, and installed-worker smoke tests. The smoke tests use the hash-verified worker packages installed
by the Web host build, exercising Web Player startup/captions/stop and shared HDHomeRun/Lineup Link admission and crash recovery without physical hardware.
Publishing waits for both platforms to pass.
On Windows, CI runs one test module at a time with `--max-parallel-test-modules 1` so real SDK publishes, worker launches,
and FFmpeg diagnostics cannot compete across test assemblies. Each module retains its existing xUnit collection parallelism
and explicit concurrent-viewer/process coverage. No tests are filtered out, and the Linux test invocation is unchanged.

The streaming-sweep harness is checked in as an opt-in hardware certification test:

```bash
dotnet run --project tests/Integration/Lineup.Integration.Streaming.Sweep/Lineup.Integration.Streaming.Sweep.csproj -c Release -- verify-outcome-reporting
```

The executable is included in `Lineup.slnx` for builds and Visual Studio restore, with `IsTestProject=false`;
building the solution does not execute it, and `dotnet test` does not discover it as a test project.
Live sweeps remain manually invoked and must not run in CI/CD because they require exclusive access to physical tuner hardware.
See [`tests/Integration/Lineup.Integration.Streaming.Sweep/README.md`](Integration/Lineup.Integration.Streaming.Sweep/README.md).
The separate `Lineup.Integration.Streaming.Sweep.Tests` xunit project participates in the normal solution gate and
exercises only deterministic policies, persistence, ownership and reports; it never launches the live harness.

Packaging regressions run in `Lineup.Tools.VirtualOutputWorkerPackager.Tests` as part of the solution gate. They use the installed SDK to publish tiny owned temporary projects and verify shared inventory, manifests, cache repair and real MSBuild publish/opt-out behavior without hardware.
Temporary packaging and browser-asset SDK commands disable MSBuild node reuse, the CLI MSBuild server, and shared compilation in their child environment so build processes cannot retain deleted fixture working directories.
Worker publishing sets deployment mode with `PublishSelfContained`, not a global `SelfContained=false`: the latter can create distinct executable-reference build nodes that concurrently overwrite the same intermediate assembly. The real-SDK graph regression verifies one compilation per helper intermediate directory and preserves portable/self-contained runtime configurations.
RID-specific worker publishing also sets `PublishRuntimeIdentifier`: project-reference negotiation can remove `RuntimeIdentifier`,
otherwise causing executable helpers to infer the build host's architecture instead of the deployment target. The graph regression
cross-publishes to a different architecture and verifies both worker and helper dependency manifests without executing foreign binaries.
For installed Windows or Linux worker validation, set `LINEUP_TEST_WORKER_PACKAGE_ROOT` to a package tree before running `HdHomeRunWorkerProcessTests` or the `Worker_*` cases in `WebPlayerWorkerProcessTests`. These fixtures resolve hash-verified packaged workers instead of test-output assemblies and exercise local authenticated IPC, loopback HTTP and deterministic media cleanup. Explicit worker paths used by host-build tests take precedence. Leave the variable unset for ordinary source tests.

Guide-reader regressions retain pooled concurrent connections and verify distinct native handles while every owner remains open,
then begin independent read transactions. SQLite provider versions before 10.0.13 can lend the same pooled handle to two live owners
([upstream fix](https://github.com/dotnet/efcore/pull/39012)), causing nested-transaction failures. The direct `Microsoft.Data.Sqlite.Core`
reference supplies that driver fix without requiring an EF Core or Extensions upgrade. Preserve this coverage rather than
serializing readers or disabling pooling to hide the race.

## Tests

- Add or update focused regression tests for every behavior change and bug fix.
- Every test must contain explicit `// Arrange`, `// Act`, and `// Assert` sections.
- Test the requested outcome directly, including meaningful failure and edge cases.
- Keep tests deterministic and independent of execution order.
- Lineup Link loopback fixtures use `LoopbackHttpListener` to retry address conflicts during actual HTTP listener startup; a released TCP probe does not reserve an HTTP prefix.
- Own bUnit contexts with `await using` so component cleanup finishes before scoped services are disposed. The shared `LineupWebBunitContext` also surfaces renderer disposal failures.
- In bUnit tests, await `ClickAsync(new())`, `ChangeAsync(...)`, and other async event dispatch when the handler performs asynchronous work before asserting completion. Use synchronous dispatch only for synchronous handlers.
- When testing an in-flight operation, retain the `ClickAsync` task, assert the pending state, release any controlled dependency, and await the task before disposing the component.
- Pass the shared `LineupWebBunitContext.WaitTimeout` explicitly to bUnit waits instead of mutating the process-wide `DefaultWaitTimeout`.
- Use `ControlledTimeProvider` for elapsed-time behavior and wait for timer registration before advancing time. Use completion barriers or an operation's known pending state instead of sleeps to establish concurrency.
- Do not use short real cancellation timers to establish test ordering. First prove the intended operation is blocked, then cancel explicitly or advance its injected clock. Keep request, producer, and maintenance deadlines on the intended clocks.
- Do not let short worker leases bound real-process setup. Test crash/disconnect cleanup separately from precise lease expiration; use the same controlled clock for session authorization and maintenance sweeps in expiry tests.
- Release controlled dependencies in `finally` and observe all owned work before teardown. Keep real-time watchdogs bounded, but do not use elapsed-time performance thresholds to prove lifecycle ordering.
- Keep real-process readiness reports activation-specific so recovery never replaces a report still being inspected. Include drained child output when reporting an early process exit.
- On .NET 10 Windows, redirected process-output reads can block thread-pool workers for the child's lifetime. Keep long-lived test-worker output drains off the broker's thread pool and await them after stopping the child.
- When replacing a stateful NSubstitute callback, use `Configure()` so configuring a failure does not execute the previous callback. Verify Arrange leaves controlled workers alive until the intended exit or stop.
- Observe short-lived test processes through their original `Process` instance before handing ownership to the code under test; retain exit signals after disposal instead of reopening a PID that may already be gone. Cover probes that exit before the factory returns.
- Real Web Player process tests run in an isolated collection to keep unrelated launch/IPC stress from consuming production admission deadlines. Keep their explicit concurrent-viewer tests and production deadlines unchanged.
- Unobserved task calls (`CS4014`) and unawaited async assertions (`xUnit2021`) are errors in test code; retain and eventually await tasks intentionally started in flight.

## Related documentation

[Project overview](../README.md) | [Contributor standards](../CONTRIBUTING.md)
