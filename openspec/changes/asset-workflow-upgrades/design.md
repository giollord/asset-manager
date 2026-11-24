# Design

## Context

See proposal.md for the motivation and the feature list, and the delta specs for the behaviour. Current code that shapes the approach:

- **Tags:** `AssetTag` has the key `(AssetId, TagId)` and one `TagOrigin` (`User`, `Import`, `AI`). `TagService.AddTagsAsync` uses `INSERT OR IGNORE`, so an existing row keeps its origin. AI enrichment uses `ReplaceOriginTagsAsync(..., TagOrigin.AI)`, which removes AI-origin tags the new answer no longer has.
- **AI pipeline (`ImportEnricher`):**
  - the pack pass (`PackPass`) runs first and returns `PackDefaults`, tags and style that every item inherits
  - then the item pass (`ItemPass`) sends contact sheets of up to 8 items and returns `ItemResult` (description, type, tags, colours, style, `IsGameAsset`)
  - `ItemPass.CanRefine` allows type changes within image/texture/sprite sheet
  - nothing runs after the item pass for a whole source
- **Images:** `ImageHandler` computes `ImageMetadata.IsPixelArt` as "max edge ≤ 128 or colours ≤ 64", which is crude. `AssetType.SpriteSheet` exists, but only the AI ever sets it. `ModelRendering`/`AnimatedWebp` can already write animated WebP.
- **Unity:** `UnityPackageReader` extracts each entry's `.meta` next to the file into the extraction cache. `AssetFile.UnityGuid` is stored. `CompanionResolver` and `UnityMaterial` already parse GUIDs out of `.meta` and `.mat` YAML.
- **Export:** `AssetExporter.PlanAsync/ExtractAsync` (conflict choices, structure) and `ExtractForClipboardAsync` (temp folder plus `IClipboardFileService`).
- **Search:**
  - `SearchQuery` is a URL-token record, with `SimilarTo` for "find similar" via `EmbeddingService` (EmbeddingGemma, text only)
  - `asset_fts` is maintained by `SearchIndexer` (columns: name, files, paths, description, tags, source)
  - `LicenseFilters` resolves effective licenses
- **Drops:** `fileDrop.js` treats any file drop on the document as an import (WebView2 gives real paths).
- **Settings and catalog:**
  - app-level settings are in `settings.json` (`AppSettings`); catalog-level ones are in the catalog
  - `CatalogStore` already checkpoints the WAL and copies to `.bak` before migrations
- **Platform:** `IClipboardFileService`, `IShellService` and others are implemented in `WindowsPlatformServices`, and `UnsupportedPlatformServices` is used in the web host.

## Goals / Non-Goals

**Goals:**
- Every feature works on catalog data when drives are detached, except actions that need originals: drag-out, Unity export, frame export and rescan.
- Search, collection and stats queries stay unchanged wherever possible. New data is shaped so that existing queries see it.
- AI output is treated as a hint wherever a deterministic check exists: sprite grids and pixel art.

**Non-Goals:**
- A second embedding model (CLIP/SigLIP) or a new inference runtime.
- Godot or Unreal-specific export. Godot regenerates its `.import` files itself, so plain export already works there.
- Live file-system watching (drives come and go; scans are explicit).
- Auto-installing updates.
- Merging or removing tags by AI consensus. The consensus step only adds.

## Decisions

### D1. Source tags are materialized onto assets
- **Storage:** a new `SourceTag(SourceId, TagId, Origin ∈ {User, AI})` table records the source-level tag. Each asset of the source gets an `AssetTag` row with the new `TagOrigin.Source`. Because of `INSERT OR IGNORE`, an asset that already has the tag from the user, the import or the AI keeps that row, which gives the "keep user copy" behaviour for free.
- **Removing:** removing a source tag deletes only `Origin = Source` rows.
- **Per-asset opt-out:** stored in a small `AssetTagOptOut(AssetId, TagId)` table, which is written when the user removes a `Source`-origin tag from one asset.
- **`SourceTagService.ApplyAsync(sourceId)`:** inserts the missing `Source` rows minus the opt-outs. It is called:
  - after a source tag is added
  - after any import, rescan or version update that adds assets
  - after `ReplaceOriginTagsAsync(AI)`, which may have dropped an AI-origin row for a tag that is also a source tag
