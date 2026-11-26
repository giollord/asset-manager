# Design

## Context

Where things stand now (see proposal.md for why they need to change):

- `JobProgressView` works out the elapsed time at render time (`Job.Elapsed(DateTime.UtcNow)`). It has no timer of its own, so the time only moves when its parent re-renders it:
  - The Jobs page re-renders on hub `Changed` events and on a one-second timer. That timer runs `await RefreshAiAsync(); StateHasChanged();`, and `AiQueue.StatusAsync` runs a `PendingCountAsync` database query. During an import the catalog database and the drive are busy, so the query can be slow, and each tick's re-render waits for it. In practice the clock then moves only when a hub event arrives, which is when the current file changes.
  - The import wizard has no tick at all. The app-bar quick view ticks only while its popover is open.
- `JobProgress.Update` publishes to the hub at most every 100 ms. It never sends a trailing publish: a change that arrives within 100 ms of the previous publish stays unpublished until the next change. One example is setting the current item just before a long archive extraction. The UI then keeps showing an older file name.
- Scan (`ImportStager`, stage "Scanning"): `Total` is 0, so the view shows no done/total, and the bar is indeterminate. `Advance(1, node.RelativePath)` passes the path relative to the container, so the archive is not visible. `VirtualFileSystem.VisitAsync` runs `reader.OpenAsync` before it yields the `Container` node, so nothing is reported while an archive is extracted.
- `JobSnapshot` is a positional record that is built from the `Job` entity. Every snapshot field is persisted to the `Jobs` table.

## Goals / Non-Goals

**Goals:**
- A job's clock moves on its own, without waiting for job events or page data.
- A general way for any job to report a current container and a few labeled live counts. Import is the first job to use it.
- The latest progress state always reaches the UI, even after a burst of updates.

**Non-Goals:**
- A real total or percentage for the scan stage. That would need a pre-walk of the inputs, which doubles the disk work on slow drives.
- Saving the container or the counts to the database, or showing them in job history.
- Changes to how other job types report progress, apart from the clock fix, which applies to all of them.

## Decisions

**D1. `JobProgressView` runs its own one-second tick while its job is `Running`.** The timer starts when the component sees a running job and is disposed when the job stops running or the component is removed. On each tick it calls `InvokeAsync(StateHasChanged)`, with no I/O. Because the tick lives in the component, the clock works the same on the Jobs page, in the import wizard, in the quick view and in `SyncResults`. With this in place, `JobsIndicator`'s own popover tick can be removed.
*Alternative:* fixing only the Jobs page timer. That would leave the wizard with the same problem, and every new host would need its own tick.

**D2. The Jobs page's tick no longer waits for the AI status.** The page now re-renders first. It then starts the AI status refresh in the background, with a guard so that only one refresh runs at a time, and re-renders again when the refresh finishes. A slow count query therefore delays only the AI panel.

**D3. `JobProgress` sends a trailing publish.** When `Update` throttles a publish, it arms a one-shot timer, through the injected `TimeProvider`, that publishes the latest snapshot once the 100 ms window ends. At most one timer is pending at a time. This makes sure the last current item, container and counts show up even when no further update follows.

**D4. New progress fields are live-only and are not part of the constructor.**
- `IJobProgress` gets `SetContainer(string? container)` and `SetCount(string label, long value)`. `SetCount` inserts the count or updates it in place, so counts keep the order in which they were first set.
- `JobSnapshot` gets `init` properties `Container` (string?) and `Counts` (an `IReadOnlyList<JobCount>`, default empty), with `record JobCount(string Label, long Value)`.
- `JobProgress` keeps both in private fields, not on `Job`, and adds them to each snapshot with a `with` expression.

Existing `new JobSnapshot(...)` calls and `JobSnapshot.Of(job)` stay unchanged, and no migration is needed. History snapshots loaded from the database simply have no container and no counts.
*Alternative:* `[NotMapped]` properties on `Job`. That mixes live UI state into the entity and would still need `Of` to change.
*Alternative:* adding the container to `CurrentItem` as text. That cannot be styled, and it breaks tests and other code that read `CurrentItem`.

**D5. The VFS reports a container before opening it.** `WalkAsync` gets an optional `Action<Location>? opening` parameter. `VisitAsync` calls it right before `reader.OpenAsync`. The callback is synchronous and cheap, and existing callers need no change. The scan passes `loc => progress.SetContainer(ContainerLabel(loc))`.
*Alternative:* a new `VfsNodeKind.Opening` node kind. Every consumer of the walk would then have to ignore it.

**D6. The import scan sets the container label and the counts.**
- Container label: the outer file name followed by the file name of each inner entry, joined by ` → `, for example `Packs.zip → Forest.unitypackage`. It uses file names, not full paths, to keep the line short. For each file node, the scan calls `SetContainer(node.Location.Container is { } c ? ContainerLabel(c) : null)`, so the label goes back to the parent container, or clears, as the walk moves on.
- Counts: the scan keeps local counters and calls `SetCount("files found", n)` for every `File` node, including skipped ones. It calls `SetCount("importable", m)` when a file is classified as a supported `FileKind.Asset`.
- After grouping, it calls `SetCount("assets", created.Count)`. The container is cleared when the scan ends.
- Counts are displayed with `N0` formatting and joined by ` · `, as their own caption line under the stage line.

**D7. Resuming an import.** `JobProgress` is created per run. After a restart, an import that resumes past the scan (`StagedThrough`) no longer has the scan counts. On resume, the stager sets "assets" from a count of the session's staged assets. File counts are not rebuilt.

## Risks / Trade-offs

- [Each job view on screen has its own timer] → Timers run only while the job is `Running`, and only a few jobs show at a time (the quick view shows up to 6). The cost is negligible.
- [`SetCount` runs per scanned file, which adds lock work] → It is the same lock and throttle that `Advance` already uses. That is a dictionary or list update under a lock already taken. No database work is added.
- [A trailing publish can arrive after `Finish`] → The timer callback checks that the job is still active and skips the publish otherwise, so a finished job does not come back to the active list.
- [After a restart mid-import, only the asset count is shown] → An accepted limitation (D7). The scan counts matter mostly while the scan runs.
- [The rule for "importable" follows the classifier] → It follows the same rule the scan already uses to stage files, so the count matches what grouping receives.
