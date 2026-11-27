# Tasks

## 1. Reusable folder listing in the VFS

- [x] 1.1 Add `FolderListing` and `VirtualFileSystem.List(folderPath, ignore)` (design D1). `FolderListing` wraps the `FolderReader.Walk` result and exposes the top-level file entries the walk will treat as files: entries with a `File`, minus archives kept closed by an ignore rule, each with its relative path. Verify with a test in `UnityPackageAndVfsTests.cs`: for a folder with plain files, a zip, an ignored folder and a zip matched by an ignore rule, the listing reports the plain files and the open zip only.
- [x] 1.2 Let `WalkAsync` take a `FolderListing` (overload or optional parameter) and walk it without listing the folder again. Make the existing path-based `WalkAsync` build the listing and delegate. Verify that the existing VFS and container reader tests pass unchanged, and add a test showing that walking from a listing yields the same nodes as walking the path.

## 2. Scan done/total in the import stager

- [x] 2.1 In `ImportStager.ScanAndGroupAsync`, list every folder input before scanning, with the current item "Listing files...". Sum the total from the listings' entries that pass `Wanted`, plus 1 for each existing single-file input, and set it with `SetTotal` before the first advance. Walk each folder from its listing (design D2). Verify with an `ImportStagingTests` test that the scan stage's total is set before the first advance, for a folder with plain files, a zip and an ignored folder.
- [x] 2.2 Advance once per finished top-level item (`File`, `Container` or `ContainerError` at the top level, passing `Wanted`): advance for the previous item when the next one starts, and for the last one when an input's walk ends. Use `SetCurrentItem` instead of `Advance` for every file node, inner files included, and keep the "files found"/"importable" counts as they are (design D3, D4). Add the end-of-scan top-up to the total (design D5). Verify with a test that done equals the total at the end of the scan, that no advance happens while the zip's inner files are scanned, and that the zip's advance comes after its last inner file.
- [x] 2.3 Update `RecordingProgress` in `ImportStagingTests.cs` to record `SetStage` totals, `SetCurrentItem` with the container in effect, and advance counts. Adapt `Scan_reports_the_archive_and_what_it_found` so that `Sfx/beep.wav` is checked through the current item, not through an advance. Verify that the test passes with the same container and count assertions as before.
- [x] 2.4 Add tests for the other totals: a rescan with `OnlyPaths` (the total is the added files only, and an added archive counts as 1), two folder inputs (the total covers both from the first total, and done never decreases), and a single-file archive input (total 1). Verify that the tests pass.

## 3. Integration check

- [x] 3.1 Add a `JobsPageTests` or `JobUiTests` case: a running import snapshot in the scan stage with `Done = 600`, `Total = 2000` and `StageCount = 6` shows "600 / 2,000 (30%)", a determinate bar, and an overall percentage of 5% in the state chip. Verify that the test passes.
- [x] 3.2 Run the full test suite (`dotnet test`) and verify that it passes. Then run an import of a folder that holds archives in the app (see the MAUI app debugging notes). Verify that the scan shows a growing done/total, that the count holds while an archive is read, and that the overall percentage moves during the scan.

## Workflow follow-up

- Sync the `job-progress` delta into `openspec/specs/job-progress/spec.md` and archive the change after review.
