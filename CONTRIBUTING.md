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

### Running Tests

```bash
dotnet test
```

### Running the Web App

```bash
cd src/Lineup.Web
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

| Project | Description |
|---|---|
| `Lineup.HDHomeRun.Device` | Local device communication — discovery, channel lineup, native binary protocol |
| `Lineup.HDHomeRun.Api` | Remote API client — fetches EPG data from `api.hdhomerun.com` |
| `Lineup.Core` | Core business logic — orchestration, XMLTV conversion, caching |
| `Lineup.Web` | Blazor Server web app — dashboard, EPG viewer, settings, live TV |
| `Lineup.Tui` | Terminal UI — interactive menu using Spectre.Console |

## License

By contributing, you agree that your contributions will be licensed under the [GNU General Public License v3.0](LICENSE).
