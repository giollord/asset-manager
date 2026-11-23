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
- `AssetManager` (MAUI, `net10.0-windows10.0.19041.0` only): Platform services for the clipboard file list, pickers and shell open/reveal. Volume identity (`WindowsVolumeService`, plain P/Invoke) lives in Processing so both hosts and the tests share it; non-Windows hosts get a service that stores absolute paths.
- `AssetManager.Web` stays as a development host. It runs in the browser with hot reload and uses stub platform services for clipboard and reveal, which report "not supported".
- Each library exposes its own registration (`AddAssetManagerCore`, `AddAssetManagerData`, …). The single `services.AddAssetManager()` entry point lives in `AssetManager.Shared`, which references every library, and both hosts call it. Startup work (opening the catalog, choosing the vector index, resuming jobs) runs through `IStartupTask`s that `AppStartup` executes once from the layout, because MAUI has no `IHostedService` pipeline.
- *Alternative:* a single project. Rejected, because it mixes platform code with domain code and blocks reuse by the Web host.

### D2. Database: SQLite in a single file
- SQLite runs through EF Core (Microsoft.EntityFrameworkCore.Sqlite) in WAL mode with `auto_vacuum=INCREMENTAL`.
- FTS5 virtual table `asset_fts` covers name, file names, paths, description, tags and source name. Triggers or a sync service keep it current, and it uses `prefix='2 3'` so prefix queries work.
- **Search** (`AssetSearchService`): a `SearchQuery` record (Core) holds every filter.
  - Each filter becomes one `Where` on the catalog's asset query, and they combine with AND.
    - Tags and collections use the shared tag-rule translation.
    - The source filter includes nested sources.
    - License filters use the effective license.
    - Availability means stored, or on a mounted volume or local path.
    - Model issues use the flattened flags.
  - Text joins the FTS hits and ranks them by weighted bm25: name 10, tags 5, files 4, description 2, paths 1, source 1. Every word must match as a prefix.
  - Sorting: relevance (with text), newest or name.
  - `SearchQuery` round-trips through a URL token, so dashboard drill-downs can navigate to Browse.
