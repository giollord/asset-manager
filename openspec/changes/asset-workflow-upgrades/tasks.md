# Tasks

## 1. Drag-out spike

- [x] 1.1 Prototype D7 in the MAUI app: on `pointerdown` plus movement on a Browse card, call .NET, build a shell data object for 2 temp files with `SHCreateDataObject`, and call `DoDragDrop` on the UI thread. Verify by hand that dropping into Explorer and into a Unity Project window copies both files. Record in design.md D7 whether the in-card gesture works, or whether the drag-handle fallback is needed.

## 2. Data model and migration

- [x] 2.1 Add the domain changes:
  - `Asset.Favorite`, `Rating` and `Notes`
  - `GridUncertain`
  - `SpriteSheetMetadata` (frames, grid, confidence, `DetectedBy`)
  - `ImageMetadata.PixelScale`
  - `EditedFields.Grid`
  - `TagOrigin.Source`, `PreviewKind.SpriteSheet`
  - the `SourceTag` and `AssetTagOptOut` entities

  Configure them in `CatalogDbContext`, with indexes on `Favorite`, `Rating` and `GridUncertain`. Verify that `CatalogSchemaTests` is updated and passes.
- [x] 2.2 Create the `AddWorkflowFeatures` EF migration, including recreating `asset_fts` with a `notes` column. Extend `SearchIndexer` to fill it. Verify with a `CatalogMigrationTests` case that an older catalog fixture migrates, keeps its assets and tags, and that FTS finds a word from the notes.
- [x] 2.3 Extend `SearchQuery` (`FavoritesOnly`, `MinRating`, `HasNotes`, `GridUncertain`, `ImageQueryId`) and `AssetSearchService` with the new filters. Verify with `AssetSearchTests` cases for each filter and a URL-token round trip.

## 3. Favourites, ratings and notes

- [x] 3.1 Add the favourite toggle, star rating and notes editor to `AssetDetailsDrawer`, a favourite star and rating on `AssetCard`, and "Favourite" and "Rate" actions to `BulkActionBar`. Make sure re-import and source updates (`Reimport`, `ImportCommitJob`) carry these fields over. Verify with bUnit tests for the drawer and the bulk bar, and an import test showing that a re-import keeps rating and notes (spec "Rating survives re-import").
- [x] 3.2 Add the Browse filter controls (favourites only, minimum rating, has notes). Verify with a `BrowseTests` case for the "Favourites with a minimum rating" scenario.

## 4. Keyboard navigation

- [x] 4.1 Implement the Browse keyboard handler in `browse.js` and its .NET callbacks:
  - focus tracking, arrow keys and Shift+arrows, Ctrl+A, Enter and Esc, Space, `/`
  - `F`, `0`-`5`, Delete
  - the input-field guard

  Add the `?` shortcut dialog, driven by the same shortcut table. Verify with bUnit tests for the .NET actions (rating, favourite, delete confirmation), and by hand with `tools/cdp.mjs` for focus movement and the "Typing in search" scenario.
- [x] 4.2 Document the shortcuts in the README ("Using the app" or a new "Keyboard shortcuts" section). Verify that the README table matches the `?` dialog.

## 5. Suggested tag vocabulary

- [x] 5.1 Add `SuggestedTags.Default` (the built-in list from the spec), stored through `CatalogSettingsService` (missing value = default). Add the "Preferred tags" line to the `ItemPass` and `PackPass` prompts, and the synonym fold after `AiText.NormalizeTags`. Verify with prompt tests showing that the line lists the catalog's tags, and a unit test showing that "lowpoly", "low-poly" and "low_poly" fold to "low poly".
- [x] 5.2 Add the suggested-tags editor to Settings → AI (chips with add, remove, reset). Verify with a `SettingsTests` case: add "voxel", reset, and see that the default is restored and existing assets are unchanged.

## 6. Source tags

