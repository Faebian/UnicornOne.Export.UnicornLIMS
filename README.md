# UnicornOne.Export.UnicornLIMS

Standalone **UnicornLIMS incremental instance snapshot** plugin for UnicornOne. It externalises the legacy `UnicornOne.Platform.Data.Mirror.DataSnapshotService` behaviour as a system export plugin.

> **Important:** This is an **instrument-wide directory mirror**, not an experiment exporter and **not** the sample's Export/Snapshots tab. It is not a transactional SQLite backup solution.

## How it works

`UnicornLimsExportPlugin` implements `UnicornOne.Abstractions.Export.ISystemExportPlugin` using `SystemExportContract.Version`. The **host** invokes enabled system export plugins during startup and after its forced disk-flush operations; the plugin does not schedule its own timer.

The `SystemExportRequest` supplies the instance source and snapshot destination paths, typically:

```text
Source:      C:\UnicornOne\Data\<instance UUID>
Destination: C:\UnicornOne\Snapshot\<instance UUID>
```

The plugin recursively mirrors source files and writes a JSON manifest. Its existing behaviour includes:

- New files copied, changed files replaced; last-write UTC timestamps preserved.
- Unchanged size/timestamp plus a previous manifest entry allows reuse of a prior SHA-256 value.
- File statuses `new`, `replaced`, `unchanged`, `unchanged_verified`, and `orphaned`.
- Files missing from the current source are **not deleted** from the snapshot; they remain and are recorded as `orphaned`.
- `manifest.json` updated via a candidate file; `.snapshot_building` and `.snapshot_ready` marker files indicate snapshot progress/readiness.
- Temporary SQLite-related (`*.db-wal`, `*.db-shm`), `*.tmp` and `*.partial*` files are skipped, as implemented in the source.
- The plugin rejects source/destination directories that overlap, to prevent recursive mirroring.

The host wraps the operation in its `DataWriteGate.EnterSnapshot` mechanism; only writers that participate in that gate are coordinated.

## Host prerequisites

**Unlike the other two exporters**, this project requires a host supporting the **system export contract** (`ISystemExportPlugin`). The experiment-only `IExperimentExportPlugin` interface cannot provide instrument-wide export events and source/destination paths.

Install/rebuild the corresponding UnicornOne host integration first, with a matching `UnicornOne.Abstractions.dll` containing `ISystemExportPlugin` and a host that dispatches system exports.

Required installed assemblies:

- `C:\UnicornOne\UnicornOne.Abstractions.dll`
- `C:\UnicornOne\Newtonsoft.Json.dll`

Build requirements: Windows, Visual Studio 2022 MSBuild/Build Tools, **.NET Framework 4.7.2 Developer Pack**. The library targets **C# 7.3 / .NET Framework 4.7.2 / x64**. It references the installed DLLs directly and does not ship copies of them.

## Build and deploy

Open this folder in VS Code and press **Ctrl+Shift+B**, or run in PowerShell:

```powershell
.\build.ps1
.\build.ps1 -Deploy
```

The build output and deployment path are:

```text
bin\Release\UnicornOne.Export.UnicornLIMS.dll
C:\UnicornOne\Plugins\Export\UnicornOne.Export.UnicornLIMS.dll
```

## Enable in UnicornOne

Enable the DLL in **Config > Exports**, or merge the top-level `ExportPlugins` fragment below into the existing system configuration:

```json
{
  "ExportPlugins": {
    "UnicornOne.Export.UnicornLIMS.dll": { "Enabled": true }
  }
}
```

The legacy `Exports.UnicornLIMS.Enabled` switch is **not** the activation switch for this external system plugin. Restart UnicornOne after enabling it.

## Validation and data safety

1. Build/deploy the DLL only after verifying your host implements the system export contract.
2. Enable the plugin and restart UnicornOne. Confirm a snapshot is created at the configured instance destination.
3. Compare source and destination file inventories and inspect `manifest.json` for `new`/`unchanged`/`orphaned` entries.
4. Change a small, noncritical file in the source and trigger a host-driven snapshot; verify `replaced` is reported.
5. **Test restoration** of any SQLite database or other critical system data before treating this mirror as a recovery backup.

**SQLite consistency warning:** copying live database files while excluding WAL files does not guarantee a recoverable, transactionally consistent snapshot. `DataWriteGate` alone is not a substitute for SQLite's backup API or a database-aware snapshot. This plugin should not be your sole disaster-recovery strategy until restoration has been tested.

## Repository layout

```text
UnicornLimsExportPlugin.cs              Plugin and manifest models
UnicornOne.Export.UnicornLIMS.csproj    .NET Framework library project
UnicornOne.Export.UnicornLIMS.sln       Visual Studio solution
build.ps1                              Build and optional deployment
.vscode/tasks.json                    VS Code build tasks
```

The documentation update does not include a Windows compile or a live snapshot test. As with all in-process plugins, load only trusted DLLs.