- **Semantic search and find similar** (`CatalogSearch`):
  - The query is embedded (or the selected asset's stored vector is used), then the nearest 2,000 neighbors are taken from the vector index.
  - All the other filters are applied to those neighbors in SQL, and the results are ordered by distance. In semantic mode, the words rank results rather than filter them.
- **Vectors:** The `sqlite-vec` extension provides a `vec0` virtual table of 768-d float32 vectors. They are truncated to 256-d via Matryoshka to save space, plus cosine KNN. *Fallback* if the native extension fails to load: brute-force cosine over int8-quantized vectors cached in memory. This is about 50 MB per 200k assets at 256-d.
- **Blobs:**
  - `PreviewBlob(asset_id, kind, index, mime, bytes)` holds the previews.
  - `StoredFileChunk(stored_file_id, seq, bytes)` holds stored originals in 8 MB chunks. This stays under SQLite's blob limits and allows streaming.
  - Removing stored copies runs `PRAGMA incremental_vacuum`. A "Compact catalog" action runs a full `VACUUM`.
- **Staging:** Import sessions live in the same DB, which preserves the single-file rule, and staging survives restarts. Staged sources and assets are ordinary `Source`/`Asset` rows with a non-null `ImportSessionId`, hidden from every catalog query by EF global query filters. Previews, tags, handlers and the FTS index therefore work on staged items unchanged, and confirming a source clears `ImportSessionId` in one transaction. Skipped entries are kept in `StagedSkip(session, source, path, reason)`.
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
- Containers are extracted whole into the cache when they are walked for import, so every entry is a real local file that handlers can open by path. Resolving a single entry later (open, copy, store) extracts only that entry from random-access formats (zip, tar, non-solid 7z) and falls back to a full extraction for solid and gzip-compressed ones.
- An extracted `.unitypackage` is rearranged into its original project layout (`Assets/...`, each `.meta` beside its asset), so entries keep their real names and extensions; a GUID index is kept beside it.
- `VirtualFileSystem` walks the tree recursively. When an entry is itself a container and the depth is under the limit (default 5), it extracts the entry to the cache and recurses.
- **Ignored folders:** `FolderIgnoreRules` holds the session's rules: the catalog's `ClassificationOptions.IgnoredFolders` (unless the import turns them off) plus the rules entered for that import, both stored on the `ImportSession` so a resumed scan behaves the same. A rule without '/' matches a folder name at any depth; a rule with '/' matches a path from the root of the input folder or of the container being read (`*` and `?` match within one segment; case-insensitive). The folder walk does not enter an ignored folder, container entries under one are dropped, and containers there are never opened. An archive or package file counts as a folder: a rule matching its name or path keeps it closed (useful when a pack ships both a folder and a zip of it). The walk yields one `IgnoredFolder` node per ignored folder, which the stager lists as a skipped item "ignored folder (<rule>)".
- Every entry gets a `Location` with the full chain.
- **Extraction cache:** `%LOCALAPPDATA%\AssetManager\cache`, with content-addressed folders, LRU eviction by size and age, and a lock file for entries in use.
- **Volume identity (Windows):** The volume serial comes from `GetVolumeInformationW` through P/Invoke. Drive changes are detected by polling `DriveInfo.GetDrives()` every few seconds, plus an event raised to the UI. Availability is checked lazily per volume, not per file.

### D5. Type handlers
- Each asset type has one handler: `IAssetTypeHandler { Detect; ExtractMetadata; GeneratePreviews; BuildAiInput }`. The handlers are image/texture, model, audio, font, video, document and fallback.
- A grouping step runs before the handlers. `CompanionResolver` attaches textures to models in this order:
  1. Unity `.mat` references, by GUID, to `_MainTex`, `_BaseMap`, `_BumpMap` and so on. The `.mat` YAML is parsed with YamlDotNet after the `!u!` tags are stripped.
  2. Assimp material texture paths.
  3. File-name heuristics: the same base name, or suffixes such as `_albedo`, `_diffuse`, `_basecolor`, `_normal`.
- A texture claimed by a model becomes a companion and not its own asset. A texture shared by several models is a companion of each. The material and renderer slot a companion fills are stored on its `AssetFile`, so previews can be regenerated later without re-reading the package.
- Classification also separates `SourceDocument`s (license/readme/credits files and `.url` shortcuts), which feed license discovery and are not imported as assets.
- **Ignored or noted content:** Script and code extensions are configurable, by default `.cs .js .shader .hlsl .cginc .asmdef .dll .prefab .unity .asset` and others. They are collected into a per-source manifest that the AI summarizes into the source notes. Borderline items get a `ShouldImport` decision from the AI in the batch prompt, and the user can override it in the preview.

### D6. Previews
- **Images:** Magick.NET (Q8) decodes PNG, JPG, TGA, PSD (flattened), EXR (tone-mapped), DDS, SVG, GIF and others.
  - The preview is downscaled to a maximum edge of 512 and saved as lossy WebP at quality 80 with alpha.
  - The original is kept when it is at most 512 px and at most 64 KB, and is a PNG, JPG, GIF or WebP.
  - Pixel art (an edge of 128 px or less, or 64 colors or fewer) is scaled with point filtering.
  - The dominant palette comes from Magick quantization to 5 colors, and is stored as hex values plus named colors.
- **Settings:** All of the numbers in this section are defaults only. A single `PreviewOptions` record, stored as catalog settings and edited on the Settings page, is the only source that handlers read from. Values are validated with DataAnnotations: for example, the azimuth count must be 1–16, elevations must be within −89..89°, the clip length 1–60 s, and the quality 1–100. The record's version is stored per preview, so "regenerate outdated previews" can find the affected assets.
- **Audio:** FFmpeg is bundled as `ffmpeg.exe` and `ffprobe.exe` (the LGPL shared build from BtbN/FFmpeg-Builds, pinned and SHA-256-verified, downloaded once at build time and not committed). ffprobe runs through FFMpegCore; encoding calls ffmpeg directly.
  - The preview clip is the first 10 s, encoded as mono Opus in Ogg at 32–48 kbps. WebView2 plays it natively.
  - A waveform PNG (`showwavespic`) and a spectrogram (`showspectrumpic`) are generated and stored as WebP. The spectrogram is the AI's audio fallback.
  - Metadata comes from ffprobe.
- **3D:**
  - AssimpNetter (the maintained fork of AssimpNet, bundling Assimp 6.x) loads FBX, OBJ, DAE, glTF/GLB, 3DS, PLY, STL and other formats. It gives the stats (vertices, triangles, bones, animations).
  - The GLB for the renderer is written by our own `GlbWriter` from the loaded scene. Assimp's glTF2 exporter crashed the whole process (a native access violation) on real FBX files such as Mixamo characters. The writer covers what previews need:
    - the node hierarchy with local transforms;
    - triangle geometry: positions, normals, the first UV set (V flipped to glTF's convention) and vertex colors;
    - materials by name with their base color and opacity;
    - for animations only: one skin over every bone (JOINTS_0/WEIGHTS_0 with the four strongest weights, inverse bind matrices from the bone offsets), all clips as LINEAR samplers, and nodes as translation/rotation/scale so clips can drive them.

    Textures stay out of the GLB, because the renderer applies them by material name.
  - AssimpNetter hands out Assimp's matrices as `System.Numerics.Matrix4x4` without converting them: the memory keeps Assimp's row-major, column-vector layout (translation in M14/M24/M34). `AssimpMatrix.ToNumerics` transposes every node and bone matrix before any math (bounds, scale, probe ranking) or GLB output; scenes built in code go the other way. A hand-written glTF test guards this.
  - Assimp calls are serialized behind one process-wide lock, because AssimpNet shares native state.
  - The renderer is three.js (bundled in `wwwroot/lib`, version pinned). It runs inside the app's own WebView in a hidden `PreviewRenderHost` component that MainLayout mounts once.
  - .NET streams the GLB plus the resolved texture bytes through `DotNetStreamReference` to a JS module.
  - The JS module applies textures by material name, frames the bounding sphere, and renders 3 elevations (−15°, 20°, 55°) × 8 azimuths at 384 px. It renders with `alpha: true` and a transparent clear color, then returns WebP blobs from `canvas.toBlob('image/webp', 0.8)`.
  - The same JS module powers the interactive "open original" viewer, through OrbitControls. One renderer serves both uses.
  - *Alternative:* native offscreen OpenGL (Silk.NET). Rejected because it needs a second rendering stack and per-platform context handling, while the WebView already provides WebGL.
- **Animations:** `ModelAnalyzer.LooksLikeAnimationClip` decides after metadata extraction (clip lengths are stored in `ModelMetadata.AnimationSeconds`); the model handler then sets the type to `Animation` unless the user set the type. Animation-only files without a mesh load too.
  - The renderer's `renderAnimation` plays the first clip with an `AnimationMixer`, samples `AnimationFps` frames per second up to `AnimationMaxSeconds`, frames the camera on the union of the bounds of up to 8 sampled poses (precise, skinned), and renders each frame from elevation 15°, azimuth 30° as WebP. Skinned meshes are not frustum-culled, since their bounds are the rest pose.
  - .NET joins the frames with Magick.NET into one looping lossy animated WebP (`PreviewKind.Animation`, frame delay 1/fps, background disposal so transparency does not smear) and stores the middle frame as the thumbnail. Animations get no angle views and no orientation probe.
  - A skeleton without a mesh is drawn as a stick figure: a thin cylinder per parent→child bone, re-posed every frame.
  - The grid card plays the animated WebP while the pointer rests on it; the details show it, and the 3D view loops the clip.
  - The AI may turn a 3D model into an animation (a prompt line explains the type), never the reverse.
- The Unity `preview.png` is used as the fallback for formats that cannot be rendered. It is stored as a `UnityPreview` blob. With no preview at all, nothing is stored and the UI shows the type placeholder; the failure reason is kept in `PreviewError`.
- The orientation probe sheet is generated together with the model views whenever the file has no usable up-axis metadata, and stored as an `OrientationProbe` blob for the AI step. The geometric pre-ranking is stored in the model metadata.
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
     - The probe sheet is sent as six separate numbered images (the drawn labels are blanked), because small models mix up the tiles of a composed sheet. The schema asks for one observation per candidate (top / ground contact) before the choice.
     - Self-consistency: when the first answer is confident, the question is asked again with the candidates in reverse order. If the two answers name different candidates, the result is treated as low confidence ("orientation uncertain", unchanged). Measured with Gemma 4 E2B on two sideways sample models: one was corrected and one was flagged; none was rotated wrongly. Without this check, E2B confidently chose upside-down candidates. With the default Gemma 4 E4B, both were corrected, and the two answers agreed each time.
  4. **User override:** Rotate ±90° buttons on X/Y/Z in the details drawer show a live preview in the viewer. Saving re-renders the model and sets `decided_by = User`, which the AI never overrides. This reuses the user-edited field mask from D3.
  - The correction is applied only as a root transform in three.js, for the previews and the in-app viewer. Original files are never rewritten, and export returns the original bytes.

### D7. AI runtime: bundled llama.cpp server
- **Runtime:**
  - `llama-server.exe` ships in two builds: Vulkan for GPU, and CPU. Both are pinned at release b8637 or later.
  - `LlamaServerHost` starts it on a free localhost port with `--mmproj`, binding only to 127.0.0.1.
  - The process is tied to the app lifetime through a Windows Job object, health-checked through `/health`, and restarted if it crashes.
  - The modalities it reports through `/props` set whether audio input is enabled.
- **Clients:** The client is `Microsoft.Extensions.AI` `IChatClient` through the OpenAI-compatible adapter. Prompts use `ChatResponseFormat.ForJsonSchema<T>()`, which llama.cpp enforces through grammar, and the output is validated before it is persisted. `StructuredChat` sends the schema (snake_case names, enums as strings), parses and validates the answer (`IAiResult.Validate`, plus caller checks such as "every labeled item answered"), and on failure sends the rejected answer back with the problems listed. After 3 attempts the item is marked failed.
- **Embeddings:** A second, lightweight llama-server instance runs with `--embedding`, using the EmbeddingGemma-300M GGUF. It is called through `IEmbeddingGenerator`.
  - The text is built in EmbeddingGemma's prompt format (`title: {name} | text: {type}. {description} Tags: ...`; queries use `task: search result | query: ...`), truncated to 256 dimensions (Matryoshka) and normalized.
  - `Asset.EmbeddingDirty` is set by a SaveChanges interceptor when name, description, type or tags change, or a tag is renamed. Bulk tag SQL calls `EmbeddingStaleness.MarkAsync`.
  - A background refresher re-embeds dirty committed assets after catalog changes (debounced) and when the embedding server becomes ready. The flag is cleared only if the text did not change while embedding. Vectors from a different model file are re-embedded.
- **Models:**
  - Settings → AI lets the user pick a local GGUF file plus its mmproj file. It also offers a one-click, user-initiated download from Hugging Face into `%LOCALAPPDATA%\AssetManager\models`.
  - The default model is Gemma 4 E4B-it Q4_K_M. Any model compatible with llama.cpp works.
- *Alternatives:*
  - LLamaSharp (in-process): it lags behind llama.cpp in new-architecture support such as Gemma 4, and its multimodal API is less mature.
  - ONNX Runtime GenAI: limited availability of Gemma 4 E4B builds.
  - Ollama: requires a separate install.
- **Throughput:** Gemma at about 1–4 s per request means per-file prompts for 100k+ files would take days. Enrichment is therefore batched:
  - **Pack pass:** One request per source. It includes the folder tree summary, sample previews, the documents and the user instruction. It returns the pack description, the license and links, the script notes, a default style, and the default tags inherited by items.
    The script count in the source notes is computed, not left to the model; the model only adds what the scripts appear to do. Unity Asset Store packages default to "Unity Asset Store EULA" when no document says otherwise. llama.cpp cannot decode WebP, so preview images are re-encoded as JPEG for the model.
  - **Item pass:** Items are batched as contact sheets, with up to 8 labeled thumbnails or model renders per request. The response is a per-item array. 3D models get their own request with 2–4 views.
    The contact sheet is one composed JPEG (4 columns of 256 px tiles on gray, numbered labels). A model request sends 4 views of the primary ring (front, right, back, left) as a 2×2 sheet. Audio is sent as a 16 kHz mono WAV made from the stored clip when `/props` reports audio. Otherwise its spectrogram becomes a sheet tile, and with no vision the item is sent as text only. The mode is stored in `AiInputMode`. AI type changes stay within a family (image/texture/sprite sheet, audio/music) or move away from Other. Named AI colors fill `Colors` only when no measured palette exists.
  - **Depth per import:** Each import has a depth setting: Full (pack + items), Pack only (items inherit the pack defaults and the file-name tags), or None.
- **English output:** The system prompt requires English output. The schema field descriptions say "English".
- **Documents:** `.txt` and `.md` files are read directly. HTML is converted to text with AngleSharp, PDF with PdfPig, and `.url` files are parsed as INI. The text is truncated to fit the context window, and license-related passages are given priority. Documents are found inside the source while walking it, and next to a file or archive source (same folder). Shortcut URLs are recorded as source links directly, without the AI. Evidence the model cites is matched to a provided document, or dropped.

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
  - Worker pools: Cpu (2 jobs), Io (2), Ai (1), Render (1). Handlers parallelize their own items. A job interrupted by an app exit is queued again on start. "Retry failed" re-enqueues the same type with an `onlyItems` list in the payload (`JobContext.Includes`).
  - Handlers are resolved on the queue's first use, not when it is constructed, because several handlers enqueue follow-up jobs and so depend on `IJobQueue`. A composition test resolves the real service graph under a timeout, since a cycle through a factory registration deadlocks instead of throwing.
  - Jobs resumed at app start run before the AI runtime is up. The AI stages therefore wait (up to 5 min) for a configured chat model that is still loading, and only treat a missing model as "not enriched" when none is configured or its server failed.
  - **Parallel AI requests:** the chat server runs with `AiParallelRequests` slots (Settings → AI, 1-4, default 3; always 1 on the CPU), each with its own 8192-token context (`-np N -c 8192*N --no-kv-unified`). Generating text one token at a time is limited by memory bandwidth, so several requests decoded in the same pass give more answers per second. Measured on an RTX 4070 with Gemma 4 E4B, image descriptions per minute: 42 / 77 / 106 / 101 for 1 / 2 / 3 / 4 slots. If the GPU server does not start with N slots it is retried with 1 before falling back to the CPU. The item pass works on up to N chunks at once, each with its own database context; applying and saving results is serialized, so a new tag is not created twice. The shared JSON options are frozen up front, because schema creation would otherwise mutate them from several threads.
  - **Server restarts:** `AiQueue.Chat` resolves the running server for every request (`GatedChatClient` over an `IChatServerProbe`). A request that cannot reach the server (it restarted after a settings change, or crashed) waits until a server is ready on a new endpoint (up to 5 min) and is sent again; it fails only if the server stays unreachable, failed or was turned off.
  - **AI queue control:** Pause and resume use a gate that every AI request waits on. `AiQueue.Chat` wraps the chat client in a `GatedChatClient`, so no caller can bypass the pause. The rate counts enriched items (not requests) over the last 5 minutes. The pending count is the number of assets with `AiStatus = Pending`. Cancel stops the AI jobs and sets the waiting assets to "not enriched".
  - **Notifications:** MudBlazor `ISnackbar` messages, raised by a single subscriber to job-completed events.
- **Stats:** `ICatalogStatsService` runs aggregate SQL `COUNT ... GROUP BY` queries.
  - These cover type, enrichment state, availability, license flags, model-issue flags and tag usage.
  - The size breakdown comes from the SQLite `dbstat` virtual table, grouped by table: preview blobs, stored chunks and other data.
  - Collection counts reuse the same rule-to-SQL translation as search, so the counts can never disagree with what Browse shows.
  - Results are cached in memory and invalidated by a catalog-changed event that bulk operations and imports raise once per commit, not once per row. At 200k assets every query runs on indexed columns and stays well under 1 s.
  - Each drill-down count carries the `SearchQuery` that produced it, and clicking navigates to Browse with that query.
  - Implementation (`CatalogStatsService`):
    - Type and enrichment counts use `GROUP BY` over the covering indexes `(ImportSessionId, Type)` and `(ImportSessionId, AiStatus)`.
    - License and availability come from one grouped pass over `(ImportSessionId, SourceId, license override, IsStored)`, combined in memory with the sources' licenses and volumes.
    - Model issues, collections and tags use the search filters directly.
    - Source counts include nested sources.
    - When SQLite lacks `dbstat`, the size breakdown falls back to the sum of blob lengths.
    - A test checks that every drill-down count equals the Browse total of its query.
    - At 200k assets, all stats take about 0.3 s and the first tag-filtered page about 0.1 s.
- **Import flow:**
  1. **Scan:** stage the sources and entries.
  2. **Group** the files.
  3. **Metadata and previews,** stored in the staging tables.
  4. **AI pack and item passes.** The user may confirm at any point after step 3.
  - Every archive or package met while scanning becomes its own staged source, nested under the folder or archive that holds it (`ParentSourceId`). Files belong to the innermost container's source. A single plain file input is a File source.
  - Skips are stored with a kind and location:
    - **Noted:** scripts and project files.
    - **Document:** license/readme/link files, read again by the pack pass.
    - **Ignored**, **Unsupported:** unknown file types, or nesting too deep.
    - **Error:** for example "cannot open archive (...)".
  - Unity's embedded `preview.png` is stored at staging as the fallback preview.
  - `ImportSession.StagedThrough` records the last finished stage, so an interrupted import job resumes. An unfinished scan starts over; previews continue with unprocessed assets; the AI stages continue with pending ones. The wizard offers "Resume import" when the import job failed or was cancelled, or when a full-AI session still has assets that are not enriched or failed; `ImportService.ResumeAsync` sets those back to pending and enqueues a new import job for the session. A stage that already finished never touches the model: the "not enriched" fallback applies only to a pass that actually needed it. One item's failure (bad AI output, a failed orientation re-render) fails only that item; the re-render looks the asset up including staged rows.
  - Pack-pass default tags are given origin Import (inherited), so the item pass's own AI tags add to them instead of replacing them. With no model installed, pending assets are set to "not enriched" and the job records one warning.
  - `Source.CommittedFromSessionId` keeps the link after confirm, so unfinished AI stages still find their assets.
- **Review step** (`ImportReviewService`, `ImportReview` component):
  - Staged items are shown per source as `AssetCard`s, 48 at a time with "Show all". Each source shows its `LicenseBadge`, description, errors, skipped entries and replacement proposal.
  - The review reloads, at most every 1.5 s, while the import or an `ai.enrich` job reports progress, so AI results appear live.
  - Inline edits go through `MetadataEditService`, so edited fields become user-edited. Tags added in the review are user tags.
  - Files unchanged from a version being replaced are labeled "unchanged", not "already cataloged".
  - The replacement can be accepted, declined ("Import as new"), or pointed at another cataloged source picked by name.
- **Corrections:** A correction prompt on the selected staged items queues one `ai.enrich` job for exactly those items. The job carries the session instruction plus the user's message as "Correction from the user for these items".
- **Regrouping** (`StagedRegrouping`):
  - A companion file can become its own staged asset. It is classified by its path and keeps the import tags.
  - Selected items can be merged: the other items' files become companions of the first one.
  - Metadata and previews of the affected assets are generated again.
- **Confirm:** Confirm is allowed once the session is Ready, after previews. It queues an `import.commit` job, which is stage 6, "Commit".
  - Sources commit innermost first, each in one transaction. The transaction deletes the source's excluded assets, clears `ImportSessionId` on the rest, and sets `CommittedFromSessionId` (and applies a version replacement, if accepted). A source left with no assets and no child sources is dropped.
  - Staged skips and unused tags are then removed, and the session becomes Committed.
  - Asset ids do not change, so a running import job simply continues its AI stages on the committed assets. If that job is no longer active while assets are still Pending, it is queued again. It skips its finished stages.
- **Re-import** of selected assets is an `assets.reimport` job.
  - It re-reads each file's size and fast hash (resolved like any read, so a stored copy is used first), then regenerates metadata and previews with the handlers, which respect `UserEdited`. Tags are not touched.
  - With re-enrichment, the assets become Pending and an `ai.enrich` job follows. That job runs the item pass on a selection, and AI corrections use it too. The item pass keeps user-edited fields and replaces only AI-origin tags.
- **Duplicates** are matched by size and fast hash first, then a full xxHash3 when needed. Detection runs at the end of the grouping stage and compares staged primary files with committed ones. A match is confirmed in one of three ways:
  - Both files are at the same location.
  - The file is 128 KB or smaller, so the fast hash already covers all of it.
  - Full hashes agree. These are cached in `AssetFile.ContentHash`.

  If the original can no longer be read, the fast-hash match stands. Duplicates get `DuplicateOfAssetId`, and they are excluded unless the session includes duplicates. In the preview stage, a duplicate copies its original's metadata and previews when they are current, instead of rendering again.
- **New version detection:** A new source is compared against existing sources:
  - Score 1 for an Asset Store id match.
  - Otherwise, the Jaccard overlap of the GUID set, or of the relative-path set when there are no GUIDs. A score of 0.5 or more triggers the proposal.
  - Detection (`SourceVersions.ProposeAsync`) runs after duplicate detection. It sets `ReplacesSourceId` and `ReplaceScore` on the staged source.
  - Duplicates whose original belongs to the proposed source stay included, because they are unchanged updates. A source whose content is identical to a cataloged one is not proposed: re-adding a folder skips it as duplicates.
  - Duplicates reuse the original's previews and metadata, and also its AI results when the original is enriched, so unchanged files are not enriched again.
  - The diff (updated, added, removed) comes from the staged rows, so it is always current. Assets are matched by GUID, otherwise by relative path, each old asset at most once.
    - Included matches are updates.
    - Unmatched included assets are added.
    - Old assets with no match are removed.
    - An excluded staged asset that matches leaves its old asset untouched.
  - **Apply on confirm** (unless `ReplaceAccepted == false`) runs in the source's transaction:
    - Matched old assets keep their ids, user-edited fields and user tags. They take the new files, previews and metadata, and the new version's non-user tags. They also take its AI results, or become Pending when the new version has not been enriched yet.
    - The old source takes the new location and package info. Its edited fields stay, and its description is cleared when the pack pass has not run yet.
    - Added assets and child sources move to the old source, removed assets are deleted, and the staged source is dropped.
    - Assets that had stored originals are queued for storing again.
  - The AI passes pick their next source or pending asset dynamically, and save only rows that still exist. A replacement confirmed mid-pass is therefore enriched on the old source. Staged skips are kept until the pack pass has run.

### D9. Export and platform services
- Platform work is behind interfaces implemented by the MAUI Windows host:
  - `IClipboardFileService`: uses `Windows.ApplicationModel.DataTransfer.DataPackage.SetStorageItems` with `StorageFile`s from the temp export folder.
  - `IFolderPicker`: CommunityToolkit.Maui.
  - `IFilePicker`: MAUI.
  - `IShellService`: opens files with the default app and reveals them in Explorer.
- **Drag and drop:** A JS drop handler in WebView2 (`fileDrop.js`, installed by the layout) calls `chrome.webview.postMessageWithAdditionalObjects`. The host receives the `CoreWebView2File.Path` values in `WebMessageReceived` and publishes them to the `FileDropBroker` singleton.
  - MAUI before 10.0.120 creates the WinUI `WebView2` control with `AllowDrop = false`, so Explorer drops never reach the page. The host sets `AllowDrop = true` when the BlazorWebView initializes.
  - The layout navigates to the import page, which takes the pending paths.
  - Outside WebView2 (the web dev host) a drop shows a hint to use the buttons instead.
  - The inputs step also accepts a pasted path.
- **Exporter:** One `AssetExporter` (Processing) handles all three actions: open, copy and extract. It resolves every file of an asset through `ILocationResolver`, which checks the stored copy first, then the live location, then extracts from the container chain to the cache.
  - An asset is exported whole or not at all: when one of its files cannot be resolved, the asset is reported (a failed job item with the missing drive) and the others are still exported.
  - Extract keeps each file's `RelativePath` (Unity: `Assets/...`; `..` and invalid characters are dropped) or writes flat.
  - Conflicts are planned before the job: `PlanAsync` lists the target paths that already exist or that two selected files share. `ExtractDialog` asks about each one (overwrite, skip, or keep both as `name (2).ext`), with "do this for all remaining". The choices go into the `assets.extract` job payload, so a resumed job needs no UI.
  - Copy (`assets.copy`) writes the files flat into a new folder under `%TEMP%\AssetManager\export` (clashes renamed; folders older than a day are deleted) and puts the written files on the clipboard.
  - Both are Io-pool jobs with progress and "Retry failed" for the unavailable assets.

### D10. UI
- **Component library:** MudBlazor, using only its stock themes. Dark/light is handled by `MudThemeProvider` + `IsDarkMode` (dark, light or system), and the choice is persisted in the app settings.
- **Transparency:** Previews are stored with their alpha channel (WebP), and both the offline renderer and the live viewer clear to transparent. Every preview surface (grid tiles, details, the 3D viewer) uses the `preview-surface` class, whose background is the theme's own color, so switching between dark and light changes what is behind the asset. Only the copies sent to the AI are flattened to JPEG.
- **Drive changes:** `WindowsVolumeService` polls the drives; a `VolumesChanged` event is forwarded as a catalog change (`VolumeChangeStartupTask`), so the cached statistics are recomputed and open pages refresh their availability counts and "drive not attached" marks.
- **Toasts:** every snackbar closes on its own after 15 s (`ToastDuration`; hovering keeps it open) and has a close button. Job toasts keep their "Details" action but no longer wait for a click.
- **Offline test mode (Debug builds):** `ASSETMANAGER_OFFLINE=1` points `HttpClient.DefaultProxy` and WebView2 (`--proxy-server`) at a proxy that does not exist, so every network request fails as it would without a network. Loopback stays reachable for llama-server and the DevTools port; the app's pages are served in-process.
- **Offline assets:** Static assets come from `_content/MudBlazor/...`. Material icons are embedded SVG constants in MudBlazor, the Roboto font is bundled locally, and the Google Fonts link is removed. three.js lives in `AssetManager.Shared/wwwroot/lib`.
- **Pages:**
  - Dashboard (the start page): stat cards (`MudPaper`) and a simple per-type `MudChart` donut, with clickable counts.
  - Browse: a filter panel, a total matching count, and a virtualized card grid with multi-select.
    - The grid is a `Virtualize` over rows of fixed height (220 px). JS (`browse.js`) measures how many 168 px cards fit and reports changes through a `ResizeObserver`; the items provider asks `CatalogSearch` for `rows × columns` assets per request.
    - The filters are one `SearchQuery`. Every change replaces the URL with `browse?q=<token>`, so drill-downs, bookmarks and Back restore the same view.
    - Selection: a click selects one card, Ctrl toggles, Shift extends from the last clicked card (ids of an unloaded range are fetched by skip/take), the checkbox toggles, "Select all" and Ctrl+A take every match, Esc clears. A double click opens the details.
    - Card extras: model angles cycle while the pointer rests on a card; audio cards have a play button that plays the stored preview clip through one shared `Audio` element; enriched assets have "Find similar" (`SimilarTo`).
    - Previews come from `PreviewQueries` (Data) through a per-window `PreviewCache` of data URLs (bounded, cleared on `ContentChanged`).
  - Asset details drawer (`AssetDetailsDrawer`, a right-hand temporary `MudDrawer` opened by double-clicking a card).
    - Data comes from `AssetDetailsService.LoadAsync`: the asset with files and source, the source chain (outermost first, shown as breadcrumbs that open Browse filtered to that source), tags with origin, and the still previews (model angles as a strip, else thumbnail / waveform / Unity preview). The audio clip is only read when "Play preview" is pressed.
    - The header has the store/unstore icon button (enqueues `storage.store` / `storage.unstore`) and Edit. The edit form covers name, type, description and the license flags; saving goes through `MetadataEditService`, so each changed field is marked user-edited and shows an "edited" chip.
    - Tags use `TagEditor` (added as user tags). Links and the Asset Store page open through `IShellService` and only for http(s) URLs.
    - Models: an orientation panel shows the current rotation and who decided it, with ±90° buttons per axis. A pending rotation switches the preview to the live `ModelViewer` (three.js `createViewer` over a GLB built from the original by `PreviewService.ViewerRequestAsync`; `setOrientation` rotates and re-centers without reloading). "Save & re-render" stores the rotation as decided by the user (`AssetDetailsService.SetOrientationAsync`, `EditedFields.Orientation`) and enqueues `previews.regenerate` for the asset. The orientation judge and re-imports skip user-decided orientations.
    - A details table lists geometry, skeleton, animations, real-world size, unit/root scale and anomaly, source up-axis, missing textures, and the image/audio/video/font/document facts.
  - Collections (`collections`, `collections/{id}`), with a Tags tab (`collections?tab=tags`).
    - The list shows each collection's member count and its largest types, from `ICatalogStatsService.CollectionsAsync`.
    - The editor has the name, must-have and must-not-have tags, and the description. While the rule is edited, the members grid (48 per page, shared `AssetCard`, double click opens the details drawer) and the per-type counts show the edited rule's members; Save makes it the collection's rule. "Open in Browse" opens the same rule as a search.
    - "Write with AI" enqueues `ai.describe-collection` (`DescribeCollectionJob`, AI pool, so the AI queue's pause applies). Without a running model the job fails with a message. A description the user changes becomes theirs (`DescriptionOrigin = User`).
    - The Tags tab lists tags with their usage count, sorted by usage (default) or by name. A tag opens Browse filtered to it, or starts a new collection with that tag (`collections?tab=new&tag=…`).
  - Sources (`sources`, `sources/{id}`): a filterable list with each source's asset count (nested sources included), and a details panel.
    - The details (`SourceService.LoadAsync`) show the kind, the location with the drive's current letter (or a "drive not attached" chip), the import date, the package metadata (title, publisher, version, Asset Store id), the Asset Store link and the other links, the description, the license, the notes, the parent chain and the nested sources.
    - "Edit license and notes" saves through `MetadataEditService.EditSourceAsync`, so the fields are marked user-edited.
    - "Delete source" asks for confirmation with the number of assets it removes (nested sources included), then calls `DeletionService.DeleteSourcesAsync`. The original files are never touched.
  - Import wizard: a `MudStepper` with Inputs → Preview/Review → Confirm.
    - The inputs step offers files, folders, drops or pasted paths, shared tags, the AI instruction, the AI depth and "include files that are already cataloged".
    - Confirm is enabled once the session is Ready (previews done). The AI stages may still be running, and continue after the commit.
    - An open session (not confirmed or cancelled) is shown again when the page opens, including after a restart.
  - Jobs.
  - Settings, in sections:
    - Catalog: the open catalog's path and size (previews, stored originals, other), Compact, and "open another catalog" (a file path, Browse, or a new `catalog.amdb` in a picked folder). `CatalogLocationService.SwitchAsync` opens the file (creating and migrating it when needed) and then saves the path in the app settings. It refuses while jobs run, since they write to the open catalog. Jobs still pending in the newly opened catalog resume at the next start.
    - AI model: model files, download, acceleration.
    - Import: the default AI depth (app setting `DefaultAiDepth`, which the import wizard starts with), the maximum nesting depth, the cache size limit and age, and the script and ignored extension lists (`ClassificationOptions`, stored in the catalog).
    - Previews: one field per `PreviewOptions` property, generated from its `[Display]` and `[Range]` attributes, with the default and range as helper text. Per-field range checks happen while typing; the record's cross-field rules run on Save. "Reset to defaults" stores a fresh record. A note says existing previews change only when regenerated.
    - Maintenance: "Regenerate outdated previews".
- **Original viewer:** "Open the original" in the details drawer opens `OriginalViewer` in a large dialog.
  - Models and animations use the orbit `ModelViewer` with the saved orientation.
  - Images, audio and video come from `OriginalMedia` (Processing), which resolves the primary file through the exporter. Browser formats pass through unchanged; other images become PNG at full resolution (Magick), and other audio becomes full-length Opus (FFmpeg). The bytes reach the page as a `DotNetStreamReference` that `viewer.js` turns into a blob URL, revoked when the dialog closes.
  - "Open externally" resolves the file (a file inside a container is extracted to the cache) and opens it with the default app. "Show in Explorer" selects the file, or the outermost container file for a file inside an archive or package. `explorer.exe /select,"<path>"` quotes only the path, since Explorer opens Documents when the whole argument is quoted.
- **Bulk actions:** `BulkActionBar` appears in Browse next to the selection count. It offers tag and untag (`TagDialog`: free tags with suggestions, or the selection's own tags with their counts), copy, extract, and delete (a confirmation naming the count; cancel removes nothing). An overflow menu has re-import, re-import and re-enrich, re-enrich, regenerate previews, and store/unstore.
  - Long work is queued as jobs. Re-enrich (`ReenrichService`, AI) marks the assets pending and queues `ai.enrich`. Without a configured, working model it refuses to start (the bar also checks before "re-import and re-enrich"), because the pass would otherwise mark the assets "not enriched" and lose their AI status.
  - "Select all" takes every match of the current query, text included (`AssetSearchService.MatchingIdsAsync` applies the FTS match; the semantic filter clears the text itself).
- **Shared components:** `AssetCard`, `PreviewViewer` (image, 3D angles, audio), `TagEditor`, `LicenseBadge` and `BulkActionBar`. The Browse page, collections and the import preview all reuse them.
- **Stored originals:** "Store in catalog" and "Remove stored copy" are an icon button in the details drawer and items in the bulk-action overflow menu.

### D11. Dependency health
Every dependency was checked for active maintenance as of 2026-11, and only maintained packages are used:
- **MudBlazor, EF Core Sqlite, Microsoft.Extensions.AI, CommunityToolkit.Maui:** active.
- **SharpCompress, Magick.NET, YamlDotNet:** active.
- **FFMpegCore:** 6.x, active. It is used with a bundled LGPL FFmpeg 8.1 build.
- **PdfPig:** 0.1.14 stable, with 0.1.16 alphas in mid-2026.
- **AngleSharp:** 1.8+.
- **AssimpNetter:** replaces the unmaintained `AssimpNet`. It is a 6.x fork that tracks Assimp 6, and Assimp 6 no longer has a Blender importer, which is consistent with the `.blend` non-goal.
- **sqlite-vec:** active but pre-1.0 (0.1.9, March 2026). It is isolated behind `IVectorIndex`, with the in-memory fallback from D2. SQLite upstream reportedly added a `vec1` extension in September 2026, which could replace it later behind the same interface.
- **llama.cpp and three.js:** active. Versions are pinned and vendored (three.js r186, ES modules with relative imports, under `AssetManager.Shared/wwwroot/lib/three`).
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
