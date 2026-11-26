# Proposal

## Why

The catalog can find and preview assets well. It is weaker at the steps around that:
- choosing between candidates (no ratings or personal notes)
- shipping them legally (no credits file, no license audit)
- getting them into a project (no drag-out, `.unitypackage` exports lose their `.meta` GUIDs, sprite sheets are treated as single images)
- keeping the catalog current and safe (no rescans of imported sources, no backups)

The AI also tags each asset on its own. When most of a pack is clearly "pixel art" or "low poly", that fact never reaches the rest of the pack. Sprite sheets are only told apart from images by the AI looking at a thumbnail, which is unreliable for grid layout. This change adds those everyday workflow features as one batch.

## What Changes

**Finding and judging**
- **Search by image:** drop or paste an image (or pick one from the clipboard) to find assets that look like it. The AI describes the image, the description is embedded like an asset's, and results are re-ranked by palette similarity.
- **Favourites, ratings, notes:** a per-asset favourite flag, a 0-5 star rating and free-text notes. They are searchable and filterable, can be set in bulk, and AI enrichment never touches them.
- **Keyboard navigation:** arrow keys move through the Browse grid and Shift+arrows extend the selection. Enter opens the details and Esc closes them. Space plays audio or turns a model. `/` focuses search, `F` toggles favourite, `0`-`5` set the rating, Delete starts the bulk delete with its confirmation, and `?` shows the shortcut list.

**Licensing**
- **Credits report:** export Markdown, plain text or CSV credits for a selection, a collection or the current search, grouped by source. It lists the license, attribution text, author or publisher, and links. Assets with an unknown license are listed first.
- **License audit:** a dashboard and Sources view of sources that need attention: unknown license, attribution required but no attribution text, non-commercial, or a per-asset override that differs from its source. Each opens the matching filter or the source for editing.

**Getting assets out**
- **Drag out:** drag one or more selected assets from the grid into Explorer, Unity, Godot, Blender or any app that accepts dropped files. Packed files are extracted to the cache first.
- **Unity project export:** when the target folder is inside a Unity project, or the user turns on the option, the export writes each file's original `.meta` next to it, so GUIDs and import settings survive. It can also add the files the selected assets reference by GUID (materials → textures, prefabs → meshes and materials) from the same source. It warns when a GUID already exists in the target project.
- **Sprite sheets and pixel art:**
  - Sprite sheets are detected as a type separate from images, and pixel-art style is detected alongside it, so one asset can be both a sprite sheet and pixel art.
  - The grid (cell size, offset, spacing, frame count) is detected from slicing data when it exists: a Unity `.meta` with multiple sprites, or a TexturePacker or Aseprite JSON or XML next to the image. Otherwise an algorithm finds it, with the AI's answer used only as a hint that the algorithm checks.
  - The user can review and correct the grid.
  - The details show the frames and play rows as animations, and single frames or a row as an animated WebP can be exported.
  - Pixel-art detection moves from "small or few colours" to a measured check: nearest-neighbour pixel scale, hard edges and a limited palette.

**Pack-wide tags and consistent tagging**
- **Suggested tag vocabulary:** a list of generic tags shown to the AI in item and pack requests, so the same concept always gets the same tag instead of a near-synonym ("low poly", not "lowpoly"). It covers dimension, style, view, kind and technical tags. New catalogs start with a built-in list, which can be edited in Settings → AI. Consistent tags also make the tag consensus below more reliable.
- **Source tags:** a tag can belong to a whole source. Every asset of the source carries it (origin "source"), and so do assets added to the source later.
- **AI tag consensus:** after the item pass of a source, tags and styles that a large share of its assets have in common (e.g. 80% "pixel art", 70% "low poly") are given to the AI with counts and samples. The AI decides which ones describe the whole source. The ones it accepts become source tags. Thresholds can be set in Settings → AI.

**Keeping the catalog current and safe**
- **Rescan sources:** check imported sources for changes:
  - new, changed or removed files in folder sources
  - a changed fingerprint for archive and package sources
  - **library folders** the user registers, scanned for packs not yet imported

  Findings are offered as staged imports or updates; nothing changes without confirmation. Runs on demand and, optionally, at startup.
- **Catalog backup and restore:**
  - consistent backups of the catalog file to a backup folder, on demand and on a schedule (default daily at startup), keeping the last N (default 5)
  - restore from Settings, which keeps a copy of the current catalog first
- **Update check:** an opt-in check of GitHub Releases. It is off by default and is the only other network access besides model downloads. About shows when a newer version exists and links to its release page. Nothing is downloaded or installed.

