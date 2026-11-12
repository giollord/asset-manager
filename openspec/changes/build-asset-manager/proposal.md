# Proposal

## Why

The asset library on the external drive (`E:\Assets`) holds 150k+ files: loose PNG/WAV/OGG/FBX files, zip/rar/7z archives (sometimes nested), and 200+ `.unitypackage` files. There is no way to search it, see what's inside packages, or remember licenses and source links. Existing tools either live inside the Unity editor, copy files into their own library, or can't run AI categorization fully offline. The repo now holds a fresh .NET 10 MAUI Blazor Hybrid template, so this change turns it into a local-first, offline game asset manager with an embedded multimodal AI.

## What Changes

- Add a single-file catalog database. It stores the assets, their metadata, tags, collections, licenses, import sources, the previews (as blobs) and the vector embeddings.
- Add asset registration from files, folders, archives (zip/rar/7z/tar/gz) and `.unitypackage`, including archives nested in archives and packages nested in packages. Remember each asset's provenance chain (drive → path → archive entry → …).
- Keep the catalog fully usable while the external drive is detached. Previews, metadata and search all work. Opening, copying or extracting originals reports that the drive is unavailable.
- Add optional storage of originals in the catalog. A small, unobtrusive action, available for one asset or many selected assets, can store an asset's original files inside the catalog DB or remove them again. A stored asset can be opened, copied and extracted without the drive.
- Generate previews when importing:
  - 3D models: multi-angle textured renders on a transparent background.
  - Images: lossy WebP thumbnails, or the original when it is already small.
  - Audio: a lossy clip of at most 10 seconds.
- Add an embedded local AI. It is a small multimodal model (Gemma 4 E4B class) that runs in a bundled native runtime, so no Python is needed. It writes English descriptions, tags, color and style info, reads license and link documents (txt/html/pdf/url), notes scripts, and describes collections.
- Add a staged import workflow:
  - Choose the sources, a shared tag and an instruction for the AI.
  - Review a preview of the import and ask the AI to fix selected items.
  - Confirm the import.
- Add package re-import: when a newer version of a source (for example a renamed `.unitypackage`) is imported, its assets are updated, added or removed.
- Add search by asset type, free text, included and excluded tags, and semantic (vector) similarity.
- Add bulk operations: delete or re-import selected assets.
- Add asset export: open the original, copy an asset or a group of files (for example a model and its textures) to the clipboard as files, or extract them to a chosen folder.
- Add an app shell built on a popular Blazor component library (MudBlazor), using only its stock styling, with a dark/light theme switch. All UI assets are bundled locally, with no CDN.
- Add a stats dashboard as the start page. It shows the total, per-type, per-collection, per-source and per-tag counts, plus enrichment, availability, license and model-issue breakdowns. The breakdowns are clickable and open the matching filtered search.
- Add job progress for every long-running operation (import, re-import, source update, AI enrichment, preview regeneration, store, export). Each job shows its stage, done/total, ETA and errors. There is an AI queue overview with pause and resume, an app-bar indicator, completion notifications, retry of failed items, and a job history.
- Remove the template's demo pages (Counter, Weather).

## Capabilities

### New Capabilities
- `asset-catalog`: Single-file database, the asset/file/source data model, per-asset metadata, tags, tag-based collections, license and link info, and bulk delete.
- `source-access`: Reading assets from folders, archives, nested archives and `.unitypackage` files. Drive and volume identity, availability detection while the drive is detached, optionally storing originals inside the catalog, and the temporary extraction cache.
- `asset-previews`: Generating and storing previews for 3D models, images and textures, and audio.
- `ai-enrichment`: The embedded local multimodal model runtime, per-asset description/tag/style generation, reading license and link documents, collection descriptions, and text embeddings.
- `asset-import`: The staged import session (shared tags, AI instructions, review and AI corrections, confirmation), re-import, and updating sources from a newer package version.
- `asset-search`: Filtering by type, text and included/excluded tags, plus vector similarity search.
- `asset-export`: Opening originals, copying files to the clipboard, and extracting to a folder.
- `app-shell`: The app layout and navigation, the component library, the dark/light theme, offline-only static assets, and settings.
- `catalog-stats`: The dashboard with catalog totals and breakdowns, the collection, source and tag counts, and search result counts.
- `job-progress`: Progress, ETA, errors, pause/resume/cancel, notifications and history for all long-running operations, including the AI queue.

### Modified Capabilities
<!-- None: no existing specs in openspec/specs/. -->

## Impact

- **Code:** The MAUI host (`AssetManager`), the shared Razor UI (`AssetManager.Shared`) and the web host (`AssetManager.Web`) all change. New class libraries hold the core domain and services, which keeps logic out of the UI and lets both hosts reuse it.
- **Platform:** Windows desktop is the primary target, because of external-drive volume IDs, the clipboard file-drop list and the native AI runtime binaries. The Android and iOS targets are dropped from the build. macOS is left possible behind platform interfaces.
- **Dependencies (NuGet and bundled native):**
  - MudBlazor
  - Microsoft.Data.Sqlite / EF Core SQLite with FTS5, plus the sqlite-vec extension
  - SharpCompress
  - Magick.NET
  - AssimpNetter (maintained AssimpNet fork, Assimp 6.x)
  - FFmpeg (FFMpegCore plus bundled binaries)
  - PdfPig
  - AngleSharp
  - YamlDotNet
  - Microsoft.Extensions.AI
  - llama.cpp `llama-server` binaries
  - three.js, bundled locally for 3D rendering in the WebView
- **Data:** Model weights (GGUF, several GB) live outside the DB in the app data folder. The catalog DB grows with the preview blobs, which is about tens of KB per asset. It also grows by the full size of any originals the user chooses to store.
- **Out of scope:** Unreal/Fab packages, asset versioning or history, multi-user or network sharing, and editing asset files.
