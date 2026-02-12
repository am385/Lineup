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

- Follow the conventions defined in `.editorconfig`
- Use file-scoped namespaces
- Use nullable reference types (`<Nullable>enable</Nullable>`)
- Prefer `var` only when the type is apparent from the right-hand side
- Private fields should use `_camelCase` naming
- Keep methods focused and small
- Add XML documentation for public APIs

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