**Textured model previews**
- **Texture matching:** many packs ship a model's textures under other names or in another folder, and the model's materials don't reference them (or reference paths that don't exist). Such models are rendered untextured today. After the existing matching (Unity `.mat` references, material paths, same file name), the source's remaining textures are shortlisted by name, folder and kind. Stripped name prefixes and suffixes (`SK_Orc` ↔ `T_Orc_Albedo`) decide on their own when they are exact. Otherwise the AI sees the model rendered with each candidate and picks the one that fits, or none. Confident matches are attached, marked "matched by AI", and the previews are re-rendered with them. Less confident ones are offered as suggestions. The user can apply, detach or choose a texture in the details drawer.

**Browse and viewer polish**
- **Click anywhere on a tile:** today only the preview area of a Browse card reacts to clicks; the name and type line below it do nothing, and the name text gets highlighted when the user clicks or drags over it. A click anywhere on the card selects it (with Ctrl/Shift as today), and a double click anywhere opens it. Text on cards is no longer selectable. Text in the details panel stays selectable.
- **Card context menu:** the "Find similar" icon button on the card's preview goes away. Right-clicking a card (or pressing the context-menu key or Shift+F10 on the focused card) opens a menu with icons: "Open details", "Find similar" and "Copy name".
- **Turntable for stored renders:** when a model's original is not available (drive detached, not stored), the details panel and the original viewer show its stored multi-angle renders as a turntable: dragging left and right turns the model through the azimuths, dragging up and down moves between elevation rings. It looks like a 3D view at a very low frame rate, instead of an image with a strip of thumbnails.

**README**
- A few screenshots of the app (Browse, asset details, Dashboard, import review) in the README, under the intro. The logo at the top of the README stays as it is.

## Capabilities

### New Capabilities
- `sprite-sheets`: sprite sheet and pixel-art detection, grid detection (from slicing data, by algorithm, with an AI hint) and correction, frame preview and animation, frame export.
- `catalog-backup`: scheduled and on-demand catalog backups, retention, restore.
- `library-sync`: rescanning imported sources for changes, and library folders for packs not yet imported.

### Modified Capabilities
- `asset-catalog`: source-level tags and the "source" tag origin; favourite, rating and notes on assets.
- `ai-enrichment`: tag consensus across a source; suggested tag vocabulary; sprite sheet and pixel-art hints in item output; picking a model's texture from rendered candidates.
- `asset-search`: search by image; filters for favourites, minimum rating and notes; "find similar" moves to the card context menu; turntable inspection of stored model renders.
- `asset-previews`: models get textures matched from their source when their materials reference none or missing ones; the user can review, change and detach them.
- `asset-export`: drag out, Unity-aware export with `.meta` files and dependencies, credits report; the original viewer falls back to the turntable when a model's original is unavailable.
- `catalog-stats`: license audit view.
- `app-shell`: keyboard navigation; the "fully offline" rule allows the opt-in update check; whole-card click selection; card text not selectable; card context menu.
- `app-distribution`: update check against GitHub Releases; screenshots in the README.

## Impact

- **Data:** EF Core migration adding:
  - asset `Favorite`, `Rating` and `Notes`
  - `SpriteSheetMetadata` and a pixel scale on `ImageMetadata`
  - a `SourceTag` table and `TagOrigin.Source`
  - an image fingerprint for palette re-ranking
  - an origin on companion files (import, name match, AI, user) and stored texture suggestions

  The notes go into the full-text index.
- **Processing:** new sprite-sheet grid detector and pixel-art analyser (Magick.NET), slicing readers for Unity `.meta`, TexturePacker and Aseprite, Unity dependency resolution from YAML GUID references, rescan job.
- **AI:** consensus pass after the item pass, image-query description, sprite-sheet hint in the item schema, texture choice from candidate renders before a model's item pass.
- **UI:**
  - Browse (keyboard, drag source, image drop target, rating and favourite)
  - details drawer (frames viewer, notes, rating, model textures)
  - Sources (source tags, audit, rescan)
  - Dashboard (audit tiles)
  - Settings (backup, library folders, consensus thresholds, update check)
  - Export dialog (Unity options), credits export
  - `AssetCard` (whole-card click, no text selection, context menu) and a new turntable viewer used by the details drawer and `ModelViewer`
- **Docs:** README screenshots in `docs/screenshots/`.
- **Platform:** native OLE drag-and-drop from the MAUI Windows window, Unsupported on the web dev host.
- **Network:** one optional HTTPS GET to the GitHub API, only when the user turns it on.
- **Specs:** several existing specs change, as listed above.