- **Rejected: computing effective tags at query time** (asset tags ∪ source tags). It would touch every search, collection, count and FTS path, and make the 1-second search target harder.

### D2. Consensus pass after the item pass
`ConsensusPass` is a new stage per source, after `ItemPassAsync`, and also a standalone AI job.
- **Candidates:** computed in SQL. Count each tag and each style value over `IsGameAsset` assets of the source, overall and per type. Keep those with share ≥ `ConsensusShare` (default 0.6) in sources with ≥ `ConsensusMinAssets` (default 5) assets, excluding existing source tags.
- **Prompt:** source name, description, per-type counts, a candidate table, and two small contact sheets per candidate (up to 4 with the tag, up to 4 without), reusing `ItemPass.ContactSheet`. The images are sent only when the vision projector is loaded; otherwise the prompt is text only.
- **Answer:** the schema-validated `ConsensusResult { Decisions: [{ Tag, ApplyToSource, Reason }] }`. Accepted tags go through D1 with origin AI. Styles that are accepted become source tags under the same normalized name, so "pixel art" as a style and as a tag converge.
- **Settings:** `ConsensusEnabled`, `ConsensusShare` and `ConsensusMinAssets` live in `AppSettings` (Settings → AI).
- **Rejected:** applying above-threshold tags automatically without the AI. The "mixed pack" case (low-poly props plus realistic skyboxes) shows why a judgement step is needed. The threshold only limits what the AI is asked.

### D3. Sprite sheets: slicing data, then algorithm, then AI hint
`SpriteSheetMetadata` is stored on the asset like `ImageMetadata`. It has:
- `Frames: [{ Name?, X, Y, W, H }]`
- the grid form: `CellW, CellH, OffsetX, OffsetY, SpacingX, SpacingY, Columns, Rows`
- `FrameCount`, `Confidence`
- `DetectedBy ∈ { SlicingData, Algorithm, User }`

A new flag `GridUncertain` is flattened onto `Asset` for search. A new `EditedFields.Grid` marks user edits.

1. **Slicing readers** (`Processing/Sprites/SlicingReaders.cs`):
   - Unity `.meta`: `spriteMode: 2`, the `spriteSheet.sprites[].rect`. Unity's y is bottom-up, so it is flipped.
   - TexturePacker JSON (hash and array) and XML (Starling/Sparrow).
   - Aseprite JSON (`frames` and `meta.size`).
   
   They are looked up as companions with the same base name, or as the `.meta`.
2. **Grid detector** (`Processing/Sprites/GridDetector.cs`), on the image downsampled only if larger than 4096 px:
   - **Gutter method:** project an "empty" mask (alpha = 0, or equal to the corner background colour within a tolerance) onto x and y. Runs of empty columns and rows give the separators. Regular spacing between them gives the cell size, offset and spacing.
   - **Packed method:** for candidate cell sizes, take divisors of the width and height in 8-512 px plus the AI hint. Score each by the mean seam discontinuity at cell borders (higher is better) and by the similarity of adjacent frames (animation frames resemble each other).
   - The best candidate's score margin over the runner-up gives the confidence. Below `GridConfidenceThreshold` (default 0.6) the result is `GridUncertain` and no grid is stored.
   - Empty cells (fully transparent or background) are not counted as frames.
