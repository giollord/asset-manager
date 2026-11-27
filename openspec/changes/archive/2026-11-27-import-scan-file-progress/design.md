# Design

## Context

See proposal.md for why this change is needed. The relevant code today:

- `FolderReader.Walk(folder, ignore)` enumerates the whole folder tree into a list, sorts it, and only then returns it. `VirtualFileSystem.WalkAsync` calls it once per folder input before it yields the first node. The full top-level listing therefore already exists before any file is read. It is simply not exposed.
- `ImportStager.ScanAndGroupAsync` calls `SetStage("Scanning", 0, stageCount)` with no total. It walks the inputs one after another and calls `progress.Advance(1, node.RelativePath)` for every `File` node, including files inside containers. `Done` grows, but with `Total == 0` the view shows no done/total, the bar is indeterminate, and `JobSnapshot.Fraction` counts the scan as 0.
- `JobSnapshot.Fraction = (StageIndex + StageFraction) / StageCount`, and `JobProgressView` shows `Done / Total (P0)` whenever `Total > 0`. Both the UI and the overall percentage pick up a real scan total with no change.
- What the walk yields at the top level (`Location.IsInContainer == false`): `File`, `Container`, `ContainerError`, `IgnoredFolder` (ignored folders, and archives kept closed by a rule), and `File` with `DepthLimited`. `Wanted()` filters by `OnlyPaths` on top-level nodes. Inner nodes of a container follow its `Container` node.
- The earlier change import-scan-progress-detail listed "a real total for the scan" as a non-goal because it would need a pre-walk. Getting the total from the listing the walk already makes removes that cost.

## Goals / Non-Goals

**Goals:**
- The scan total is known before the first file is processed, with no second directory enumeration.
- The total covers exactly the top-level items the scan will process. Done reaches the total when the scan ends.

**Non-Goals:**
- Progress inside an archive, for example a fraction from the archive's entry count. The done count stays still while a large archive is read. The current container and current file lines show the activity in that time.
- Changes to the other stages, to `JobProgress`, `JobSnapshot` or the UI components.
- A unit label such as "files" on the done/total line. The stage line keeps its current format.

## Decisions

**D1. The folder listing is a separate step that the walk can reuse.** `VirtualFileSystem` gets a `List(folderPath, ignore)` method. It returns a `FolderListing`, which holds the result of `FolderReader.Walk` and its folder. It also has a `WalkAsync` overload, or an optional parameter, that takes a `FolderListing` instead of listing the folder again. The existing `WalkAsync(localPath, …)` builds the listing itself and delegates, so other callers do not change.
`FolderListing` exposes the top-level entries that the walk will treat as files: entries with a `File`, minus archives that an ignore rule keeps closed. It uses the same `ReaderFor` + `ignore.Match` check as the walk. Each entry carries its relative path, so the stager can apply its `OnlyPaths` filter.
*Alternative:* an `Action<int> listed` callback from inside `WalkAsync`. It is simpler, but the total of a second input would only be known when that input starts, so the percentage would go backwards between inputs.
*Alternative:* a separate counting pass with `Directory.EnumerateFiles`. That doubles the directory enumeration, which is slow on the external drives this app targets.

**D2. The stager lists every input before scanning.** At the start of the scan stage, `ScanAndGroupAsync` sets the current item to "Listing files...". It then builds the listings for all folder inputs and sums the total:
- each listing's file entries that pass `Wanted` (the `OnlyPaths` key check for top-level files),
- plus 1 for each single-file input that exists.

It calls `SetStage("Scanning", StageScan, stageCount)` before listing, so the stage shows while listing runs. After listing, it calls `SetTotal(total)` before the first advance, then walks each folder input from its listing. Inputs that are not found are skipped and are not counted. Listing all folder inputs first means their listings are held in memory together. That is already true today for one input at a time, and multi-folder imports are rare.

**D3. Done counts finished top-level items.** The stager keeps a flag for "a top-level item is in progress". When the walk yields a top-level node of kind `File`, `Container` or `ContainerError` that passes `Wanted`, it first advances by 1 for the previous top-level item, if there is one, and then marks the new one as in progress. When an input's walk ends, it advances for the last item. So:
- a plain file counts as done when the next item starts, which is almost at once;
- an archive or package counts as done after all its contents have been yielded and handled.

Top-level `IgnoredFolder` nodes do not count, because they are not in the total either.

An archive's `Container` node arrives only after the archive has been extracted. A top-level archive therefore also starts in the walk's `opening` callback. The item is tracked by its location, so the later `Container` node does not count it twice. Without this, the file before the archive would stay "not done" for the whole extraction. The `opening` callback also clears the current item. Otherwise the last file of the previous archive would show under the new archive's name. This was found while checking in the app.
*Alternative:* advancing when a top-level item starts. That would show a 4 GB archive as done before it is extracted.

**D4. Inner files update the current item but do not advance.** For every `File` node, the scan calls `SetCurrentItem(node.RelativePath)`, after `SetContainer(...)` as today, instead of `Advance(1, node.RelativePath)`. The "files found" and "importable" counts do not change. The ETA comes from `JobProgress`'s per-item moving average over top-level advances.

**D5. A guard against drift.** At the end of the scan, if done is still short of the total, for example because an entry vanished between listing and walking, the stager advances by the difference so that the stage ends at 100%. A listed file deleted before the walk reaches it is simply missing from the walk. The listing itself is what the walk iterates, so no extra item can appear.

## Risks / Trade-offs

- [The done count stalls on a big archive] → Accepted (Non-Goals). The container line and the current file show that work is going on.
- [The ETA is uneven when items vary in cost, plain files against large archives] → The moving average smooths it. The ETA was absent before, so this is still an improvement.
- [Many folder inputs are listed in memory together] → Each listing holds only paths and `FileInfo`s, which is the same data the walk holds today for one input.
- [A listing step before the first progress event on a huge folder] → This is no slower than today, where the same listing runs inside `WalkAsync` before the first node. "Listing files..." makes the step visible.
