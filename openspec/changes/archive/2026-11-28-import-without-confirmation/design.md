# Design

## Context

See proposal.md for motivation. How imports work today:

- **Staging.** Rows written during staging are ordinary `Source`/`Asset` rows with `ImportSessionId` set. Global query filters hide them from the catalog.
- **Import job.** `ImportJob` runs scan → grouping → metadata/previews (the session becomes `Ready`) → AI pack pass → AI item pass.
- **Commit.** The user's Confirm queues `ImportCommitJob`. That job commits each staged source in its own transaction:
  - It deletes `Included == false` assets and clears `ImportSessionId`.
  - It merges into `IntoSourceId` for "import added files".
  - It applies accepted version replacements through `SourceVersions.ReplaceAsync`.
- **AI after confirm.** Asset ids survive the commit, so an AI pass still running carries on through `Source.CommittedFromSessionId`.
- **`StagedSkip` rows.** They feed the pack pass (noted files, documents) and are deleted after the commit and pack pass.
- **`StagedThrough`** records the last finished staging stage (3 = previews, 4 = pack pass, 5 = item pass). The enricher uses it, together with the display stage index it receives, to skip finished work on resume.
- **Version proposals.** `SourceVersions.ProposeAsync` runs at the end of grouping. It sets `ReplacesSourceId`/`ReplaceScore`, with score 1 for the same Asset Store id and the Jaccard overlap otherwise. It also flips the old version's unchanged duplicates to `Included = true`. The user's choice lands in `ReplaceAccepted`.

## Goals / Non-Goals

**Goals:**
- Commit automatically right after previews, reusing the existing per-source commit code paths (plain, merge-into and replace) unchanged.
- Keep "cancel before the commit leaves no trace" for free by keeping the staging mechanism.
- Make the import results survive a restart.

**Non-Goals:**
- Moving review-only tools (AI correction with a message, split/merge of file groups) into the catalog.
- An undo for an import. The catalog's bulk delete and the existing backups cover this.
- Changing how duplicates are detected or how replacement keeps user edits.

## Decisions

### D1. Keep staging, commit inside the import job
`ImportJob` calls a new `ImportCommitter` service between the previews and the AI pack pass. The body of `ImportCommitJob.RunAsync` moves into that service. The state moves `Scanning → Committing → Committed`, and `Ready` is no longer entered by new sessions.

- **Alternative: write rows straight into the catalog during scanning.** Rejected. It would show half-grouped assets without previews. It would also lose the atomic per-source commit. And cancel would need its own cleanup of committed rows.
- **Alternative: keep a separate commit job and queue it automatically.** Rejected. It adds a second job, a second progress entry and a race with the AI stages for no benefit.

`ImportCommitJob` stays registered as a thin wrapper that calls `ImportCommitter`. `import.commit` rows already queued in a user's database can then still run after the upgrade.

### D2. The commit is idempotent and resumable
The committer works only on sources that still have `ImportSessionId == sessionId`, so running it twice is harmless. On resume, `ImportJob` handles each state as follows:

- `Scanning` with `StagedThrough >= 3`, or `Committing`: commit, then continue with the AI stages.
- `Committed`: skip straight to the AI stages, which already happens today after an early confirm.

### D3. Decouple display stage numbers from `StagedThrough`
The visible stage order becomes scan, grouping, previews, commit, pack pass, item pass (job-progress delta). `StagedThrough` keeps its stored meaning (4 = pack pass done, 5 = item pass done), so sessions from before the upgrade still resume correctly. `PackPassAsync` and `ItemPassAsync` get separate "display stage" and "staged-through marker" arguments; today one `stage` argument serves both.

### D4. Who decides replacements
`ProposeAsync` makes the decision itself:

- **Strong match** (same Asset Store id, score 1): it sets `ReplacesSourceId` and `ReplaceAccepted = true`.
- **Explicit target**: a new `ImportRequest.ReplaceSourceId`, stored on the session, applies to the session's single root source. It sets `ReplacesSourceId = target` and `ReplaceAccepted = true` without any matching.
- **Weak overlap match**: it is not recorded at all. The old version's unchanged files then stay `Included = false` duplicates and are skipped as already cataloged, unless the user included duplicates.

The "same content re-added" short-circuit stays as is.