3. **AI hint:** `ItemResult` gains `SpriteSheet: { Likely, Columns?, Rows?, Frames? }` and `PixelArt: bool?`. The hint adds a candidate to step 2 and decides only when steps 1 and 2 give no result and the image is clearly not a texture. Even then, the type becomes sprite sheet only with a grid, so it stays an image or texture with `GridUncertain`.
4. **Type and order:** detection runs in metadata extraction (steps 1 and 2) and again after the item pass when a hint arrives. `ItemPass.Apply` no longer moves an image to SpriteSheet on the AI's word alone; it routes through `SpriteSheetClassifier.Decide(slicing, grid, hint)`.
- **Rejected: asking the vision model for the cell size.** At contact-sheet tile size (about 256 px), a 16 px grid cannot be seen, and models miscount frames.

### D4. Measured pixel-art detection
`PixelArtAnalyzer` works on the full image, sampled to at most 1024² by taking whole blocks:
- **Pixel scale k:** the largest k in 1..16 such that ≥ 97% of k×k aligned blocks (trying offsets 0..k-1) are uniform among opaque pixels.
- **Hard edges:** the share of opaque pixels whose colour is not in the top-N palette (N = 64) after exact counting. Anti-aliased art has many rare blend colours.
- **Decision:** pixel art = (k ≥ 2 and palette-bounded), or (k = 1 and ≤ 64 colours and hard-edge share ≥ 0.98 and max edge ≤ 1024).

The thresholds go into `PreviewOptions`, replacing `PixelArtMaxEdge` and `PixelArtMaxColors` (the settings form gets new fields, and old values are ignored). `ImageMetadata` gains `PixelScale`. When a result is detected, the style "pixel art" is added and the tag is added with origin AI, unless the style was edited (`EditedFields.Style`). An AI `PixelArt = true` with a measured negative is ignored. A measured positive stands even if the AI disagrees.

### D5. Frame viewer and frame export
- **Viewer:** the frame viewer uses the stored thumbnail. Frame rectangles are scaled by thumbnail size ÷ original size. Pixel art is drawn with `image-rendering: pixelated` in a canvas (`spriteViewer.js`), and the row or range plays at 1-30 fps.
- **Thumbnail resolution:** when a sheet's thumbnail is smaller than 64 px per cell, a second preview `PreviewKind.SpriteSheet` is stored at a resolution that keeps ≥ 64 px per cell (capped at 4096 px), so frames stay legible offline. This is a new enum value, and its size is bounded by `ImageMaxEdge`-style options.
- **Export:** frame export resolves the original through `AssetExporter.ResolvePrimaryAsync`, crops with Magick.NET to PNG, and builds the animated WebP with the existing `AnimatedWebp` helper.

### D6. Search by image: describe, embed, re-rank
There is no image-embedding model.
1. The query image is resized to ≤ 768 px and sent once to the chat model with an item-style prompt ("describe this as a game asset; tags; style; colours"). This reuses `StructuredChat` and `ItemResult`.
2. The description, tags and type are formatted with the same `Documents` builder as asset embeddings and embedded with `EmbeddingService`. The vector search runs with the other filters (`SearchQuery.ImageQueryId`).
3. The top 200 are re-ranked by `0.8 · cosine + 0.2 · palette similarity`. Palette similarity is computed from `Asset.Colors` against the query's `ImageHandler.Palette`, as a weighted nearest-colour distance in Lab.

The query image and its vector are held in an in-memory `ImageQueryCache` (LRU of 8), keyed by an id in the URL token, and never written to the catalog. After a restart, the token degrades gracefully to "query image expired". The whole query costs one chat request (about 1-3 s on GPU) and is shown with a spinner.

Drop routing: `fileDrop.js` checks the drop target. Dropping on the search bar's image zone, or pasting an image while Browse has focus, goes to image search. A drop anywhere else stays an import.

- **Rejected: CLIP/SigLIP through ONNX Runtime.** It adds a new native runtime, a second model download and a second vector index. It would be better for pure visual similarity, so it can be revisited later.

