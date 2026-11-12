# Design

## Context

- The repo is the stock .NET 10 "MAUI Blazor Hybrid and Web App" template:
  - `AssetManager` is the MAUI host, targeting Android, iOS, MacCatalyst and Windows.
  - `AssetManager.Shared` is the Razor UI, with the demo pages Counter and Weather.
  - `AssetManager.Web` is a Blazor Server host.
  - There is no domain code yet, and no specs.
- The sample library at `E:\Assets` has roughly:
  - 118k PNG files, 35k WAV/OGG/MP3 files and 1.4k FBX files
  - about 1k zip files, plus some rar and 7z archives
  - 219 `.unitypackage` files
  - `License.txt` files, a `license.pdf`, HTML files and `.url` shortcuts
  - archives nested in archives, Asset Inventory backup zips (which are not assets), and PSD/AI/EPS sources
- A `.unitypackage` is a gzip-compressed tar archive. It has one folder per GUID, each holding `asset`, `asset.meta`, `pathname` and an optional `preview.png`. Packages downloaded from the Asset Store carry JSON metadata (link id, title, publisher, version) in the gzip header's FEXTRA field. The user's own [unity-package-extractor](https://github.com/giollord/unity-package-extractor) is a reference implementation.
- Gemma 4 E4B is supported by llama.cpp (PR #21309, release b8637 and later) for text and vision, through a GGUF model file plus an `mmproj` projector file. Audio input for E4B in llama.cpp is not yet reliable (issue #21325).

## Goals / Non-Goals

**Goals:**
- Keep the core logic separate from the UI and the host, so that every capability is a DI-registered service that both hosts reuse.
- Use one extensible pipeline: container reader → asset handlers (one per type) → enrichment. A new file type is one new handler class, and no logic is copied between types.
- Make imports scale to 100k+ files with bounded memory, resumable jobs and batched AI work.

**Non-Goals:**
- Mobile targets. Android and iOS are dropped, and macOS is not built or tested in v1.
- Native `.blend` / `.max` rendering. These files are cataloged with placeholder or embedded previews only (see Open Questions).
- Image-embedding (CLIP) similarity. Semantic search is text-embedding based, over AI descriptions.
- Godot-specific package parsing. Godot asset-library zips are handled as ordinary archives.

## Decisions

### D1. Solution layout
- `AssetManager.Core` (net10.0): Domain entities, service interfaces, the import pipeline and the handler abstractions. It has no UI and no platform code.
- `AssetManager.Data` (net10.0): The SQLite catalog. It holds the EF Core DbContext and migrations, the FTS5 and vector tables, and the blob stores.
- `AssetManager.Processing` (net10.0): Container readers, type handlers, metadata extraction, previews and the FFmpeg and Magick integrations.
- `AssetManager.AI` (net10.0): The llama-server host, the `IChatClient` and embedding clients, prompts and schemas, and the enrichment and document-extraction services.
- `AssetManager.Shared` (Razor): All pages and components (MudBlazor), plus the JS modules for the 3D renderer and viewer.
- `AssetManager` (MAUI, `net10.0-windows10.0.19041.0` only): Platform services for the clipboard file list, pickers, volume identity and shell open/reveal.
- `AssetManager.Web` stays as a development host. It runs in the browser with hot reload and uses stub platform services for clipboard and reveal, which report "not supported".
- One `services.AddAssetManager(config)` extension in Core and Processing wires everything, and both hosts call it.
- *Alternative:* a single project. Rejected, because it mixes platform code with domain code and blocks reuse by the Web host.

### D2. Database: SQLite in a single file
- SQLite runs through EF Core (Microsoft.EntityFrameworkCore.Sqlite) in WAL mode with `auto_vacuum=INCREMENTAL`.
- FTS5 virtual table `asset_fts` covers name, file names, paths, description, tags and source name. Triggers or a sync service keep it current, and it uses `prefix='2 3'` so prefix queries work.
- **Vectors:** The `sqlite-vec` extension provides a `vec0` virtual table of 768-d float32 vectors. They are truncated to 256-d via Matryoshka to save space, plus cosine KNN. *Fallback* if the native extension fails to load: brute-force cosine over int8-quantized vectors cached in memory. This is about 50 MB per 200k assets at 256-d.
- **Blobs:**
  - `PreviewBlob(asset_id, kind, index, mime, bytes)` holds the previews.
  - `StoredFileChunk(stored_file_id, seq, bytes)` holds stored originals in 8 MB chunks. This stays under SQLite's blob limits and allows streaming.
  - Removing stored copies runs `PRAGMA incremental_vacuum`. A "Compact catalog" action runs a full `VACUUM`.
- **Staging:** Import sessions live in the same DB, in the `ImportSession`, `StagedItem` and `StagedPreview` tables. This preserves the single-file rule, and staging survives restarts.
- **Settings:** Settings tied to a catalog live in the DB. App-level settings (the catalog path, the theme and the model paths) live in a small JSON file under `%LOCALAPPDATA%\AssetManager`, because the catalog path cannot live inside the catalog.
- *Alternative considered:* LiteDB, which is a single-file NoSQL store with built-in file storage. Rejected because it has no full-text search and no vector index, and it is weaker at 200k+ documents with large blobs.

### D3. Core data model (simplified)
- `Volume`: the volume serial, label and last-seen drive root.
- `Source`: The kind (file, folder, archive or unitypackage), the root `Location`, the display name, a fingerprint (size + mtime + head/tail hash), `parent_source_id` for nesting, the Asset Store metadata, notes, a `LicenseInfo` and `Links`.
- `Asset`: The type, name, description, style and colors, technical metadata (stored as typed JSON columns, one per type), the AI status, the user-edited field mask and the stored flag.
- `AssetFile`: The role (primary or companion), the `Location`, size, content hash, Unity GUID and the original relative path.
- `Location`: the volume id, the path on that volume, and a list of inner entry paths for nested containers. It is serialized as one string. Resolution is centralized in `ILocationResolver`.
- `Tag`, `AssetTag(origin = User | Import | AI)`, and `Collection(rule: include[], exclude[], description, description_origin)`. Membership is computed through a query, not stored, so it can never go stale.
- `LicenseInfo`: the kind, commercial (yes/no/unknown), attribution (yes/no/unknown), attribution text and the evidence document path. It sits on the Source, with an optional override on the Asset.

### D4. Container abstraction
- The core interface is `IContainerReader { bool CanRead(entry); IAsyncEnumerable<IVirtualEntry> Enumerate(...) }`. Its implementations are:
  - `FolderReader`.
  - `ArchiveReader`, built on SharpCompress. It covers zip, rar, 7z, tar and gz.
  - `UnityPackageReader`. This reader:
    - extracts the whole package once to the cache with SharpCompress (tar.gz)
    - maps each GUID folder to its `pathname`
    - exposes the GUID, the `.meta` file and the embedded `preview.png`
    - parses the gzip FEXTRA JSON with a small header reader, because SharpCompress does not expose that field
- `VirtualFileSystem` walks the tree recursively. When an entry is itself a container and the depth is under the limit (default 5), it extracts the entry to the cache and recurses.
- Every entry gets a `Location` with the full chain.
- **Extraction cache:** `%LOCALAPPDATA%\AssetManager\cache`, with content-addressed folders, LRU eviction by size and age, and a lock file for entries in use.
- **Volume identity (Windows):** The volume serial comes from `GetVolumeInformationW` through P/Invoke. Drive changes are detected by polling `DriveInfo.GetDrives()` every few seconds, plus an event raised to the UI. Availability is checked lazily per volume, not per file.

### D5. Type handlers
- Each asset type has one handler: `IAssetTypeHandler { Detect; ExtractMetadata; GeneratePreviews; BuildAiInput }`. The handlers are image/texture, model, audio, font, video, document and fallback.
- A grouping step runs before the handlers. `CompanionResolver` attaches textures to models in this order:
  1. Unity `.mat` references, by GUID, to `_MainTex`, `_BaseMap`, `_BumpMap` and so on. The `.mat` YAML is parsed with YamlDotNet after the `!u!` tags are stripped.
  2. Assimp material texture paths.
  3. File-name heuristics: the same base name, or suffixes such as `_albedo`, `_diffuse`, `_basecolor`, `_normal`.
- A texture claimed by a model becomes a companion and not its own asset.
- **Ignored or noted content:** Script and code extensions are configurable, by default `.cs .js .shader .hlsl .cginc .asmdef .dll .prefab .unity .asset` and others. They are collected into a per-source manifest that the AI summarizes into the source notes. Borderline items get a `ShouldImport` decision from the AI in the batch prompt, and the user can override it in the preview.

### D6. Previews
- **Images:** Magick.NET (Q8) decodes PNG, JPG, TGA, PSD (flattened), EXR (tone-mapped), DDS, SVG, GIF and others.
  - The preview is downscaled to a maximum edge of 512 and saved as lossy WebP at quality 80 with alpha.
  - The original is kept when it is at most 512 px and at most 64 KB, and is a PNG, JPG, GIF or WebP.
  - Pixel art (an edge of 128 px or less, or 64 colors or fewer) is scaled with point filtering.
  - The dominant palette comes from Magick quantization to 5 colors, and is stored as hex values plus named colors.
- **Settings:** All of the numbers in this section are defaults only. A single `PreviewOptions` record, stored as catalog settings and edited on the Settings page, is the only source that handlers read from. Values are validated with DataAnnotations: for example, the azimuth count must be 1–16, elevations must be within −89..89°, the clip length 1–60 s, and the quality 1–100. The record's version is stored per preview, so "regenerate outdated previews" can find the affected assets.
- **Audio:** FFmpeg is bundled as `ffmpeg.exe` and `ffprobe.exe` and driven through FFMpegCore.
  - The preview clip is the first 10 s, encoded as mono Opus in Ogg at 32–48 kbps. WebView2 plays it natively.
  - A waveform PNG (`showwavespic`) and a spectrogram (`showspectrumpic`) are generated and stored as WebP. The spectrogram is the AI's audio fallback.
  - Metadata comes from ffprobe.
- **3D:**
  - AssimpNetter (the maintained fork of AssimpNet, bundling Assimp 6.x) loads FBX, OBJ, DAE, glTF/GLB, 3DS, PLY, STL and other formats. It gives the stats (vertices, triangles, bones, animations) and exports to GLB in memory.
  - The renderer is three.js (bundled in `wwwroot/lib`, version pinned). It runs inside the app's own WebView in a hidden `PreviewRenderHost` component that MainLayout mounts once.
  - .NET streams the GLB plus the resolved texture bytes through `DotNetStreamReference` to a JS module.
  - The JS module applies textures by material name, frames the bounding sphere, and renders 3 elevations (−15°, 20°, 55°) × 8 azimuths at 384 px. It renders with `alpha: true` and a transparent clear color, then returns WebP blobs from `canvas.toBlob('image/webp', 0.8)`.
  - The same JS module powers the interactive "open original" viewer, through OrbitControls. One renderer serves both uses.
  - *Alternative:* native offscreen OpenGL (Silk.NET). Rejected because it needs a second rendering stack and per-platform context handling, while the WebView already provides WebGL.
- The Unity `preview.png` is used as the fallback for formats that cannot be rendered.
- **Scale analysis:** This runs in .NET, in the model handler.
  - Assimp is loaded *without* `GlobalScale`, so the authored transforms stay visible.
  - The handler reads the scene metadata: `UnitScaleFactor`, `UpAxis`, `UpAxisSign`, `FrontAxis` and `OriginalUpAxis`, which FBX and Collada files provide.
  - It also reads the root and first-level node scales, and computes the world-space AABB in metres.
  - The result is stored as `ModelTransformInfo`: the size in metres, the unit scale, the root scale, uniformity, and an anomaly value (None, TooSmall, TooLarge, NonUnitScale or NonUniform). The thresholds come from `PreviewOptions`, with defaults of 1 cm and 1 km.
  - Rendering always normalizes by the bounding sphere, so the scale never affects how the previews look.
- **Orientation:** The orientation is stored as one of the 24 axis-aligned rotations, together with how it was decided (Metadata, AI, User or None), a confidence, and a "verified" flag.
  1. **Metadata:** When the file declares an up-axis, a correction to Y-up is applied before the first render. This covers the typical Blender Z-up FBX.
  2. **Probe:** For models without usable metadata, or when the AI is enabled to double-check, the renderer produces one orientation probe image. It is a 3×2 contact sheet of the model under the 6 candidate up-axes (±X, ±Y, ±Z), each seen from the same three-quarter view and labeled 1–6. A heuristic pre-ranks the candidates: models are usually taller along up than the floor footprint, and the center of mass sits low. The pre-ranking goes into the prompt as a hint.
  3. **AI decision:** The AI returns `{ choice: 1..6 | "none", yaw_fix: 0|90|180|270, confidence }`. `yaw_fix` is optional, and turns the model so its front faces the default azimuth 0. A confident choice that differs from the current orientation queues a re-render job. The item enrichment for that model waits for the re-render, so the description is based on upright views.
  4. **User override:** Rotate ±90° buttons on X/Y/Z in the details drawer show a live preview in the viewer. Saving re-renders the model and sets `decided_by = User`, which the AI never overrides. This reuses the user-edited field mask from D3.
  - The correction is applied only as a root transform in three.js, for the previews and the in-app viewer. Original files are never rewritten, and export returns the original bytes.

### D7. AI runtime: bundled llama.cpp server
- **Runtime:**
  - `llama-server.exe` ships in two builds: Vulkan for GPU, and CPU. Both are pinned at release b8637 or later.
  - `LlamaServerHost` starts it on a free localhost port with `--mmproj`, binding only to 127.0.0.1.
  - The process is tied to the app lifetime through a Windows Job object, health-checked through `/health`, and restarted if it crashes.
  - The modalities it reports through `/props` set whether audio input is enabled.
- **Clients:** The client is `Microsoft.Extensions.AI` `IChatClient` through the OpenAI-compatible adapter. Prompts use `ChatResponseFormat.ForJsonSchema<T>()`, which llama.cpp enforces through grammar, and the output is validated before it is persisted.
- **Embeddings:** A second, lightweight llama-server instance runs with `--embedding`, using the EmbeddingGemma-300M GGUF. It is called through `IEmbeddingGenerator`.
- **Models:**
  - Settings → AI lets the user pick a local GGUF file plus its mmproj file. It also offers a one-click, user-initiated download from Hugging Face into `%LOCALAPPDATA%\AssetManager\models`.
  - The default model is Gemma 4 E4B-it Q4_K_M. Any model compatible with llama.cpp works.
- *Alternatives:*
  - LLamaSharp (in-process): it lags behind llama.cpp in new-architecture support such as Gemma 4, and its multimodal API is less mature.
  - ONNX Runtime GenAI: limited availability of Gemma 4 E4B builds.
  - Ollama: requires a separate install.
- **Throughput:** Gemma at about 1–4 s per request means per-file prompts for 100k+ files would take days. Enrichment is therefore batched:
  - **Pack pass:** One request per source. It includes the folder tree summary, sample previews, the documents and the user instruction. It returns the pack description, the license and links, the script notes, a default style, and the default tags inherited by items.
  - **Item pass:** Items are batched as contact sheets, with up to 8 labeled thumbnails or model renders per request. The response is a per-item array. 3D models get their own request with 2–4 views.
  - **Depth per import:** Each import has a depth setting: Full (pack + items), Pack only (items inherit the pack defaults and the file-name tags), or None.
- **English output:** The system prompt requires English output. The schema field descriptions say "English".
- **Documents:** `.txt` and `.md` files are read directly. HTML is converted to text with AngleSharp, PDF with PdfPig, and `.url` files are parsed as INI. The text is truncated to fit the context window, and license-related passages are given priority.

### D8. Import sessions and jobs
- **Job queue:** `JobQueue` is backed by a `Job` table (type, payload, state, attempts). Hosted workers read from `System.Threading.Channels`.
  - Concurrency is set per job type: scanning and previews are parallel up to the CPU count, the AI runs one or two jobs at a time, and the 3D renderer runs one.
  - Pending jobs resume on startup.
  - Progress is exposed through an `IProgressHub` service that the app bar and the Jobs page observe.
- **Progress model:** Every operation reports through one shared `IJobProgress` handle; no feature has its own progress code.
  - The handle carries the stage, the stage index out of the stage count, done/total, the current item, warnings, and errors with their reasons.
  - `JobRunner` wraps every job handler. It creates the handle and persists snapshots to the `Job` table, throttled to at most about 1 per second. It computes the ETA from an exponential moving average of the per-item time within the current stage.
  - `IProgressHub` is an in-memory observable that UI components subscribe to through a shared `JobProgressView` component. The same component appears in the app-bar popover, the Jobs page and the import wizard.
  - Failed items are stored as `JobItemError` rows (`job_id`, `item_ref`, `reason`). "Retry failed" creates a new job that targets exactly those rows.
  - Finished jobs keep their summary JSON, which holds counts such as updated, added and removed. They are pruned to the last 200.
  - **AI queue control:** Pause and resume use a gate (`AsyncManualResetEvent`) that the AI workers check before each request. The rate is measured over the last 5 minutes.
  - **Notifications:** MudBlazor `ISnackbar` messages, raised by a single subscriber to job-completed events.
- **Stats:** `ICatalogStatsService` runs aggregate SQL `COUNT ... GROUP BY` queries.
  - These cover type, enrichment state, availability, license flags, model-issue flags and tag usage.
  - The size breakdown comes from the SQLite `dbstat` virtual table, grouped by table: preview blobs, stored chunks and other data.
  - Collection counts reuse the same rule-to-SQL translation as search, so the counts can never disagree with what Browse shows.
  - Results are cached in memory and invalidated by a catalog-changed event that bulk operations and imports raise once per commit, not once per row. At 200k assets every query runs on indexed columns and stays well under 1 s.
  - Each drill-down count carries the `SearchQuery` that produced it, and clicking navigates to Browse with that query.
- **Import flow:**
  1. **Scan:** stage the sources and entries.
  2. **Group** the files.
  3. **Metadata and previews,** stored in the staging tables.
  4. **AI pack and item passes.** The user may confirm at any point after step 3.
- **Corrections:** A correction prompt on selected staged items queues item-pass jobs with the extra message.
- **Confirm:** Each source commits in one transaction that moves the staged rows into the catalog tables. Unfinished AI jobs are re-targeted to the committed assets.
- **Duplicates** are matched by size and fast hash first, then a full xxHash3 when needed.
- **New version detection:** A new source is compared against existing sources:
  - Score 1 for an Asset Store id match.
  - Otherwise, the Jaccard overlap of the GUID set, or of the relative-path set when there are no GUIDs. A score of 0.5 or more triggers the proposal.
  - The diff (updated, added, removed) is computed at staging time and applied when the user confirms.

### D9. Export and platform services
- Platform work is behind interfaces implemented by the MAUI Windows host:
  - `IClipboardFileService`: uses `Windows.ApplicationModel.DataTransfer.DataPackage.SetStorageItems` with `StorageFile`s from the temp export folder.
  - `IFolderPicker`: CommunityToolkit.Maui.
  - `IFilePicker`: MAUI.
  - `IShellService`: opens files with the default app and reveals them in Explorer.
- **Drag and drop:** A JS drop handler in WebView2 calls `chrome.webview.postMessageWithAdditionalObjects`. The host receives the `CoreWebView2File.Path` values and forwards them to the import page.
- **Exporter:** One `AssetExporter` handles all three actions: open, copy and extract. It resolves every file of an asset through `ILocationResolver`, which checks the stored copy first, then the live location, then extracts from the container chain to the cache. Conflict handling uses a dialog that offers overwrite, skip or rename, with "apply to all".

### D10. UI
- **Component library:** MudBlazor, using only its stock themes. Dark/light is handled by `MudThemeProvider` + `IsDarkMode` (dark, light or system), and the choice is persisted in the app settings.
- **Offline assets:** Static assets come from `_content/MudBlazor/...`. Material icons are embedded SVG constants in MudBlazor, the Roboto font is bundled locally, and the Google Fonts link is removed. three.js lives in `AssetManager.Shared/wwwroot/lib`.
- **Pages:**
  - Dashboard (the start page): stat cards (`MudPaper`) and a simple per-type `MudChart` donut, with clickable counts.
  - Browse: a filter panel, a total matching count, and a virtualized `MudGrid` / `Virtualize` card grid with multi-select.
  - Asset details drawer.
  - Collections.
  - Sources.
  - Import wizard: a `MudStepper` with Inputs → Preview/Review → Confirm.
  - Jobs.
  - Settings.
- **Shared components:** `AssetCard`, `PreviewViewer` (image, 3D angles, audio), `TagEditor`, `LicenseBadge` and `BulkActionBar`. The Browse page, collections and the import preview all reuse them.
- **Stored originals:** "Store in catalog" and "Remove stored copy" are an icon button in the details drawer and items in the bulk-action overflow menu.

### D11. Dependency health
Every dependency was checked for active maintenance as of 2026-11, and only maintained packages are used:
- **MudBlazor, EF Core Sqlite, Microsoft.Extensions.AI, CommunityToolkit.Maui:** active.
- **SharpCompress, Magick.NET, YamlDotNet:** active.
- **FFMpegCore:** 5.x, active. It is used with a bundled LGPL FFmpeg 8 build.
- **PdfPig:** 0.1.14 stable, with 0.1.16 alphas in mid-2026.
- **AngleSharp:** 1.8+.
- **AssimpNetter:** replaces the unmaintained `AssimpNet`. It is a 6.x fork that tracks Assimp 6, and Assimp 6 no longer has a Blender importer, which is consistent with the `.blend` non-goal.
- **sqlite-vec:** active but pre-1.0 (0.1.9, March 2026). It is isolated behind `IVectorIndex`, with the in-memory fallback from D2. SQLite upstream reportedly added a `vec1` extension in September 2026, which could replace it later behind the same interface.
- **llama.cpp and three.js:** active. Versions are pinned and vendored.
- Rejected because they are unmaintained or outdated: the original `AssimpNet`, `sqlite-vss` (superseded by sqlite-vec) and `NAudio`, which is Windows-only and redundant with FFmpeg.

## Risks / Trade-offs

- **Gemma 4 audio is not working in llama.cpp yet.** → Detect the modalities at runtime and fall back to spectrogram, metadata and file names. Record the input mode per asset so assets can be re-enriched once audio support lands.
- **AI throughput on 150k files.** → Pack-level inheritance, contact-sheet batching and per-import depth selection. Enrichment runs in the background and can be resumed.
- **The catalog grows large from previews and stored originals.** → About 15–30 KB per image preview and about 350–450 KB per model (24 renders). Stored originals are opt-in. Chunked blobs and incremental vacuum reclaim space. The size is shown in Settings.
- **3D rendering depends on the WebView being alive.** → The renderer host is mounted in MainLayout, so it exists whenever the app runs. Jobs wait for the renderer and report "renderer unavailable" in the headless Web dev host.
- **The AI misjudges orientation on ambiguous shapes such as rocks, props lying flat or tiles.** → It has a "no natural up" answer, a confidence threshold that leaves the orientation unchanged and marks it "uncertain", an "Orientation uncertain" search filter for quick review, and a one-click user override.
- **Assimp FBX quirks such as unit scale and embedded textures.** → Normalize scale by the bounding sphere, and record the anomalies instead of hiding them. Extract embedded textures through Assimp. Fall back to the Unity `preview.png`. Record failures per asset and allow regeneration.
- **Native dependencies:** llama-server, ffmpeg, sqlite-vec and the Assimp (AssimpNetter) and Magick native libraries enlarge the package (about 150 MB without models). → Accepted for a desktop app. Versions are pinned.
- **The sqlite-vec extension fails to load.** → The in-memory brute-force fallback in D2.
- **Unity YAML is not standard YAML.** → Preprocess the file to strip the `--- !u!` tags before parsing, and read only the texture-reference fields.
- **Multi-gigabyte packages need temporary disk space when extracted.** → Extract to the cache with size checks first, and process one package at a time. The cache is cleaned after each source is committed.

## Migration Plan

This is a greenfield app, so no data migration is needed. EF Core migrations version the catalog schema from v1. The app applies migrations to an opened catalog at startup, after making a `.bak` copy.

## Open Questions

- **`.blend` and `.max` previews:** One option is an optional setting pointing to a locally installed Blender, used to convert `.blend` files to GLB. This could be added later without changing the specs. Until then, these files use placeholder previews.
- **The exact default quantization (Q4_K_M or Q5) and the default GPU or CPU build:** These can be tuned after benchmarking on the user's machine.
