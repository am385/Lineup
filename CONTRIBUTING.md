# Contributing to Lineup

Thank you for considering contributing to Lineup! This document provides guidelines and steps for contributing.

## Development Setup

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (version pinned in `global.json`)
- An HDHomeRun device on your local network (for integration testing)
- Git

### Building

```bash
# Clone the repository
git clone https://github.com/am385/lineup.git
cd lineup

# Restore and build
dotnet restore
dotnet build
```

All project build outputs and intermediate files are centralized beneath the repository-level `out` directory.

### Running Tests

```bash
dotnet test
```

The browser subtitle poller's deterministic JavaScript regressions use Node.js's built-in test runner, with no npm dependencies:

```powershell
node --test tests\Apps\Lineup.App.Web.Tests\Browser\subtitle-polling.test.cjs
node --test tests\Apps\Lineup.App.Web.Tests\Browser\shutdown.test.cjs
```

Run these tests when changing incremental caption delivery. They execute the production player script with controlled responses and timers;
they do not launch a browser or require tuner hardware. The dedicated worker's CORS response-header regressions also run in the normal .NET gate.
The shutdown regressions exercise owned stop routing, repeated-stop HTTP 404 handling, explicit HTTP/network failures,
player destruction failures and isolation from replacement players.

The streaming-sweep harness is checked in as an opt-in hardware certification test:

```bash
dotnet run --project tests/Integration/Lineup.Integration.Streaming.Sweep/Lineup.Integration.Streaming.Sweep.csproj -c Release -- verify-outcome-reporting
```

It is intentionally excluded from the solution and CI/CD because the full sweep requires exclusive access to physical tuner hardware. See [`tests/Integration/Lineup.Integration.Streaming.Sweep/README.md`](tests/Integration/Lineup.Integration.Streaming.Sweep/README.md).
The separate `Lineup.Integration.Streaming.Sweep.Tests` xunit project participates in the normal solution gate and
exercises only deterministic policies, persistence, ownership and reports; it never launches the live harness.

Packaging regressions run in `Lineup.Tools.VirtualOutputWorkerPackager.Tests` as part of the solution gate. They use the installed SDK to publish tiny owned temporary projects and verify shared inventory, manifests, cache repair and real MSBuild publish/opt-out behavior without hardware.
For installed Windows or Linux worker validation, set `LINEUP_TEST_WORKER_PACKAGE_ROOT` to a package tree before running `HdHomeRunWorkerProcessTests` or the `Worker_*` cases in `WebPlayerWorkerProcessTests`. These fixtures resolve hash-verified packaged workers instead of test-output assemblies and exercise local authenticated IPC, loopback HTTP and deterministic media cleanup. Explicit worker paths used by host-build tests take precedence. Leave the variable unset for ordinary source tests.

### Running the Web App

```bash
cd src/Apps/Lineup.App.Web
dotnet run
```

The app starts on `http://localhost:8080`.

## How to Contribute

### Reporting Bugs

