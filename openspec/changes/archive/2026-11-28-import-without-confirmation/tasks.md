# Tasks

## 1. Data model

- [x] 1.1 Add `ImportSession.Results` (nullable JSON list of per-source results: source id, name, assets added, and for replacements the old source id with updated/added/removed counts) and `ImportSession.ReplaceSourceId` (nullable long). Add an EF migration for both. Verify with `dotnet build` and with a test that opens a migrated empty catalog and round-trips both fields.
- [x] 1.2 Add `ReplaceSourceId` to `ImportRequest`, and have `ImportStager.CreateSessionAsync` store it. Reject it unless the request has exactly one input and the source exists. Verify with a unit test in `ImportStagingTests`.

## 2. Replacement decisions (D4)

- [x] 2.1 Change `SourceVersions.ProposeAsync`:
  - A store-id match sets `ReplacesSourceId` and `ReplaceAccepted = true`.
  - A session `ReplaceSourceId` applies to the single root source.
  - Weak overlap matches are not recorded, and the unchanged-files `Included = true` flip happens only for accepted replacements.

  Verify with `ImportVersionTests`, rewritten without `SetReplacementAsync`:
  - "Renamed package version" and "Removed asset in new version" replace automatically.
  - An 80% path overlap without a store id becomes a new source and leaves the old one unchanged.
  - An explicit `ReplaceSourceId` replaces.

## 3. Automatic commit in the import job (D1, D2, D3, D5, D7)

- [x] 3.1 Extract the body of `ImportCommitJob.RunAsync` into an `ImportCommitter` service, registered in DI. It:
  - starts with a conditional `Scanning`/`Ready` → `Committing` update and returns if the session is `Cancelled`;
  - runs only on sources still staged for the session;
  - writes a `Duplicate` `StagedSkip` ("already cataloged") for each dropped duplicate;
  - records the per-source `Results`, capturing the replacement diff before `ReplaceAsync`;
  - stops deleting `StagedSkip` rows.

  Keep `ImportCommitJob` as a wrapper around it. Verify with `ImportCommitTests`: an import is committed once, a second run is a no-op, duplicates appear as skips, and `Results` holds the counts.
- [x] 3.2 Reorder `ImportJob.StageNames` to Scanning, Grouping files, Generating previews, Commit, AI pack pass, AI item pass. Call `ImportCommitter` after previews, and skip it when the session is already `Committed`. Give `ImportEnricher.PackPassAsync`/`ItemPassAsync` separate display-stage and `StagedThrough`-marker arguments, keeping markers 4 and 5. Remove the skip deletion at the end of `ImportJob.RunAsync`. Verify:
  - an import with "No AI" and one with Full AI both end `Committed` with assets visible in `db.Assets`, without calling any confirm;
  - a job interrupted after previews (`StagedThrough = 3`, state `Scanning`) commits on resume;
  - the job-progress stage test sees "Commit" as stage 4 of 6.
- [x] 3.3 Remove `ImportService.ConfirmAsync`. Change `CancelAsync` as in D7: a conditional `Scanning → Cancelled` update clears the staging as today; otherwise it only cancels the job and keeps state and rows. Verify with tests:
  - a cancel during previews leaves no assets, sources or tags;
  - a cancel during the AI item pass after the commit keeps every asset and leaves the session `Committed`;
  - a cancel racing the commit ends in exactly one of the two outcomes.
- [x] 3.4 Update `ImportKit` and the other import tests (`ImportStagingTests`, `IgnoredFoldersTests`, `TextureMatchImportTests`, `StagedRerenderTests`, `ReimportTests`, `Sync/RescanTests`) to wait for the import job to finish instead of confirming. Change the assertions that read the staged preview to read the committed catalog and the skips. Verify with `dotnet test --filter "FullyQualifiedName~Import|FullyQualifiedName~Sync"` passing.

## 4. Results read model and dismissal (D6)

- [x] 4.1 Replace `ImportReviewService` with `ImportResultsService.LoadAsync(sessionId)`: session, per-source results, and skips grouped by source, including the "Not imported" group for skips without a source. Add `ImportService.DismissAsync`, which deletes the session's skips and marks the results dismissed. Have `StartAsync` dismiss earlier committed sessions. Delete `StagedRegrouping` and its registration. Verify with unit tests: "Skipped items listed" (a `.cs` script and a corrupt zip appear with their reasons), the results survive re-creating the services (restart), and a new import clears the old skips.
- [x] 4.2 Have `OpenSessionsAsync` return running sessions plus the newest committed session whose results were not dismissed. Verify with a unit test covering the running, undismissed and dismissed cases.

## 5. Import page UI (D8)

- [x] 5.1 Rework `Pages/Import.razor` into the steps Inputs → Importing → Results:
  - **Importing:** progress, the Resume alert, and Cancel import. Cancel says "Stop AI enrichment" after the commit.
  - **Results:** assets added per source with a link to `browse?q=` filtered by source, replaced sources with their counts, skipped items with their reasons, Dismiss, and Import more.

  Remove the Confirm button and the Confirm step. Delete `Components/ImportReview.razor` and its styles. Verify with a bUnit test replacing `Ui/ImportReviewTests.cs`: no `#confirm-import` exists, a finished import shows `#import-results` with the skip reasons, and Dismiss returns to Inputs.
- [x] 5.2 Update `Components/SyncResults.razor`:
  - "Update source" passes `ReplaceSourceId`; nested archives resolve the nested child source by location.
  - "Import added files" and the library import tooltips and comments no longer mention a review.

  Verify with `Ui/SyncResultsTests` asserting that `ImportRequest.ReplaceSourceId` is set for both update buttons.

## 6. Legacy sessions (D9)

- [x] 6.1 In the startup resume hook, move `Ready` sessions without a pending or running job to `Committing` and queue their import job. Verify with a test that seeds a `Ready` session with staged assets, runs the hook and the job, and sees the assets committed.

## 7. Docs and integration

- [x] 7.1 Update the README: the screenshot table entry "Import review", and the sync paragraphs (lines on "Import added files" and "Update source"). Describe automatic commit, the results view and when a new version replaces a source. Retake `docs/screenshots/import-review.png` as an import-results screenshot, or drop it. Verify the README has no remaining "import review" or "confirm" wording for imports.
- [x] 7.2 Run the full test suite (`dotnet test AssetManager.slnx`), then run the app and import a sample folder that contains a script and a duplicate. Confirm that:
  - assets appear in Browse with no confirmation;
  - the results list the skips;
  - Cancel during AI keeps the assets.

## Workflow follow-up

- Archive the change after review with `openspec archive import-without-confirmation`.
