# Lineup.App.Tui

The terminal presentation host, source configuration and topology validation. Source-development commands run from the repository root.

- [Running the TUI](#running-the-tui)

## Running the TUI

```bash
cd src/Apps/Lineup.App.Tui
dotnet run
```

Configure the TUI via environment variables:

- `Lineup__TuiAppDataPath` — persistent TUI data directory; defaults to `.lineup/tui` beneath the working directory
- `Lineup__DatabasePath` — path to the authoritative SQLite channel and guide database

The TUI stores topology and guide-retention settings in `settings.json` beneath its app-data directory; it does not read a standalone `streaming.json`.

Guide rows and export paths render as literal terminal text, including brackets and markup-looking provider values; application-authored table and status styling is preserved.

On first launch, use **Configure Sources** to add physical devices, guide sources, and virtual outputs before explicitly refreshing channels or guide data.
Missing settings start with defaults and no physical inputs; incompatible or unreadable persisted settings enter the explicit factory-reset recovery flow, with valid-backup recovery when available.
Web and TUI honor the same configured guide-history retention policy. The TUI retains manual channel/guide refresh commands; it does not automatically enable the Web host's periodic refresh workers.

The TUI validates the complete topology before saving hosting changes. Conflicting listener bindings or invalid security and worker settings are reported without changing live settings, the settings file, or its backup. If saved topology is already invalid, the TUI shows the validation errors and offers **Configure Sources** and **Exit** instead of applying it to physical devices and guide sources. Correct the errors and return from configuration to resume the normal menu.

TUI hosting edits preserve the existing listener's client-certificate mode, forwarded-header setting, and trusted proxies, which the terminal editor does not expose. Editing a port or other hosting field does not remove these requirements. Switching to HTTP is rejected if the preserved policy requires or accepts client certificates; change that policy explicitly before selecting HTTP.

## Related documentation

[Project overview](../../../README.md) | [Contributor standards](../../../CONTRIBUTING.md)