- Use the [Bug Report](https://github.com/am385/lineup/issues/new?template=bug_report.md) issue template
- Include steps to reproduce, expected vs actual behavior, and your environment details
- Check existing issues first to avoid duplicates

### Suggesting Features

- Use the [Feature Request](https://github.com/am385/lineup/issues/new?template=feature_request.md) issue template
- Describe the use case and expected behavior

### Submitting Pull Requests

1. **Fork** the repository and create a branch from `main`
2. **Name your branch** descriptively (e.g., `fix/epg-parsing-bug`, `feature/new-tuner-support`)
3. **Make your changes** — follow the coding standards below
4. **Add or update tests** for any changed functionality
5. **Ensure all tests pass** (`dotnet test`)
6. **Ensure the build succeeds** (`dotnet build`)
7. **Submit a pull request** against `main`

### Coding Standards

- Follow all conventions defined in the root `.editorconfig`.
- Use file-scoped namespaces.
- Maintain nullable reference type safety; nullable reference types are enabled repository-wide.
- Prefer `var` only when the type is apparent from the right-hand side.
- Name private fields using `_camelCase`.
- Use PascalCase for types, methods, properties, and public members.
- Keep methods focused, cohesive, and small enough to understand without unrelated context.
- Reuse existing helpers and patterns instead of duplicating logic.
- Surface failures explicitly. Do not use broad catches, silent fallbacks, or success-shaped error results.
- Preserve type safety and avoid unnecessary casts such as `as any` or `as unknown as`.
- Do not disable analyzers, warnings, tests, or coding-standard checks to make a change pass.

#### Tests

- Add or update focused regression tests for every behavior change and bug fix.
- Every test must contain explicit `// Arrange`, `// Act`, and `// Assert` sections.
- Test the requested outcome directly, including meaningful failure and edge cases.
- Keep tests deterministic and independent of execution order.
- The Web UI's `Dashboard_RemainingDurationFormat` and `Dashboard_ChannelGapDurationFormat` resources are executable .NET `TimeSpan` formats, not ordinary translated text. Preserve `d`, `h`, and `m` numeric tokens and quote literal units, for example `h' hrs 'm' min'`. `UiLocalizationTests` exercises both formats with zero, hourly, and multi-day durations in every supported culture.
- In bUnit tests, await `ClickAsync(new())` when the click handler performs asynchronous work before asserting completion. Use `Click()` only for synchronous handlers.
- When testing an in-flight operation, retain the `ClickAsync` task, assert the pending state, release any controlled dependency, and await the task before disposing the component.

#### Formatting

- Run `dotnet format Lineup.slnx --verify-no-changes --no-restore` before submitting code.
- Prefer unwrapped method and constructor declarations. Keep the complete declaration, including all parameters and constraints, on one line whenever it fits within the 225-character limit.
- Wrap a method or constructor declaration only when its complete unwrapped form would exceed 225 characters. When wrapping is required, place one parameter per line and keep the closing parenthesis on its own line.
- Keep every C# line within 225 characters.
- Avoid unrelated formatting changes.

```csharp
public static WatchTrackSelection SelectTracks(MediaProbeResult source, int? audioIndex, int? subtitleIndex, SubtitlePresentation? subtitlePresentation = null)

public StreamController(
    ILogger<StreamController> logger,
    IAppSettingsService settingsService,
    IActiveStreamRegistry activeStreamRegistry)
```

#### Documentation

- Every non-private entity must have an XML documentation comment.
- Keep documentation synchronized with behavior and public API changes.
- Write comments only when they clarify non-obvious intent; do not narrate self-explanatory code.

#### Error Handling and Logging

- Validate external input at the boundary and return repository-standard errors.
- Catch only exceptions that can be handled meaningfully; preserve cancellation behavior.
- Log at the established level: Trace for high-volume stream traffic, Debug for routine API details, Information for meaningful operations, Warning for actionable failures, and Error for server failures or unhandled exceptions.
- Do not log credentials, request bodies, authorization data, or other sensitive values.

#### Required Validation

Run the smallest relevant tests while developing. Before submitting any code change, run the complete validation gate:

```powershell
dotnet build Lineup.slnx -c Release --no-restore
dotnet test Lineup.slnx -c Release --no-build --no-restore
dotnet format Lineup.slnx --verify-no-changes --no-restore
git diff --check
```

Restore dependencies first only when they are missing or dependency manifests changed. Documentation-only changes do not require a build or test run unless they affect generated documentation or validation tooling.

### Commit Messages

- Use clear, descriptive commit messages
- Start with a verb in imperative mood (e.g., "Add", "Fix", "Update")
- Reference issue numbers where applicable (e.g., "Fix EPG parsing for edge case (#42)")

## Project Structure

Production and test projects are grouped by dependency role:

- `src/Apps` — executable Web and TUI hosts.
- `src/Core` — the dependency-free shared model, stable plugin API, and provider-neutral application and streaming foundations.
- `src/Plugins` — independently composable adapters and their support libraries, grouped by provider or product family (`SiliconDust`, `LineupLink`, and `WebPlayer`).
- `tests` — a hierarchy that mirrors the production project roles.

Project directories, `.csproj` filenames, root namespaces, and assembly names must agree. Use `Lineup.App.*` for application hosts, `Lineup.Core` or `Lineup.Core.*` for foundations, and `Lineup.Plugin.*` for plugin families and their support libraries.
Use `Lineup.Integration.*` for integration harnesses, `Lineup.TestSupport.*` for test helpers, and `Lineup.Tools.*` for build tools. Automated test projects use their subject project's name followed by `.Tests`, with the same root namespace.

`Lineup.Core.Common` has no project or package dependencies and contains only shared data types. `Lineup.Core.Extensibility` references only `Lineup.Core.Common` and contains plugin/runtime interfaces and boundary validation. Production plugin projects may reference it, the provider-neutral `Lineup.Core.Streaming.FFmpeg` foundation when media processing is required, and designated support libraries in the same provider family, but must not reference `Lineup.Core`.
Provider family folders are organizational only; every adapter and support library remains an independently referenced assembly. Support libraries may not depend on adapters or projects from another plugin family.

| Project | Description |
|---|---|
| `Lineup.Core.Common` | Dependency-free identifiers, configuration records, normalized guide models, streaming DTOs, and serialized metadata types |
| `Lineup.Core.Extensibility` | Plugin/runtime interfaces, portable validation, and narrow host capabilities built on `Lineup.Core.Common` |
| `Lineup.Plugin.SiliconDust.HDHomeRun.Device` | Local device communication — discovery, channel lineup, native binary protocol |
| `Lineup.Plugin.SiliconDust.HDHomeRun.Api` | Remote API client — fetches EPG data from `api.hdhomerun.com` |
| `Lineup.Core` | Provider-neutral business logic — orchestration, topology, guides, and storage |
| `Lineup.Core.Streaming.FFmpeg` | Shared FFmpeg and FFprobe streaming transforms |
| `Lineup.Core.VirtualOutputs.Protocol` | Versioned local IPC contracts and framing shared by the trusted host and workers |
| `Lineup.Core.VirtualOutputs.Hosting` | Worker-side bootstrap, broker client, and lease handling |
| `Lineup.Core.VirtualOutputs.Runtime` | Trusted package validation, process supervision, IPC server, and capability broker |
| `Lineup.Plugin.GuideOutputs.Json` | Public versioned JSON guide document and serialization/deserialization API |
| `Lineup.Plugin.GuideOutputs.Json.Plugin` | JSON Guide Output driver, publisher adapter, settings, and host registration |
| `Lineup.Plugin.GuideOutputs.Xmltv` | Public serializer for normalized Lineup guide snapshots to XMLTV |
| `Lineup.Plugin.GuideOutputs.Xmltv.Plugin` | XMLTV Guide Output driver, global-output normalization, publisher adapter, and host registration |
| `Lineup.Plugin.SiliconDust.HDHomeRun.PhysicalTuner` | Physical HDHomeRun plugin |
| `Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner` | Virtual HDHomeRun output plugin |
| `Lineup.Plugin.SiliconDust.HDHomeRun.VirtualOutputHost` | Isolated virtual HDHomeRun listener executable |
| `Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryRuntime` | Trusted shared-discovery snapshot reconciler |
| `Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryHost` | Isolated shared UDP and SSDP discovery executable |
| `Lineup.Plugin.WebPlayer` | Built-in Web Player contracts, compatibility policy, and shared output lifecycle |
| `Lineup.Plugin.WebPlayer.FFmpeg` | FFmpeg CMAF planning, manifest processing, and browser compatibility-test generation |
| `Lineup.Plugin.WebPlayer.VirtualOutputHost` | Isolated CMAF/HLS/DASH media executable |
| `Lineup.Plugin.SiliconDust.GuideSource` | SiliconDust guide-source plugin |
| `Lineup.Plugin.LineupLink` | Cohesive remote Lineup physical, virtual, guide, and protocol plugin |
| `Lineup.Plugin.LineupLink.VirtualOutputHost` | Isolated Lineup Link listener executable |
| `Lineup.App.Web` | Blazor Server web app — dashboard, EPG viewer, settings, live TV |
| `Lineup.App.Tui` | Terminal UI — interactive menu using Spectre.Console |
| `Lineup.Integration.Streaming.Sweep` | Opt-in six-matrix Docker/HDHomeRun and existing-instance streaming harness (not a solution test entry point) |
| `Lineup.Integration.Streaming.Sweep.Tests` | Non-live deterministic sweep regression tests in the normal solution gate |

## License

By contributing, you agree that your contributions will be licensed under the [GNU General Public License v3.0](LICENSE).