### D7. Drag out: native OLE drag started from the host
HTML5 `dragstart` in WebView2 cannot put real files (`CF_HDROP`) on the drag, so the drag is done natively:
1. On `pointerdown` plus movement past the drag threshold on a selected card, `browse.js` cancels the HTML drag and calls `.NET DragOut.StartAsync(assetIds)`.
2. The host resolves the files with `ExtractForClipboardAsync`, which already extracts containers into a temp folder. A small "Preparing N files…" overlay shows when this takes longer than 150 ms. Unavailable assets are reported in a snackbar.
3. `IDragOutService` (new platform service) builds a shell data object with `SHCreateDataObject` from the PIDLs of the files, and calls `DoDragDrop` on the UI thread with `DROPEFFECT_COPY`. The mouse button is still held, so the OS drag continues from the cursor.

On the web host, `UnsupportedPlatformServices` returns "not supported", and the card falls back to the normal HTML drag (no files).

- **Risk:** this has to be proven first (task 1.1). If `DoDragDrop` cannot take over reliably after the WebView consumed `pointerdown`, the fallback is a dedicated drag handle button on the card or the selection bar. Pressing it starts the native drag directly, and the user can still drop into other apps.

### D8. Unity project export
- **Detection:** walk up from the target folder looking for a folder with both `Assets/` and `ProjectSettings/`. That sets the option's default.
- **`.meta` source:**
  - for package sources, the cached extraction already has `<file>.meta`
  - for Unity project folder sources, `<file>.meta` is next to the original

  The exporter copies it next to each written file and keeps the `.meta` name in step with any rename from conflict handling.
- **References:**
  - a per-source GUID → `AssetFile` map comes from `AssetFile.UnityGuid` (packages) or is parsed from `.meta` files on demand (folders)
  - the YAML of `.mat`, `.prefab`, `.asset`, `.controller`, `.overrideController` and `.anim` files is scanned for `guid: <32 hex>`, and the result is closed transitively over the map
  - GUIDs that are not in the source, such as built-in resources (`0000000000000000e000000000000000`), are ignored
  - the referenced files are added to the plan as extra entries, marked "dependency" in the plan dialog
- **GUID conflicts:** before writing, read the `guid:` line of every `.meta` under the target project's `Assets/` (cached per export) and list matches. "Skip conflicting" leaves those files out. "Continue" writes them, and Unity will then reassign one side.

### D9. Credits report and license audit
- **`CreditsReport.Build(assets)`:** groups by source, then by the effective license. Unknown licenses go first. It renders Markdown, plain text or CSV (one row per asset). It is reachable from the bulk bar, the Collections page and Browse's "…" menu, and saves with the folder picker. "Author/publisher" means `Source.Store.Publisher`, or else the attribution text's name, or else "unknown".
- **License audit:**
  - `CatalogStatsService.LicenseAuditAsync()` runs four grouped SQL queries over sources, with asset counts
  - "overrides differ" compares the override's kind, commercial and attribution fields with the source's
  - the dashboard gets an audit card, and the audit list is a tab on the Sources page with a problem filter
  - drill-down uses `SearchQuery { SourceId }` plus a license flag

### D10. Favourites, ratings, notes, keyboard
- **Fields:** `Asset.Favorite` (bool, indexed), `Asset.Rating` (tinyint, indexed) and `Asset.Notes` (text). These are user-owned columns that no AI or import code writes. Re-import and source updates copy them over from the replaced asset row, the same as user edits are kept today.
- **Search index:** the FTS table gains a `notes` column, so the migration drops and recreates `asset_fts` and the indexer rebuilds it.
- **Filters:** `SearchQuery` gains `FavoritesOnly`, `MinRating`, `HasNotes` and `GridUncertain`.
- **Keyboard:** one `keydown` handler in `browse.js`, scoped to the Browse page, ignores events whose target is an input, textarea or contenteditable. It keeps a focused index, uses `scrollIntoView({block:'nearest'})` with the virtualized grid, and calls into .NET for actions. The `?` dialog is a MudDialog listing the shortcuts from a single table that the handler also uses.

