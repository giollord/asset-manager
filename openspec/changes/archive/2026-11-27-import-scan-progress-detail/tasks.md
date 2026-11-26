# Tasks

## 1. Job clock ticks on its own

- [x] 1.1 In `JobProgressView.razor`, start a one-second timer while `Job.State == Running` that calls `InvokeAsync(StateHasChanged)`. Stop it when the job is no longer running, and dispose it with the component (implement `IDisposable` and re-check in `OnParametersSet`). Verify with a bUnit test in `JobUiTests` that the `.job-times` elapsed text advances without a new `Job` parameter. Use a short wait, or make the tick interval injectable for the test.
- [x] 1.2 In `JobsIndicator.razor`, remove the popover tick (`SetOpen` timer), which is now redundant. Verify that the existing `JobsIndicator` tests still pass.
- [x] 1.3 In `Jobs.razor`, change the one-second tick to call `StateHasChanged()` first, then start `RefreshAiAsync` without awaiting it. Use an in-flight guard so refreshes never overlap, and re-render when the refresh completes. Verify with a bUnit test where the AI status call is held by a blocked fake or a `TaskCompletionSource`, and the job's elapsed text still updates.

## 2. Trailing publish in JobProgress

- [x] 2.1 In `JobProgress.Update`, when a publish is throttled, arm a single pending one-shot timer through `_time` that publishes the latest snapshot after the 100 ms window. Skip the publish if the job has finished. Verify with a `JobQueueTests` test: two updates within 100 ms, then no more updates, and the hub's latest snapshot has the second update's current item. Extend `ManualTime` to support `CreateTimer` if needed.

## 3. Current container and live counts in the progress model

- [x] 3.1 In `JobAbstractions.cs`, add `record JobCount(string Label, long Value)`. Add to `JobSnapshot` the `init` properties `Container` (string?) and `Counts` (`IReadOnlyList<JobCount>`, default empty). Add to `IJobProgress` the methods `SetContainer(string?)` and `SetCount(string label, long value)`. Verify that the solution builds and all existing `new JobSnapshot(...)` calls compile unchanged.
- [x] 3.2 Implement both methods in `JobProgress`. Keep them in private fields, with an insert-or-update list that keeps the order of first insertion. Add them to `Snapshot` and to published snapshots through `with`. Clear the container in `FinishAsync`, and do not persist either field. Verify with a unit test that the published snapshots carry the container and the counts, and that a count updated twice stays in place.
- [x] 3.3 In `JobProgressView.razor`, show the container line above the current-item line while the job is running (class `job-container`, ellipsis). Show the counts as `N0` values joined by ` · ` in a caption line (class `job-counts`), shown while the job is active. Verify with bUnit tests in `JobUiTests` for "Packs.zip → Forest.unitypackage" and "1,200 files found · 800 importable".

## 4. Import scan reports container and counts

- [x] 4.1 In `VirtualFileSystem.WalkAsync`, add an optional `Action<Location>? opening` parameter and pass it to `VisitAsync`. Call it right before `reader.OpenAsync`. Verify with a VFS test (in `UnityPackageAndVfsTests` or `ContainerReaderTests`) that walking a zip that holds a unitypackage calls the callback for the zip and then for the nested package, each before its entries are yielded.
- [x] 4.2 In `ImportStager.ScanAndGroupAsync`, add a `ContainerLabel(Location)` helper (outer file name plus inner entry file names, joined by ` → `). Pass `opening` to call `SetContainer` with the label. For each `File` node, set the container from `node.Location.Container`, or null for plain files. Keep counters and call `SetCount("files found", …)` for every file node and `SetCount("importable", …)` for supported asset files. Clear the container after the scan, and call `SetCount("assets", created.Count)` after grouping. Verify with an `ImportStagingTests` test, using a recording `IJobProgress` or the hub snapshots, on a folder with a plain file and a zip: the container label is seen during the zip's files, it is null for the plain file, and the final counts match the staged files and assets.
- [x] 4.3 When `ScanAndGroupAsync` returns early because the session is already staged past grouping (a resume), set "assets" from a count of the session's staged assets. Verify with a test that resumes a staged session and checks the "assets" count.

## 5. Integration check

- [x] 5.1 Run `dotnet test`, and check that all tests pass.
- [x] 5.2 Launch the Debug app (see `tools/cdp.mjs`) and import a folder that holds a large zip. On the Jobs page and in the import wizard, check that the elapsed time ticks every second during the extraction, that the archive name and the growing counts show, and that "assets" appears after grouping.
