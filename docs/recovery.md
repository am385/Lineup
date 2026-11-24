# Lineup reset, backup and recovery

The Lineup 3.0 reset boundary and operator-controlled offline configuration export and recovery.

- [Lineup 3.0 reset boundary](#lineup-30-reset-boundary)
- [Offline configuration export and recovery](#offline-configuration-export-and-recovery)

## Lineup 3.0 reset boundary

Lineup 3.0 is an intentional configuration and storage reset. It does not migrate persisted state created by earlier releases. On startup, Lineup requires the exact application-settings, streaming-topology, database, and Web-settings schema versions used by the running release; an older, newer, invalid, or unversioned artifact enters the explicit factory-reset-required state.

The Web host remains available in this state so you can review recovery information and explicitly request Factory Reset from Settings.
Streaming, discovery and guide-fetch workers remain disabled. Dashboard resource monitoring does not require the disabled virtual-output runtime.

Current schema numbers alone do not establish valid settings: the complete document must deserialize and pass the settings loader's validation. A valid settings backup is recovered without changing either file until the next save. Without a valid primary or backup, recovery blocks settings writes and physical-topology publication so saved channels and guide bindings remain intact. Defaults become active only for a new installation with no settings documents. If an explicit reload fails after startup, previously loaded settings remain in memory, but writes and physical-topology publication stay blocked until the application restarts and validates its persisted settings again.

Required streaming profiles, topology collections, nested configuration objects, and collection entries cannot be explicitly null. Both application settings and standalone streaming documents reject these before readiness; omitted defaultable fields and intentionally nullable options remain supported. This structural check does not require connected hardware or turn startup into a full semantic topology validation.

Record the device addresses, guide choices, output subscriptions, and deployment-specific networking values you intend to recreate before replacing an earlier installation. A Factory Reset removes Lineup-owned settings, guide data, logs, Data Protection keys, and transient stream files, then returns the application to first-run setup. Its durable request remains until all cleanup succeeds; a failed or interrupted cleanup stays pending and is retried on the next startup.

## Offline configuration export and recovery

Both Web and TUI executables accept `--recovery backup|export|preview|preflight|restore <path>` **before** normal application startup.
Stop all Lineup processes and supervisors using the data directory first. Use the exact same release/build for backup and restore, and configure the data root
through `Lineup__AppDataPath` (Web) or `Lineup__TuiAppDataPath` (TUI). Recovery does not support a TUI database override outside that root.
The updated hosts and recovery commands retain a sibling `.NAME.lineup.lock` file to exclude concurrent owners.
The parent directory must be writable and paths must not use symbolic links/reparse points. The lock is outside factory-reset-owned contents;
do not remove it while a process owns it. Older versions, other containers/hosts with different filesystem views, and external database tools are not coordinated;
all writers must be stopped.

For example, from a stopped Windows Web installation (use `Lineup.App.Tui.dll` and its data-root variable for the TUI):

```powershell
$env:Lineup__AppDataPath = 'E:\Lineup\data'
dotnet Lineup.App.Web.dll --recovery export 'E:\Backups\lineup-portable.json'
dotnet Lineup.App.Web.dll --recovery backup 'E:\Backups\lineup-sensitive.zip'
dotnet Lineup.App.Web.dll --recovery preview 'E:\Backups\lineup-sensitive.zip'
$env:Lineup__AppDataPath = 'E:\Lineup\restored'
dotnet Lineup.App.Web.dll --recovery preflight 'E:\Backups\lineup-sensitive.zip'
dotnet Lineup.App.Web.dll --recovery restore 'E:\Backups\lineup-sensitive.zip'
```

Create the destination's parent directory first. Existing export/backup files are never overwritten.
Portable export is an allowlisted configuration starter: it retains topology identities, subscriptions and media preferences but clears all opaque adapter payloads,
hosting/security policies and grants, disables adapters, and omits keys, database contents, proxy addresses and deployment-specific destinations.
It is **not** a recovery archive or an automatic import workflow. Recreate adapter connections/authorization and review enabled outputs through normal setup;
operator-assigned names and topology identifiers remain present, so it is not an anonymized support bundle.

Sensitive recovery archives include primary Core/Web settings, saved adapter credentials/grants, SQLite channel identities/bindings/guide data,
and Data Protection XML keys. They are **not encrypted**: store them with operator-only access and encrypt/protect transport and storage outside Lineup.
Unix archive/staging files are owner-only; Windows uses inherited filesystem ACLs, so select a private directory.
Logs, settings fallback files, reset markers, transient media, generated guide files, external certificates and environment/platform secrets are excluded.
Certificate references remain in sensitive settings; provision external certificate files/passwords and host configuration separately.
Platform-protected Data Protection keys can still require the original OS account/machine; the archive cannot defeat external key encryption.
Save artifacts outside application-data, transient and generated-output/reset-owned directories, including their parent directories;
the command rejects paths inside its own app-data root, but cannot infer every externally managed cleanup location.

Backup uses SQLite's online backup API (including committed WAL data), not a live database-file copy; offline ownership keeps settings and database stable together.
Preview validates the entire archive's release, inventory, SHA-256 contents, paths, expansion limits, schemas and SQLite integrity before returning file names.
Hashes detect corruption, not authenticity: restore only trusted archives. The supported limit is two GiB of expanded data and 1,024 entries.
Incomplete writes use unique `.pending` names and are published only after closing/flushing the complete archive.

Restore validates in a private sibling staging directory and installs it by directory rename only into an absent or empty target.
It never merges with or replaces populated state; a failed preview/restore does not install partial application files.
Restore into a **new directory**, then point the stopped deployment at it after review. Mounted volume roots cannot be replaced atomically:
for containers, restore into a new subdirectory such as `/appdata/restored` and update `Lineup__AppDataPath` accordingly.
Do not delete the old deployment or perform Factory Reset until recovery has been verified.
The existing exact-schema/reset boundary remains unchanged; no Lineup 2.0 migration is introduced.

Preflight runs the same complete archive validation and verifies that the selected target is absent or empty, without installing files there.
It uses and removes private sibling staging, retains the ownership lock file, and requires a writable parent with enough space for the expanded archive.
Its JSON reports the exact producing informational release string and file inventory, plus **unverified** external deployment requirements.
Exit 0 means these archive/target checks passed, not that certificates, OS-protected keys, environment, disk capacity at a later time, or network outputs are ready.
Invalid archives, nonempty targets and ownership failures return exit 2. Cancellation is not reported as success.
Preflight is not a reservation or authorization to replace existing data: restore reacquires ownership and repeats validation and the empty-target checks.

For a container deployment, stop the existing container and its automatic restart policy first. Pin the **same producing image/build**, not a moving tag.
For example, from PowerShell, replace the image digest and the private host directories before running:

```powershell
$image = 'ghcr.io/am385/lineup@sha256:<exact-producing-image-digest>'
docker run --rm --network none --mount 'type=bind,source=E:\Lineup\data,target=/appdata' --mount 'type=bind,source=E:\Backups,target=/backups,readonly' `
  -e Lineup__AppDataPath=/appdata/restored $image --recovery preflight /backups/lineup-sensitive.zip
docker run --rm --network none --mount 'type=bind,source=E:\Lineup\data,target=/appdata' --mount 'type=bind,source=E:\Backups,target=/backups,readonly' `
  -e Lineup__AppDataPath=/appdata/restored $image --recovery restore /backups/lineup-sensitive.zip
```

After either Windows or container restore, review private ACLs/volume ownership, restore external certificates/passwords and deployment configuration,
and configure the stopped deployment to use the new root (`/appdata/restored` in this example). Start only that deployment and verify setup, source connectivity,
channel identities/bindings, guide coverage/publications and each enabled output through normal controls and `/api/v1/status`.
Check management liveness/readiness separately from operational conditions. Keep the original root and protected backup until that verification succeeds;
rollback means stopping the restored deployment before pointing the original producing build back to the untouched original root.

## Related documentation

[Project overview](../README.md) | [Contributor standards](../CONTRIBUTING.md)
