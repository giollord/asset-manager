# Proposal

## Why

While an import runs, the Jobs page looks stuck: the elapsed time only moves when the current file name changes, so a long archive extraction or a slow step shows a frozen clock. The scan stage also says too little. For a file inside an archive it shows only the path inside that archive, so you cannot tell which archive is being read, and the stage has no count of what was found so far. The progress bar is indeterminate and no done/total is shown, because the scan does not know its total.

## What Changes

- The elapsed time and the time left for every running job tick every second on their own, wherever job progress is shown (Jobs page, app-bar quick view, import wizard). They no longer wait for the job to report progress or for other page data, such as the AI queue status, to load.
- While the scan is inside an archive or package, the job shows the container being read, with its full chain for nested containers (`Packs.zip → Forest.unitypackage`), next to the current file. When an archive is opened, its name shows straight away, before its contents are extracted, so a long extraction is visible.
- During the scan, the job shows running counts of what was found: files found and importable files (files of a supported asset type). These counts stay visible after the scan, through the rest of the import. When grouping finishes, the number of assets (files grouped with their companions) is added. Assumption: files and assets cannot be told apart reliably until grouping, so the scan counts files, and the asset count comes after grouping.
- Jobs get a general way to report a "current container" and a few labeled live counts. Import uses it first; other jobs can use it later.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `job-progress`: the "Progress for every long-running operation" requirement gains a self-updating clock and an optional current container and live counts. The "Multi-stage import progress" requirement gains the scan detail (archive name, files found, importable files) and the asset count after grouping.

## Impact

- `AssetManager.Core/Jobs/JobAbstractions.cs`: `IJobProgress` gets methods to set the current container and the live counts, and `JobSnapshot` gets matching properties, added as non-positional `init` properties so existing constructor calls still compile.
- `AssetManager.Data/Jobs/JobProgress.cs`: stores the new live fields in memory and puts them in published snapshots. No database migration: the fields are not persisted. An interrupted import restarts its scan anyway.
- `AssetManager.Processing/Containers/VirtualFileSystem.cs`: `WalkAsync` gets an optional callback that runs before a container is opened.
- `AssetManager.Processing/Import/ImportStager.cs`: reports the container and the counts during the scan, and the asset count after grouping.
- `AssetManager.Shared/Components/JobProgressView.razor`: runs its own one-second tick while its job is running, and shows the container line and the counts.
- `AssetManager.Shared/Pages/Jobs.razor`: the one-second refresh no longer waits for the AI queue status query before it re-renders.
- Tests: `AssetManager.Tests/Ui/JobUiTests.cs`, `AssetManager.Tests/Import/ImportStagingTests.cs`, VFS tests.