### D11. Library sync
- **Rescan job (`RescanJob`):** reuses `FolderIgnoreRules`, the classification and `Fingerprint`.
  - **Folders:** enumerate, apply the rules, and diff against the source's `AssetFile.RelativePath` set (primary and companion), by size and mtime from `Location`.
  - **Archives and packages:** compare `Source.Fingerprint`.

  Results go into a transient `RescanResult` per source in a job result, not in the catalog. The Sources page shows them with actions that call the existing staging (`ImportStager` with a target source id), the re-import, `DeletionService` and the replace flow.
- **Library folders:** `AppSettings.LibraryFolders` (paths with a volume id, like `Location`, so drive letters can change). Scanning lists immediate children that are not a `Source.Location` of any source. Version candidates are found by the existing `SourceVersions` matcher on name, store id or a cheap listing.
- **Startup:** if `CheckForChangesAtStartup` is on, both scans are queued 30 s after startup at low priority.

### D12. Catalog backup
- **Backup:** `CatalogBackupService` uses `SqliteConnection.BackupDatabase` (the online backup API) from a dedicated read connection into `<backupFolder>/<catalog>-yyyyMMdd-HHmmss[-manual].amdb.tmp`, then renames it. This gives a consistent snapshot while writers run, and a crash leaves no partial `.amdb`.
- **Space:** before starting, the free space on the target drive is checked against the catalog file size plus the WAL size.
- **Retention:** applies to `-auto` files only.
- **Restore:** cancel jobs, then make a manual backup of the current catalog, then close it (`CatalogStore` already supports switching catalogs through `CatalogLocationService`), then copy the backup over the catalog path and delete `-wal`/`-shm`, then reopen. Migrations run as usual if the backup is older.
- **Settings:** `BackupFolder`, `BackupSchedule` and `BackupKeep` live in `AppSettings`. The startup check runs after the catalog opens, as a background job.

### D13. Update check
- **Request:** `UpdateChecker` sends `GET https://api.github.com/repos/giollord/asset-manager/releases/latest` with `User-Agent: AssetManager/<version>` (no other headers or identifiers), a 10 s timeout, and parses `tag_name` and `html_url`. It compares the version with `AppVersion.Current` using `Version`.
- **State:** `UpdateCheckEnabled` (default false), `LastUpdateCheck`, `LastSeenVersion` and `LastCheckFailed` are stored in `AppSettings`, so the notification shows once per version.
- **When:** at most once per 24 h, at startup, only when enabled, plus "Check now" in About. Debug builds and dev hosts skip it.

### D15. Suggested tag vocabulary
- **Storage:** the list belongs to the catalog, since it shapes the catalog's tags, so it is stored with the other catalog settings (`CatalogSettingsService`) as `SuggestedTags: string[]`. A missing value means "use the built-in list" (`SuggestedTags.Default` in Core). Existing catalogs get the default without a migration, and "reset" only clears the stored value.
- **Prompts:** both the item and the pack prompts get one line:
  > Preferred tags (use these exact words whenever they apply, instead of synonyms; add other tags as needed): 2d, 3d, pixel art, …

  This costs about 60 tokens per request.
- **Normalization:** after the AI answers, tags pass through `AiText.NormalizeTags`, then through a small synonym fold. Each suggested tag is compared with the answer's tags with spaces, hyphens and underscores removed, so "lowpoly", "low-poly" and "low_poly" become "low poly". This catches a model that ignores the instruction.
- **Rejected:** a hard closed vocabulary. It would lose specific tags like "barrel" or "forest" that make search useful.
- **Rejected:** per-type vocabularies. A single generic list is enough, and the AI already sees the type.