- [x] 6.1 Implement `SourceTagService`:
  - add and remove at source level, with materializing `Origin = Source` rows minus opt-outs, and removing only `Source`-origin rows
  - recording an opt-out when a `Source`-origin tag is removed from one asset
  - `ApplyAsync(sourceId)`

  Call `ApplyAsync` after import commit, re-import, version update, rescan import and AI `ReplaceOriginTagsAsync`. Verify with `TagServiceTests` for the spec scenarios "Tag the whole pack", "Remove source tag keeps user copy", "Opt-out survives re-import" and "New assets inherit".
- [x] 6.2 Show and edit source tags on the Sources page, and show the origin "source" in the details drawer's tag list. Verify with a bUnit test that adding a source tag in the UI updates the asset count of a collection requiring it.

## 7. Tag consensus

- [x] 7.1 Implement the candidate query (tag and style shares over game assets, overall and per type, with thresholds, excluding existing source tags), and the `ConsensusShare`, `ConsensusMinAssets` and `ConsensusEnabled` settings in Settings → AI. Verify with a data test: a source with 30% of a tag gives no candidate, and one with 80% gives one.
- [x] 7.2 Implement `ConsensusPass`: the prompt with the candidate table and the with/without contact sheets, the schema-validated `ConsensusResult`, and applying accepted tags through `SourceTagService` with origin AI. Run it after `ItemPassAsync` in imports, after re-enrichment that covers a whole source, and as a "Suggest source tags" job on the Sources page. Verify with a `SchemaAwareChatClient` test for accept and reject, and a gated real-model test (`AiModelFact`) on a fixture pack with mixed art.

## 8. Pixel-art detection

- [x] 8.1 Implement `PixelArtAnalyzer` (pixel scale k, the hard-edge share, the palette bound) and replace `PixelArtMaxEdge`/`PixelArtMaxColors` in `PreviewOptions` with the new thresholds. Store `PixelScale`, and set the style and the AI-origin tag "pixel art" unless the style was edited. Verify with `ImageHandlerTests` on generated fixtures: 4× upscaled 16 px art gives k = 4 and is pixel art; a smooth 2048 px gradient is not; a 1× 32-colour icon is.
- [x] 8.2 Use the item output's `PixelArt` hint only as described in D4. Verify with an `ItemPassTests` case: the AI says pixel art but the measurement says no, and the asset is not marked.

## 9. Sprite sheets

- [x] 9.1 Implement the slicing readers: Unity `.meta` multiple sprites (with the y flip), TexturePacker JSON hash/array and XML, Aseprite JSON. Look them up as companions or `.meta` during metadata extraction. Verify with unit tests over fixtures for each format, including the "Unity sliced sprite" scenario through a `.unitypackage` built by `FixtureBuilder`.
- [x] 9.2 Implement `GridDetector` (gutter method, packed method with divisor candidates and an optional hint, confidence, empty-cell skipping). Verify with generated fixtures: the 512×64 run cycle with gutters, the 4×4 packed sheet without gutters, an irregular atlas (marked uncertain), and a seamless texture (no grid).
- [x] 9.3 Add the `SpriteSheet` hint to `ItemResult`, and route type decisions through `SpriteSheetClassifier.Decide(slicing, grid, hint)`, both in metadata extraction and after the item pass. Remove the AI-only move to SpriteSheet in `ItemPass.Apply`. Verify with tests for the spec scenarios "AI guess rejected" and "Hint checked by the detector".
- [x] 9.4 Store the extra `PreviewKind.SpriteSheet` preview when the thumbnail has fewer than 64 px per cell. Add the grid overlay and editor (cell size, offset, spacing, columns, rows, frame count, "is a sprite sheet" toggle, sets `EditedFields.Grid`), and the frame viewer (`spriteViewer.js`, pixelated drawing, row or range playback at 1-30 fps) in the details drawer. Verify with bUnit tests for the editor (scenario "Fix cell size") and by hand with the drive detached (scenario "Play a row offline").
- [x] 9.5 Implement frame export: selected frames to PNG files named from the sheet name plus the frame number or slicing name, and a row or range to animated WebP through `AnimatedWebp`. Verify with an exporter test on a fixture sheet: 8 PNGs at the original cell size.
- [x] 9.6 Add the Maintenance action "Re-detect sprite sheets and pixel art" (a background job over image, texture and sprite sheet assets whose originals are available, respecting user edits). Add the "grid uncertain" Browse filter. Verify with a job test, and that `GridUncertain` assets appear under the filter.

