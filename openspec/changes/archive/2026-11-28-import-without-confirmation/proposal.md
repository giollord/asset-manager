# Proposal

## Why

Every import currently stops at a Preview/Review step. Nothing reaches the catalog until the user presses Confirm. For large imports this is friction with little payoff: the catalog already lets users edit, re-enrich and delete assets. An import that nobody confirms (closed window, forgotten tab) also leaves staged rows waiting indefinitely. Imported data should land in the catalog on its own, and the user can remove what they don't want afterwards.

## What Changes

- **BREAKING**: An import commits its sources to the catalog automatically once metadata and previews are done, with no Confirm button. AI enrichment then continues in the background on the committed assets, as it does today after an early confirm.
- **BREAKING**: The Preview/Review step and its tools are removed: include/exclude, inline edits, the AI correction message on a selection, and splitting/merging file groups. Users work on imported assets in the catalog with the existing tools: edit, bulk tags, re-enrich, delete.
- Cancelling an import before the commit discards it, as today. Once the import is committed, "Cancel" only stops the remaining AI work, and the assets stay in the catalog.
- After the commit, the import page shows a **results summary**: assets added per source, sources replaced (with updated/added/removed counts), and the skipped items with their reasons (scripts, duplicates, unsupported, ignored folders, errors).
- New versions of existing sources:
  - A **strong match** (same Unity Asset Store id) replaces the old source automatically.
  - A **weak match** (GUID/path overlap only) is imported as a new, independent source.
  - An explicit "Update source" action from rescan results counts as the user's choice, so that import replaces the source it was started for.
- Duplicates stay skipped unless "Include files that are already cataloged" was turned on when the import started. They are listed among the skipped items in the results.
- The import job's stages are reordered so the commit comes right after previews: scan, grouping, metadata and previews, commit, AI pack pass, AI item pass.
- Rescan "Import added files" and library-folder imports go straight into the catalog without a review.
- Sessions left "ready for review" by an earlier version are committed automatically when the app starts.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `asset-import`:
  - Removes "Staged import preview", "Edit and AI-correct staged items" and "Confirm import".
  - Adds automatic commit and the import results summary.
  - Changes duplicate handling, "Update source from new version", and the wording of the ignored-folder scenarios, which no longer refer to a preview.
- `job-progress`: "Multi-stage import progress" reorders the stages so the commit follows previews.
- `library-sync`: "Acting on rescan results" and "Library folders" no longer go through a staged review. "Update source" replaces explicitly.
- `ai-enrichment`: in "Scripts and non-asset content noted", the user overrides the AI by deleting an unwanted asset in the catalog, no longer in the import preview.
- `app-shell`: the Navigation entry "Import (including any staged sessions)" becomes "Import (including running imports and the results of the last one)".

## Impact

- **Backend** (`AssetManager.AI/Import`):
  - `ImportJob` runs the commit itself after previews, and `ImportCommitJob`'s logic becomes a step the job calls.
  - `ImportService.ConfirmAsync` is removed, and `CancelAsync` changes meaning after the commit.
  - `ImportReviewService` is removed, or reduced to a read model for the results summary.
- **Processing** (`AssetManager.Processing/Import`):
  - `SourceVersions.ProposeAsync` decides on its own: it accepts strong matches and drops weak ones.
  - `StagedRegrouping` is removed.
  - `ImportRequest` gains an explicit source to replace.
- **Domain**:
  - `ImportSessionState` keeps its values; `Ready` becomes a short internal step before the commit.
  - The `Source.ReplaceAccepted` and `Asset.Included` flags become internal; they no longer come from user input.
  - `StagedSkip` rows are kept after the commit for the results summary.
- **UI**:
  - `Pages/Import.razor` loses the Review and Confirm steps and gains a results view.
  - `Components/ImportReview.razor` is deleted.
  - The `SyncResults.razor` texts and the Update action change.
- **Tests**:
  - `ImportCommitTests`, `ImportVersionTests`, `ImportKit`, `RescanTests` and the UI `ImportReviewTests` change.
  - Review-only tests are removed.
- **Docs**: the README import/sync paragraphs and the "Import review" screenshot.