- **Alternative: keep the weak proposal and have the committer ignore it.** Rejected. It leaves the unchanged-files `Included = true` flip in place, which would silently import duplicates into a new source.

`SyncResults.UpdateAsync` passes the rescanned source's id. For a nested archive it passes the id of the nested child source that has that location. `SourceVersions.MatchListingAsync` stays: the library listing's "new version of X" chip is still shown as information.

### D5. Duplicates become skips at commit time
Today duplicates are staged assets with `Included = false` and `DuplicateOfAssetId` set; the review showed them. Before deleting non-included assets, the committer writes one `StagedSkip` per dropped asset: `Kind = Duplicate`, the primary file's path, and the reason "already cataloged". The results summary then lists them like every other skip. `Included` stays as an internal flag, and only duplicate detection sets it.

### D6. Results kept on the session
- **Per-source results.** The committer records the results of each source as JSON on `ImportSession`, in a new `Results` column (EF migration). Each entry holds the source id, name, assets added, and for replacements the old source id with the updated, added and removed counts. The diff counts must be captured before `ReplaceAsync`, because the staged source no longer exists afterwards.
- **Skips.** `StagedSkip` rows are no longer deleted after the commit or pack pass. They are deleted when the user dismisses the results or starts a new import, through `ImportService.DismissAsync`; `StartAsync` dismisses earlier committed sessions.
- **Read model.** `ImportReviewService` is replaced by a small `ImportResultsService.LoadAsync(sessionId)` that returns the session, the per-source results and the skips grouped by source. Its source rows link to `browse?q=` with the `SourceId` filter, which Browse already supports.

- **Alternative: derive the results from the job summary.** Rejected. The job summary only holds totals and is not shaped for per-source rows.

### D7. Cancel semantics
`CancelAsync` first tries a conditional `Scanning → Cancelled` update.

- **If that update wins:** the behavior is as today. It cancels the job, clears the staged rows and deletes unused tags.
- **If the session is already `Committing` or `Committed`:** it only cancels the import job. The state stays, and the committed rows stay. If the AI was cut off mid-pass, those assets stay `Pending` or `NotEnriched` and can be resumed or re-enriched.

The committer begins with a conditional `Scanning → Committing` update. If that update finds the session `Cancelled`, the committer returns, which settles the race with a cancel that arrives at the same moment.

### D8. UI
`Import.razor` gets three steps: Inputs → Importing (progress, warnings, Resume, Cancel) → Results (the summary from D6, Dismiss, "Import more").

- `ImportReview.razor` is deleted, together with its CSS and the UI test that drives it.
- `StagedRegrouping` and its DI registration are removed.
- `ImportReviewService` is removed, including `CorrectAsync`, `SetReplacementAsync` and `FindSourcesAsync`.
- `EnrichPayload.Correction` stays, so persisted enrich jobs still deserialize.
- `OpenSessionsAsync` keeps its query. Its callers show the running session or the undismissed results.

### D9. Legacy sessions
At startup (the existing interrupted-jobs resume hook), every session in `Ready` with no pending or running job is moved to `Committing`, and its import job is queued. D2 then commits it.

A `Ready` session whose job is still active needs no special handling: the job reaches the commit step itself once it is running the new code. That holds because the job is re-queued on start after the upgrade.

## Risks / Trade-offs

- **Auto-replace by Asset Store id removes assets** that the new version dropped, without asking. → Only exact store-id matches qualify. User edits and tags on matched assets are kept. The results show the removed count. Catalog backups exist for recovery.
- **Unwanted items now land in the catalog** (for example, a demo scene folder). → Ignore rules at import start, and bulk delete afterwards.
- **Weak matches are no longer offered as updates**, so a re-downloaded folder pack without a store id becomes a second source. → The user can delete the old source, or use rescan "Update source", which replaces explicitly.
- **Skip rows are kept longer**, until the results are dismissed. → Bounded to the last import, because starting a new import clears them.
- **Commit happens before the AI pack pass**, so source license and notes fill in after the assets are visible. → This is already the behavior after an early confirm today.

## Migration Plan

1. Add an EF migration for `ImportSession.Results` (nullable JSON) and `ImportSession.ReplaceSourceId` (nullable long).
2. The startup hook from D9 commits legacy `Ready` sessions.
3. Rollback: an older build would see `Committed` sessions and catalog rows it already understands. The new columns are ignored. No data is lost.