## 10. Search by image

- [x] 10.1 Implement the image query: resize, describe with `StructuredChat`/`ItemResult`, embed with the `Documents` builder, run the vector search with filters, re-rank by palette (Lab nearest-colour distance), and keep the query in `ImageQueryCache` (LRU of 8, never persisted). Verify with a unit test of the re-rank maths, a `SemanticSearchTests` case with a fake chat client, and a check that the catalog row counts are unchanged after a query.
- [x] 10.2 Add the image drop zone and paste handling on Browse (routing in `fileDrop.js` so drops elsewhere still import), the image chip with remove, "unavailable without model" handling, and the "query image expired" state. Verify with bUnit tests for the chip and the no-model message, and by hand: dropping on the zone searches, and dropping elsewhere opens the import.

## 11. Credits report

- [x] 11.1 Implement `CreditsReport.Build` with Markdown, text and CSV renderers (grouped by source, overrides listed, unknown licenses first under a warning). Add the actions to the bulk bar, the Collections page and the Browse menu, using the folder or file picker. Verify with unit tests for the "Credits for a collection" scenario in all three formats, and a test that it runs with every source unavailable.

## 12. License audit

- [x] 12.1 Implement `CatalogStatsService.LicenseAuditAsync` for the four problems, with source and asset counts. Verify with `CatalogStatsTests` cases for each problem, including "Missing attribution text" disappearing after an edit.
- [x] 12.2 Add the dashboard audit card and the Sources page audit tab with a problem filter and drill-downs (edit source, Browse filtered). Verify with `DashboardTests` for the "Dashboard tile" scenario and navigation to the filtered list.

## 13. Drag out

- [x] 13.1 Implement `IDragOutService` in `WindowsPlatformServices` (as proven in 1.1) and the "not supported" version in `UnsupportedPlatformServices`. Wire `browse.js` to start the drag for the focused card or the selection, using `ExtractForClipboardAsync`, with the "Preparing N files…" overlay and a snackbar for unavailable assets. Verify by hand with the scenarios "Drag textures into Unity" and "Unavailable item in selection", and with a unit test of the file-list resolution, including the unavailable case.

## 14. Unity project export

- [x] 14.1 Add Unity project detection for the target folder and the "Unity project export" and "include referenced files" options to `ExtractDialog`/`ExportOptions`. Make `AssetExporter` copy `.meta` files next to the outputs, keeping their names in step with renames. Verify with `AssetExporterTests`: an export from a fixture `.unitypackage` writes each file with its `.meta` and an unchanged GUID.
- [x] 14.2 Implement the GUID map per source and the transitive YAML reference scan (`.mat`, `.prefab`, `.asset`, `.controller`, `.overrideController`, `.anim`), ignoring built-in GUIDs. Add the dependencies to the plan, marked as such. Verify with a fixture prefab → mesh + 2 materials → 4 textures, covering the "Prefab with dependencies" file set.
- [x] 14.3 Implement the GUID conflict scan of the target project's `Assets/**/*.meta`, and the skip/continue choice in the dialog. Verify with a test for the "GUID already in project" scenario: the conflict is listed and nothing is written before the choice.
- [x] 14.4 Verify by hand that the exported prefab from 14.2 opens in a Unity project with its materials intact. Document Unity project export in the README.

## 15. Library sync

- [x] 15.1 Implement `RescanJob` for folder diffs (applying the import rules) and archive/package fingerprints, with skipped detached sources and results per source. Verify with tests for "New files in a folder pack", "Ignored folder" and "Detached drive".
- [x] 15.2 Add the rescan results UI on the Sources page with its actions: import added files into the source through staging, re-import changed, remove missing (with confirmation), and update through the version flow. Verify with an import test for "Import added files", including the source tags on the new assets.
- [x] 15.3 Add library folders in Settings (volume-aware paths), the library scan (not-yet-imported entries, version candidates through `SourceVersions`, ignore rules), and a multi-select import. Add the "check for changes at startup" option with a delayed low-priority start and a result notification. Verify with tests for "New pack downloaded" and "Startup check" (no import without confirmation). Document both in the README.