### D16. Whole-card click and no card text selection
- **Today:** `AssetCard` puts `@onclick`/`@ondblclick` only on the preview `div`. The name line has the `selectable` class (app.css opts it into `user-select: text`), so clicking it does nothing and dragging highlights it.
- **Change:** move the click and double-click handlers to the card's root `MudPaper`. The checkbox and the overlay already stop propagation (the overlay `div` has `@onclick:stopPropagation`); the checkbox gets the same treatment so a checkbox click is not also a card click. Drop the `selectable` class from `asset-card-name` and add `.asset-card { user-select: none; }` so badges and the type line can never opt back in. The details drawer keeps `selectable` on its name, which already satisfies "Copy an asset name".
- `AssetCard` is shared with the import review grid, so that grid gets the same whole-card behaviour, which is wanted there too.

### D17. Card context menu
- **Trigger:** `@oncontextmenu` with `:preventDefault` on the card root (this also suppresses WebView2's default menu there only). The keyboard handler from D10 maps `ContextMenu` and Shift+F10 on the focused card to the same call, anchored at the card's bounding box.
- **Component:** one `AssetContextMenu` instance on the Browse page, not one per card (the grid is virtualized and can hold hundreds of cards). It is a `MudPopover` positioned at the pointer (or card) coordinates, holding a `MudList`/`MudMenuItem`-style list with `Icon` on every item: `OpenInNew` "Open details", `ImageSearch` "Find similar", `ContentCopy` "Copy name". It closes on Esc, outside click, scroll and item choice.
- **Find similar:** same action as the removed overlay button (`SetQuery(new SearchQuery { SimilarTo = id })`). It is enabled when `AiStatus == Enriched` and `CatalogSearch.SemanticAvailable`; otherwise disabled with the reason as secondary text ("Needs AI enrichment" / "Needs the embedding model").
- **Copy name:** `navigator.clipboard.writeText` from `browse.js` (both hosts serve over a secure origin, so the async clipboard API is available), followed by a "Copied" snackbar. No new platform service is needed; `IClipboardFileService` stays files-only.
- **Selection:** the menu takes the asset id it was opened for and never touches `_selected`. Bulk actions stay in the `BulkActionBar`.
- `Overlay(a)` keeps only the audio play button.

### D18. Turntable of stored renders
- **Data:** `PreviewQueries.ModelViewsAsync` already returns `ModelView` rows ordered by `Index`, where `Index = ring * azimuths + azimuth` (`PreviewKind` doc). It drops the index, so a new `ModelTurntableAsync(assetId)` returns `{ Rings, Azimuths, Primary, Views[] }`: `Rings`/`Azimuths` come from the current `PreviewOptions` when the stored count equals `ViewCount` and the indices are dense; otherwise one ring of all views (spec scenario "Renders from older settings"). `Primary` is `PrimaryViewIndex` clamped to the range. Animations keep their single animated preview and do not use the turntable.
- **Component:** `ModelTurntable.razor` + `turntable.js`. The .NET side passes the data URLs once; JS creates all `<img>` elements up front (hidden, decoded via `img.decode()`) and toggles visibility, so a step is a class change with no reload or Blazor round trip.
- **Drag mapping:** pointer capture on `pointerdown`. Horizontal: one azimuth step per `width / azimuths` px, so a drag across the full width is one turn; the step direction is chosen so the model turns with the pointer (checked against `ModelRendering`'s azimuth order during implementation). Vertical: one ring step per `height / (rings + 1)` px, clamped. Arrow keys step by one when the element has focus. Cursor `grab`/`grabbing`; `touch-action: none`.
- **Where it is used:**
  - details drawer, "Renders" mode: replaces the single `<img>` and the `#details-views` thumbnail strip for models
  - `ModelViewer`: on `OriginalUnavailableException` it renders `ModelTurntable` with a small "Original not available — showing stored renders" note instead of the error text. This covers both the drawer's "3D view" and the `OriginalViewer` dialog, which reuses `ModelViewer`.
- **Orientation edits:** while the user changes the rotation in the drawer, the live `ModelViewer` is shown as today; with the drive detached the existing message about re-rendering applies, since the stored renders do not reflect an unsaved rotation.
- **Rejected:** interpolating or cross-fading between renders. It blurs pixel-exact renders and does not make 8 azimuths look smoother; the "bad fps" look is intended.

### D19. README screenshots
- Captured from the Debug MAUI app with `node tools/cdp.mjs shot` (WebView2 content at a fixed 1440×900 window), dark theme, over a catalog built only from CC0 packs (e.g. Kenney) from the sample library, so the images can be redistributed with the repository.
- Files: `docs/screenshots/browse.png`, `details-model.png`, `dashboard.png`, `import-review.png`, optimized PNGs under ~400 KB each. The README gets a "Screenshots" block right after the intro paragraphs: the Browse shot full-width, the other three smaller below it, each with alt text. The logo line stays as it is.
- Taken after the Browse and viewer changes above, so they show the final UI.

### D14. Migration and order of work
One EF migration (`AddWorkflowFeatures`) covers:
- `Favorite`, `Rating` and `Notes`
- `SpriteSheet` (owned JSON like the other metadata), `GridUncertain`, and `PixelScale` inside the `Image` JSON
- `SourceTags`, `AssetTagOptOuts`
- the `asset_fts` rebuild

`TagOrigin.Source` and `PreviewKind.SpriteSheet` are appended enum values (stored as ints, so this is safe). Existing pixel-art flags are recomputed lazily by "regenerate previews". A one-off maintenance action "re-detect sprite sheets and pixel art" is added to Maintenance settings, so existing catalogs get the new detection without a full re-import.

## Risks / Trade-offs

- **Native drag-out from WebView2 may not start reliably mid-gesture.** → Mitigation: spike first (task 1.1). The fallback is a drag-handle button (D7).
- **The grid detector can be fooled** by irregular atlases or by sheets with decorative borders. → Mitigation: slicing data wins, a confidence threshold applies, there is a "grid uncertain" filter for review, and user edits are final.
- **The consensus step adds AI time per source.** → Mitigation: one request per source, only when there are candidates. It can be turned off.
- **Tags added by consensus could feel heavy-handed.** → Mitigation: the AI only adds, per-asset opt-out sticks, and source tags are visible and removable on the Sources page.
- **Image search quality depends on describing the image well**, so it finds "same kind of thing" more than "same pixels". → Mitigation: palette re-rank. CLIP remains a possible later upgrade (D6).
- **Unity GUID conflicts** if the user continues past the warning. → Mitigation: an explicit warning listing the files, with "skip" as the default.
- **Backups of large catalogs** (previews are stored inside) take time and disk space. → Mitigation: a background job, a free-space check, retention, and a size shown in Settings.
- **One large change.** → Mitigation: the task groups are independent after the shared migration (group 2), so they can be implemented and verified one feature at a time.
- **Whole-card clicks could swallow clicks meant for controls on the card** (checkbox, play). → Mitigation: those controls stop propagation, covered by bUnit tests.
- **Turntable with few azimuths looks jumpy.** → Accepted: it is a fallback for detached drives; the default 8 azimuths give 45° steps, and more can be set in preview settings.
- **The update check uses the network.** → Mitigation: off by default, no identifiers sent, and the app-shell "Fully offline" requirement is updated to state the exception.

## Migration Plan

- The new migration runs on first open. `CatalogStore` already writes a `.bak` before migrating.
- The FTS rebuild runs inside the migration's follow-up reindex, which takes seconds for 200k assets.
- Rollback: an older app version refuses a catalog with unknown migrations, so to go back, users restore the `.bak` or a backup made by the new backup feature.

## Open Questions

- The default grid-confidence threshold (0.6) and the pixel-art thresholds should be tuned on the sample library (`ASSETMANAGER_SAMPLES`) during implementation. The defaults are configurable, so tuning changes no spec.
