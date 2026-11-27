# Proposal

## Why

During the import scan the job shows no done/total and the progress bar is indeterminate, so the user cannot tell how far the scan has got. The scan stage also adds nothing to the overall percentage, which stays at 0% for the whole scan even when it takes most of the import time on a slow drive. The folder listing that the scan already makes knows how many files the inputs hold before the first file is read, so a real total is available at no extra disk cost.

## What Changes

- The import scan gets a real done/total: the number of files processed out of the files directly in the input folders, meaning files that are not inside an archive or package. An archive or package counts as one file, and it is counted as done once all of its contents have been scanned.
- The scan's done/total drives its stage percentage, the progress bar and the overall import percentage, which now moves during the scan instead of staying at 0%.
- Files inside archives and packages no longer move done/total. They are still shown as the current file, under the current container, and still go into the "files found" and "importable" counts.
- The scan's time left is estimated from the same done/total.
- Files the scan never processes are left out of the total: files in ignored folders, archives kept closed by an ignore rule, and, for a rescan that imports only the added files, the files that were not added.
- Several inputs: the total covers all of them from the start, so the percentage never goes backwards.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `job-progress`: adds the requirement "Import scan counts files processed out of total". The import scan reports processed out of total top-level files, and this feeds the stage and overall percentage. Existing requirements, including "Import shows what it found so far", are unchanged.

## Impact

- `AssetManager.Processing/Containers/VirtualFileSystem.cs`: the folder listing that `FolderReader.Walk` already builds must be available to the caller before the walk starts, with no second directory enumeration.
- `AssetManager.Processing/Import/ImportStager.cs` (`ScanAndGroupAsync`): sets the scan total up front and advances once per finished top-level file instead of once per file node.
- `AssetManager.Shared/Components/JobProgressView.razor`: no change expected. It already shows done/total and percentage when `Total > 0`, and `JobSnapshot.Fraction` already combines the stage fraction into the overall percentage.
- Tests: `AssetManager.Tests/Import/ImportStagingTests.cs` (the recording progress counts scan advances per file node today) and VFS tests.
- No database, settings or API changes.