## 16. Catalog backup

- [x] 16.1 Implement `CatalogBackupService`:
  - online backup into a `.tmp` file, then rename
  - the free-space check
  - auto/manual naming and retention
  - the startup schedule (off, daily, weekly) and `BackupFolder` defaulting to `<data>/backups`

  Verify with tests: a backup made while another connection writes opens and is consistent (scenario "Backup during enrichment"); retention keeps manual backups (scenario "Retention"); no partial file is left on failure.
- [x] 16.2 Implement restore (cancel jobs, manual backup of the current catalog, close, replace, remove WAL/SHM, reopen) and the Settings UI (list with date, size and kind; back up now; restore with a confirmation that mentions cancelling jobs). Verify with a test for the "Restore yesterday's catalog" scenario, and a bUnit test for the confirmation text. Document backups in the README "Data folder" section.

## 17. Update check

- [x] 17.1 Implement `UpdateChecker` (GitHub latest-release request with a plain User-Agent, a 10 s timeout, version comparison, the 24 h throttle, and `LastSeenVersion`/`LastCheckFailed` in `AppSettings`, skipped in Debug), and the About UI (toggle, "Check now", "x.y.z available" link, "last check failed" note) with one notification per new version. Verify with unit tests using a fake `HttpMessageHandler` for "Newer release", "Offline" and "Default off" (no request made), and bUnit tests for About. Update the README to name the update check as the only optional network use besides model downloads.

## 18. Whole-card selection

- [x] 18.1 Move the click and double-click handlers in `AssetCard` from the preview `div` to the root `MudPaper`, stop propagation on the checkbox, drop `selectable` from `asset-card-name`, and add `.asset-card { user-select: none; }` in `app.css` (D16). Verify with bUnit tests in `AssetCardTests`/`BrowseTests`: a click on the name selects only that card ("Click on the name"), Ctrl-click on the type line adds to the selection, a double click on the name opens the details, and a checkbox click toggles without also selecting-only. Verify by hand with `tools/cdp.mjs` that double-clicking a card name highlights no text, while the name in the details drawer can still be selected and copied.

## 19. Card context menu

- [x] 19.1 Add `AssetContextMenu` (one instance on Browse, `MudPopover` at the pointer or card position, items with icons: Open details, Find similar, Copy name) and `@oncontextmenu:preventDefault` on `AssetCard` via a new `OnContextMenu` parameter. Remove the find-similar button from `Browse.Overlay`, leaving only play. Implement Copy name with `navigator.clipboard.writeText` in `browse.js` plus a snackbar, and the disabled state with its reason for Find similar (D17). Verify with bUnit tests for "Find similar from the menu", "Not enriched yet" and "Menu on an unselected card" (selection unchanged), and by hand that "Copy name" puts the text ondrag the clipboard in the MAUI app and that the WebView's default menu no longer appears on cards.
- [x] 19.2 Map the `ContextMenu` key and Shift+F10 on the focused card to the menu in the `browse.js` keyboard handler from 4.1, focusing the first item, and add both to the `?` dialog and the README shortcut table. Verify by hand with `tools/cdp.mjs` (scenario "Keyboard").

## 20. Turntable of stored renders

- [x] 20.1 Add `PreviewQueries.ModelTurntableAsync` returning rings, azimuths, primary index and the views, with the one-ring fallback when the stored views do not match the current `PreviewOptions` (D18). Verify with data tests: 24 stored views with default settings give 3 × 8 and the primary index; 16 views after the settings changed to 3 × 8 give 1 × 16.
- [x] 20.2 Implement `ModelTurntable.razor` and `turntable.js` (preloaded images, pointer-capture drag with azimuth wrap and clamped ring steps, arrow keys, grab cursor). Check the azimuth order in `ModelRendering` and set the drag direction so the model turns with the pointer. Verify with a bUnit test that the component renders all views and starts at the primary one, and by hand with the scenarios "Turn a model offline" and "Tilt".
- [x] 20.3 Use `ModelTurntable` in the details drawer's "Renders" mode for models, replacing the single image and the `#details-views` strip, and in `ModelViewer` as the fallback on `OriginalUnavailableException` with the "Original not available" note. Verify with bUnit tests for the drawer (no `#details-views` for a model; the turntable is present) and for `ModelViewer` with a fake `OriginalUnavailableException` (scenario "View model with the drive detached"), and by hand in the MAUI app with a model whose drive is detached, both in the drawer and in the "Open original" dialog.

## 21. Integration check

- [x] 21.1 Run the full test suite, including the gated real-model and samples tests when available. Then import a representative subset of `ASSETMANAGER_SAMPLES` containing a pixel-art pack with sprite sheets, a low-poly pack and a `.unitypackage` with sliced sprites. Verify:
  - the sheets and grids are right
  - consensus added the expected source tags
  - image search finds the related assets
  - the credits report and the audit match the licenses
  - a backup and restore round trip works

  Record the tuned thresholds in design.md "Open Questions".

## 22. README screenshots

- [x] 22.1 Build a screenshot catalog from CC0 packs only in a separate data folder, run the Debug MAUI app at 1440×900 in the dark theme, and capture `docs/screenshots/browse.png`, `details-model.png` (a model with the turntable or the 3D view), `dashboard.png` and `import-review.png` with `node tools/cdp.mjs shot` (D19). Optimize the PNGs (each under ~400 KB) and check them by eye: no personal paths, drive letters or non-redistributable art.
- [ ] 22.2 Add a "Screenshots" block to the README after the intro and before "Download", Browse full-width and the other three below it, each with alt text; leave the logo line unchanged. Verify that the README renders with the images in a Markdown preview and on the branch's GitHub page (scenario "README on GitHub").

## 23. Texture matching for model previews

- [x] 23.1 Data: `CompanionOrigin` and `AssetFile.Origin` (migration; existing rows are Reference), `EditedFields.Textures`, `ModelMetadata.TextureSuggestions`, `AppSettings.TextureMatchEnabled` / `TextureMatchThreshold` / `TextureSuggestThreshold`, and the search filter "textures matched by AI" (D20).
- [x] 23.2 Name match in `CompanionResolver` (step 4, per material): stripped prefixes, suffixes and extension; keys are the material, the missing reference and the model; a unique match anywhere in the source; normal and emissive maps along; origin NameMatch stored by the stager. Tests: scenarios "Prefixed names", "Reference with another extension and folder", the name-match half of "Two materials", and the existing grouping tests.
- [x] 23.3 `TextureMatcher` (Processing): `MaterialsToMatch` (UVs, missing base colour, evidence), candidate ranking with the non-colour map and normal-colour exclusions, candidate renders through `IModelRenderer`, attaching (origin Ai) and suggestions. Tests: scenarios "Model without texture coordinates", "Normal map is not a colour candidate" and "Untextured low-poly pack".
- [x] 23.4 `TextureJudge` (AI) and wiring into `ModelEnrichment` before the orientation judgment, with one re-render; the reverse-order check; the text-only fallback; Settings → AI controls. Tests with a scripted chat: scenarios "Two materials", "Atlas shared by a pack", "Unsure choice", "None fits" and "Order changes the answer".
- [x] 23.5 Details drawer "Textures" section (origin chips, detach, apply or dismiss a suggestion, choose a texture) with re-render and `EditedFields.Textures`; the Browse model-issue filter. bUnit tests: scenarios "Choose a texture" and "Wrong AI match".
- [x] 23.6 Check by hand on Quaternius "Textured Stylized Trees" (no references) and Humble "Low Poly Farm" (missing references) from `ASSETMANAGER_SAMPLES`: the models are textured after import, the AI matches are marked, and detaching one re-renders. Record the thresholds in design.md under Open Questions.

## Workflow follow-up

- Archive the change after review, and check that the main specs gain the new `sprite-sheets`, `catalog-backup` and `library-sync` capabilities.
