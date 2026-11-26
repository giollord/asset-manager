# Design

## Context

Facts measured on the user's portable install at `E:\AssetManager` (v0.2.0), 2026-11-26:

| What | Count / size |
|---|---|
| App files outside `data/` | ~825 files, 127 folders, 590 MB |
| …of which top level | 358 `.dll`, 46 `.winmd`, 51 `appicon*Tile/Logo*.png`, 5 `.pdb`, 4 `.pri`, ~100 locale folders |
| `llama/` | 51 files: `cpu/` and `vulkan/` are two near-identical llama.cpp b12141 builds |
| `wwwroot/` | 74 files (JS, CSS, fonts, `.br`/`.gz` duplicates) |
| `data/cache` | 16,053 files (extraction cache; user data, not part of the package) |
| `data/models` | 5.9 GB (Gemma 4 E4B Q4_K_M + mmproj F16 + embeddinggemma) |

From the log for the same day: a warm start loaded the chat model in 6.1 s. A cold start took 38 s from "started" to "Vector index", then **213 s** for the chat model and 5 s for embeddings. At the same time the drive was 100% busy with little throughput, which is the signature of random small reads.

How the code works today:
- Portable publish (`AssetManager.csproj`, `Portable=true`) is self-contained, `WindowsAppSDKSelfContained`, not single-file, not trimmed.
- `IStartupTask`s run once from the Blazor layout (`AppStartup`), so the window already exists when they run. `AiRuntimeStartupTask` (order 30) immediately starts the chat server, then the embedding server. `CacheCleanupStartupTask` (order 20) enumerates the whole cache in the background at the same moment.
- `LlamaServerHost` runs `llama-server -c <ctx*np> -np <n> --no-kv-unified -ngl 999 --jinja --reasoning off [--mmproj]` with default mmap loading. It tries Vulkan, then CPU, each from its own folder.
- `ItemPass.BuildPromptAsync` builds one user message whose first line already differs per request ("Enrich this game asset." / "Enrich these N game assets."). Next come pack name/description, instruction, fixed rules, the suggested-tags line, the per-item text, then the image or audio. `StructuredChat` puts a fixed system prompt first. llama-server's `cache_prompt` is on by default, but the shared prefix is only the system prompt. Gemma's sliding-window attention can also limit prefix reuse unless the server keeps a full-size SWA cache or context checkpoints.
- No request timings are read: the OpenAI-compatible response's llama.cpp `timings` object (`prompt_n`, `cache_n`, `prompt_ms`, `predicted_n`, `predicted_ms`) is ignored.

**Baseline** (`tools/measure-startup.ps1 -Folder E:\AssetManager`, v0.2.0, start 2026-11-26 17:48): 825 files in 137 folders outside `data/` (605 top-level entries: 480 files, 125 folders), 588 MB; started → vector index 37.3 s; chat server loaded in 213.2 s (ready 252.5 s after start); embedding server 5.0 s (ready 257.7 s after start).

## Goals / Non-Goals

**Goals:**
- A portable folder with a handful of entries, and far fewer files read at start.
- Cold start from a slow drive: the window and catalog appear quickly, and model loading uses sequential reads and is not slowed by other startup I/O.
- Measurable AI speed-up from prefix reuse, verified by logged `cache_n`.

**Non-Goals:**
- Shrinking `data/cache` or changing the extraction cache format (user data; a separate concern).
- An installer, MSIX, or moving the data folder off the portable folder. The WebView2 profile stays in `data/webview` to keep the portable promise.
- Changing the AI model, quantization or the enrichment output schema.
- Trimming (`PublishTrimmed`) or NativeAOT: MAUI/Blazor/EF reflection makes it risky, and the target is file count and I/O pattern, not size.

## Decisions

### D1. Single-file publish for the app, with native libraries self-extracted
Portable publish sets `PublishSingleFile=true` and `IncludeNativeLibrariesForSelfExtract=true` (plus `IncludeAllContentForSelfExtract` only if the spike shows WinUI needs content files on disk). Managed assemblies are mapped straight from the bundle. Native DLLs (WinAppSDK, Magick, assimp, sqlite, vec0) are extracted once per version to `%TEMP%\.net\AssetManager\<hash>` on the system drive. Later starts then load them from the internal disk, not the external one, which helps the user's scenario directly. `EnableCompressionInSingleFile` stays **off**, because decompressing ~250 MB on every start costs more than reading it.

*Alternatives:* (a) Keep the folder and only drop locales, tiles and PDBs. That cuts about 200 entries but leaves ~600 DLL/winmd files. It is the fallback if single-file fails (see D6). (b) A tiny launcher exe at the top with everything in a `bin\` subfolder. That looks tidy but does not reduce I/O; rejected unless D1 and (a) both fall short of the layout requirement.

*Assumption (recorded):* extracting native libraries to the user's temp folder is acceptable for a portable app. It is a cache that is recreated when missing, not user data, and the catalog and settings stay in `data/`.

### D2. Static web content embedded in the exe
`wwwroot` (including `_content/*` from MudBlazor and `AssetManager.Shared`, and `_framework/blazor.webview.js`) is embedded as resources at publish time. A `BlazorWebView` subclass overrides `CreateFileProvider` to return an embedded-resource file provider, with the physical provider as fallback for Debug builds. Pre-compressed `.br`/`.gz` copies are not embedded: the WebView serves from memory, so compression gains nothing.

*Alternative:* keep `wwwroot/` as a folder. That is simpler but leaves 74 files and a folder the layout requirement does not allow; only used if D6 forces it.

**Outcome (tasks 3.1, 4.x, 2026-11-26):** single-file works with MAUI + WinUI + BlazorWebView. The package is `AssetManager.exe` (260 MB), `portable`, `LICENSE.txt`, `wwwroot.zip` (0.9 MB), `llama/` (51 files), `ffmpeg/` (10) and `licenses/` (1): 66 files instead of 825. Notes from doing it:
- The Windows App SDK requires `IncludeAllContentForSelfExtract=true` for single-file (its own targets set it and error otherwise). So the **whole** bundle is extracted once per version to `%TEMP%\.net\AssetManager\<hash>` on the system drive, and later starts run from there. That is good for the slow-drive case: after the first start, the app's code is read from the internal disk.
- Because of that, `AppContext.BaseDirectory` is the extraction folder. The new `AppPaths.InstallFolder` (the exe's folder) is used for what ships beside the exe: the `portable` marker / data folder, `llama/` and `ffmpeg/`. Those items, `LICENSE.txt` and `licenses/` are `ExcludeFromSingleFile`.
- .NET never removes the unpacked folders of older versions (~240 MB each on the system drive). `SelfExtractCleanupStartupTask` deletes them 30 s after the window is ready, at background priority. It renames each folder first, which Windows refuses while another running copy has files open there, so a running older copy is never damaged. Checked live: the running copy's folder could not be renamed, and the two older folders were removed.
- The MSIX tooling that single-file needs drops the extension-less `portable` item, so it is copied by a post-publish target.
- D2 changed: static web assets (JS/CSS) are copied by their own publish step outside the bundle, so `ExcludeFromSingleFile=false` cannot pull them in. Instead, a post-publish target zips `wwwroot` (without `.br`/`.gz`) into `wwwroot.zip`, which `AppBlazorWebView` serves through `ZipFileProvider` (read into memory once, one sequential read). Without the zip (Debug, dev) the stock provider is used.
- Measured from E: with a cold cache: started → vector index 2.8 s on the first start (extracting), 3.3 s on the second (baseline 37 s). The window was shown within 5 s both times.
- Checked on this PC with the user's data copy: a no-AI import of PNGs, an OGG and an OBJ made Magick thumbnails, FFmpeg clip/spectrogram/waveform and 24 model views. The 3D viewer opens, and the exe and taskbar show the logo. (A stale `obj\Release\...
esizetizer
ppicon.ico` once produced the .NET icon in a local build; deleting it fixed it. CI builds from a clean checkout.)

### D3. Drop files that are never used unpackaged
- `SatelliteResourceLanguages=en` removes the locale folders.
- The tile/logo PNGs generated by `MauiIcon` are for MSIX; remove them from publish output with a target after `ComputeResolvedFilesToPublishList`, keeping `appicon.ico`.
- `DebugType=none` for Release portable publish of all projects (the 5 PDBs come from referenced projects).
- `onnxruntime.dll`/`DirectML.dll` (40 MB) come from Windows App SDK's Windows ML. Exclude them via the SDK's opt-out property or by removing them from the publish list. Verify that the app starts and renders without them.

### D4. One llama.cpp build
llama.cpp b12141 Windows builds load backends dynamically (`ggml-*.dll`), and the Vulkan build includes all `ggml-cpu-*` variants. On a machine without a usable Vulkan device, the Vulkan backend fails to initialise and llama-server should fall back to CPU. Ship only the Vulkan build in `llama/` (flattened, no `vulkan/` subfolder). Run CPU mode as the same exe with `-ngl 0` and `--device none` (or the build's equivalent to skip Vulkan init). `LlamaServerHost` keeps its "GPU, then CPU" retry, but on one exe. **Gate:** a spike must show the Vulkan build starts with `-ngl 0` when Vulkan is unavailable (simulated with `GGML_VK_VISIBLE_DEVICES=` / disabled ICD). If it does not, keep both builds but share the identical CPU DLLs. That is still about 25 fewer files.

**Outcome (tasks 5.1/5.2):** passed. The b12141 Vulkan build loads `ggml-vulkan.dll` dynamically. With no visible Vulkan device (`GGML_VK_VISIBLE_DEVICES=-1`), and with a broken `vulkan-1.dll` beside it (simulating a machine without a Vulkan loader), it lists no devices and serves on CPU. Only the Vulkan build is shipped, flattened into `llama/` (CPU mode adds `--device none`). `LlamaServerHostTests.One_server_build_runs_on_gpu_and_cpu` starts EmbeddingGemma in both modes.

### D5. Startup I/O ordering
- `AiRuntime.StartAsync` loads **embedding first** (300 MB, makes semantic search available), then chat. The embedding server is independent of chat, so search should not wait 3 minutes behind the big model.
- `AiRuntimeStartupTask` waits until the first catalog page has rendered (a signal from the layout, or at most 3 s after the startup tasks run). The WebView's own cold start then does not compete with a 5 GB read.
- `CacheCleanupStartupTask` waits until the AI runtime has left the `Starting` state (ready or failed) and runs at below-normal I/O priority. Eviction only needs to happen eventually.

**Outcome (task 9.3, 2026-11-26):** on the first start of the new build from `E:\AssetManager`, the window was ready ~4.5 s after the first log line, but the dashboard stayed empty for about a minute. Reproduced on a copy of the catalog on the same drive: the dashboard's queries take ~6 s from a cold drive on their own (2 s of counts, 4.3 s summing preview sizes), but llama-server memory-maps the 5 GB chat model from the same drive at normal I/O priority, and the catalog reads queued behind it. The dashboard filled in only at ~65 s, after the model was loaded. **First fix, reverted:** giving llama-server very low I/O priority made the dashboard complete at 16 s, but on the user's drive the chat model then never finished loading. At low priority Windows reads a memory-mapped file 4 KB per page fault: the drive did 118 reads/s at 0.5 MB/s (its seek limit) while the resumed Kenney scan moved the head, so 4.7 GB would have taken hours. **Fix:** a start order instead of priorities. "Window ready" now means the layout has rendered *and* the first page has its data (`StartupSignals.FirstLoad`, used by the Dashboard and Browse). The AI starts after that (fallback 60 s), at normal priority, and interrupted jobs resume only after the AI has loaded (fallback 2 min, `JobQueue.ResumeAiWait`). Checked on a copy of the catalog with the Kenney import interrupted: dashboard complete at 21 s, then embedding and chat models ready, then the import resumed; the window responded throughout.

### D6. Spike before committing to the layout
Single-file with WinUI/WinAppSDK self-contained plus MAUI plus BlazorWebView is the main technical risk: `.pri` resources, `Microsoft.ui.xaml.dll` activation and WebView2 loader may expect files beside the exe. The first task publishes with D1–D3 and runs the scenario "Compact package still runs" on a clean VM or Windows Sandbox. Files the platform still needs on disk (expected: some `.pri` files) are allowed by the spec ("at most 5 other files"). If more than 5 remain, fall back to D1-alternative (a) plus D2 and report the resulting count before going further.

### D7. Model loading with sequential reads

**Measured (task 1.2, 2026-11-26, E: = USB HDD, ~100 MB/s sequential, ~120 random IOPS):** Cold loads of Gemma 4 E4B Q4_K_M + mmproj F16 used the app's arguments, with the OS cache purged by opening each file unbuffered (`FILE_FLAG_NO_BUFFERING`). Alone: `--load-mode mmap` 44.7 s, `none` 48.1 s, `dio` 47.7 s. With a concurrent 4 KB random reader on the same drive: `mmap` 68.3 s, `none` 96.0 s, `dio` 97.2 s. **Decision: keep the default (`auto` = mmap); `--no-mmap`/direct I/O are slower.** The 213 s and >300 s loads seen in the app come from what else hammers the drive at start (see "Startup finding" below), not from mmap itself.

**Startup finding (2026-11-26):** the "stuck on the splash" start reproduces from a cold cache, with the window titled "(Not Responding)" for over 8 minutes. It also reproduces on a copy with **no AI models configured**. It disappears (window responsive ~15 s after launch, app ~1 read/s) when the interrupted import job is cancelled. That job is `Import Kenney`, stage Scanning, 18,320 entries, source `E:\Assets\Kenney` on the same drive. It restarts on every launch ("Resumed N pending jobs") and saturates the drive with random reads. Meanwhile UI-thread code waits on the disk: SQLite calls through Microsoft.Data.Sqlite run synchronously even when awaited, and Blazor Hybrid components run on the UI thread. The scan never finishes, because each launch is killed while hung and the job starts over. The original plan below was:
Add `--no-mmap` to llama-server by default, so llama.cpp reads the GGUF with large buffered sequential reads instead of page faults. With full GPU offload, weights are copied to VRAM anyway, so mmap gains nothing. The cost is a temporary RAM buffer during load on CPU mode, since the weights then live in RAM instead of the page cache. If b12141 offers direct I/O (`--direct-io`), measure it as a third option. **Measurement task:** cold-cache load time from E: (clear standby list with RAMMap or reboot) for mmap vs `--no-mmap`. Pick the faster for GPU, and keep mmap for CPU mode if `--no-mmap` doubles RAM there.

### D8. Prompt layout for prefix reuse
Each prompt builder (item sheet, model, audio, pack pass, texture/orientation judges, collection describer, consensus) is reordered as:
1. system prompt (unchanged, fixed)
2. **fixed rules** for that request kind: the "Describe what each item…", tags, animation-type and contact-sheet explanation lines, worded so they do not depend on item count
3. **per-source context**: pack name, pack description, user instruction, suggested tags (same for every request in one import of one source)
4. per-request variable text: "Enrich these N items", the item list
5. the image or audio

Items 1–3 are byte-identical for every request of one kind in one source, so llama-server's `cache_prompt` reuses their KV. The suggested-tags line can be hundreds of tokens, so this saves the most there. Unit tests lock this order: two requests built for the same source share a prefix that ends after the per-source context.

**Outcome (tasks 7.1/7.2):** the item prompt now starts with `ItemPass.AppendSharedPrefix` (fixed rules, then pack, instruction and suggested tags), then the request kind's line, "Enrich these N game assets" and the items. The orientation and texture judges run once per model/material, so their fixed rules come first, up to a `FixedEnd` line, and the model name, ranking and candidate list follow. The pack pass, consensus pass and collection description run once per source or collection, so they gain nothing from reuse and were left as they are; there is no separate `ImageQuery` prompt. Tests check that the shared start of each builder is byte-identical across requests.

### D9. Server flags so the prefix is actually reused
Gemma uses sliding-window attention. llama.cpp can reuse a prefix on SWA layers only with `--swa-full` (full-size SWA cache, more VRAM) or through context checkpoints (`--ctx-checkpoints`, on by default in recent builds), which restore state at saved points. With `-np 3 --no-kv-unified`, each slot keeps its own last prompt, and slot choice by prompt similarity sends a request to the slot with the best match. Plan: measure `cache_n` with D8 under defaults first. If `cache_n` stays ~0, try `--swa-full` and checkpoints, and keep the cheapest that gives reuse within the VRAM budget. Startup's existing "fewer slots on failure" fallback covers VRAM shortage. The image itself cannot be cached across requests, since each contact sheet is new.

### D10. Timings from the server
`StructuredChat` reads llama.cpp's `timings` from `ChatResponse.RawRepresentation` (OpenAI `ChatCompletion` → raw JSON) and adds them to a per-run accumulator held by the enrichment job. If the field is missing (another server), it records nothing. The job logs one info line at the end ("AI: 52 requests, prompt 41,200 tok (cache 28,900), 96 s; generated 38,000 tok, 610 s"). Each request is logged at debug level. These numbers also guide follow-ups: if generation dominates, output length is the next lever. That is outside this change, because it changes the enrichment output (Non-Goals).

**Outcome (tasks 7.3, 8.2, 8.3, 2026-11-26):** measured on the RTX 4070 with Gemma 4 E4B (3 slots), importing Kenney "alien-ufo-pack" (51 images, 2 sources) with full AI into an empty catalog, with the build before this change and after it:

| | before | after |
|---|---|---|
| import job (scan → previews → pack + item pass) | 50.8 s | 52.7 s |
| assets enriched | 51/51 | 51/51 |
| average tags / description length | 10.3 / 76 chars | 9.3 / 69 chars |

The new summary line for the "after" run: `9 requests; prompt 10,980 tokens (396 reused from cache), 6.4 s; generated 7,817 tokens, 112.3 s` (times summed over the parallel slots). **Generation is ~95% of the AI time; prompt processing, including the images, is ~5%.** Prefix reuse works (396 tokens) but cannot save more than a few seconds per pack on a GPU, so `--swa-full` and checkpoint tuning (D9) were not adopted: they cost VRAM for at most that gain. Descriptions and tags were of comparable quality; the differences are the normal variation of the pack-level tags. The real-model AI tests (96) pass. The lever that remains is output length (~150 generated tokens per asset), which changes the enrichment output and is outside this change (Non-Goals). On CPU, where prompt processing is far slower, the reuse should matter more; it was not measured.

### D11. Background jobs run at background I/O priority
Job code is marked with an ambient flag: an `AsyncLocal<bool>` created with a value-changed callback (`BackgroundIo` in Core). `JobQueue` sets it around each job run. The flag flows with the `ExecutionContext` into everything the job starts: awaits, `Task.Run`, `Parallel.ForEachAsync`. Whenever a thread starts or stops running that context, the runtime calls the callback, which switches the current thread into or out of Windows background mode (`SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN/END)`). That lowers its CPU, memory **and I/O** priority, so synchronous SQLite reads, file hashing, Magick.NET and assimp issued by jobs queue behind the window's reads. A per-thread flag records whether this mechanism switched the thread, so it only ends what it began. Threads that have a `SynchronizationContext` (the UI thread) are never switched, even if the context flows there. Off Windows the callback does nothing.

*Alternatives:* (a) A dedicated `TaskScheduler` with background-mode threads. Rejected after inspection: handlers move heavy work to `Task.Run`/`Parallel.ForEachAsync` (ImageHandler, MediaHandlers, ModelHandler, ImportStager, ImportEnricher …), which always use the default pool and would escape it. (b) An I/O priority hint per file handle (`FileIoPriorityHintInfo`): needs ~24 open sites and misses SQLite and native libraries. (c) Process background mode, which would slow the UI too.

*Limits:* threads created by native code (OpenMP in Magick) and child processes (ffmpeg, llama-server) do not inherit the mode. The cost is two cheap syscalls per context switch on pool threads running job code.

### D12. Delayed resume of interrupted jobs
`JobQueue.StartAsync` still marks running jobs pending and publishes their snapshots, so the jobs panel shows them as waiting. It writes them into the channels only after `AppStartup` signals that the layout has rendered the catalog, plus 5 s; a fixed fallback covers hosts without the signal. New jobs the user starts are queued immediately as before. The same "window ready" signal gates `AiRuntimeStartupTask` (D5).

### D13. Heavy catalog work off the UI thread
Microsoft.Data.Sqlite runs "async" commands synchronously on the calling thread. In Blazor Hybrid that is the UI thread whenever a component awaits a service. Two places do heavy reads from the UI thread:
- **Startup tasks** (`AppStartup.EnsureStartedAsync`, called from the layout): opening and migrating the catalog, vector index, job queue start. They now run with `Task.Run`, so the layout shows "Opening catalog…" instead of the splash freezing. No startup task needs the UI thread (checked: no dispatcher use in Core/Data/Processing/AI).
- **`CatalogStatsService`** (Dashboard counts, license audit, sources/collections/tags lists used by Dashboard, Sources, Collections and Browse filters): each computation is started with `Task.Run` (`ComputeOnThreadPool`, default on). These are the queries that scan the catalog.

Browse now applies the URL's query before awaiting its filter options. Otherwise the grid's first render would use the default sort while the options load.

*Tried and rejected (2026-11-26):* a global `DbCommandInterceptor` that ran every async command started from a thread with a `SynchronizationContext` on the thread pool. It worked, but every awaited query then really yielded. That changed render order on every page, exposed components that render before their data (e.g. `PreviewSettingsForm` indexing `_values` before load), and broke 22 bUnit tests. Some of those breaks were real races, such as a late load overwriting a just-chosen theme. Its benefit on the measured hang was small next to D11/D12. The reproduction with D11+D12 alone was responsive during the scan, except for one stall during startup tasks, which this decision covers.

*Tests:* bUnit page tests construct `CatalogStatsService` with `ComputeOnThreadPool = false` for deterministic rendering. `CatalogStatsTests.Stats_are_computed_off_the_calling_ui_thread` checks both modes from a thread with a synchronization context.

**Verified (task 2.5, 2026-11-26):** this was a cold-cache start of the portable branch build from `E:\AM-branch`, a copy of the user's data with the interrupted Kenney import pending and its extraction cache removed, so the scan really works on E:. The window responded in every 5 s sample over 94 s (before: "Not Responding" from 16 s on, indefinitely). The Dashboard was fully shown 16 s after launch. The import resumed 10 s after launch ("Resumed 1 pending jobs" after "window ready") and kept extracting (~10 files/s). With a Debug build on the same data and the scan running, Browse showed 70 cards 1.1 s after navigation, and Collections opened in 0.5 s. Background mode did not slow the scan while the window was idle (714 vs 727 files extracted in 60 s, off vs on).

**Side finding (not in scope):** extracting a pack's archives into `data/cache` on a USB HDD runs at ~10 small files/s. A first import of Kenney's archives takes tens of minutes, and its progress (Done/Total) does not move while it extracts.

### D14. Extraction cache on the system drive
Measured during the resumed Kenney import on a copy of the user's data on the USB hard drive: in 31 s the catalog's WAL did not change (SQLite wrote nothing), while the import created 10 files/s in `data\cache` and the drive did 57 writes/s at 99% busy, about 6 scattered NTFS metadata writes per extracted file. Transactions or another database would not help. The extraction cache therefore defaults to `%LOCALAPPDATA%\AssetManager\cache` (`AppPaths.DefaultCache`) for every mode, including portable copies; `AppSettings.CacheFolder` (Settings → Import) can move it, applied at the next start. The cache is disposable, so a portable copy loses nothing when moved to another PC. The old `data\cache` is no longer used and can be deleted.

### D15. Model loading and the jobs quick view
Loading the AI models shows in the jobs panel as "Loading AI models" (stages: embedding model, chat model; current item: the file), published by `AiRuntime` straight to the progress hub with id -1, since it is not a queued job (no Cancel). It ends as completed, or failed with the server's error. The app-bar indicator is a chip ("⟳ 2 jobs · 45%") that opens the Jobs page; hovering opens a popover in the app's panel colors (MudBlazor tooltips are light gray, which made the purple progress unreadable), refreshed every second while open, and the compact job view now shows the file being processed.

### D16. Models load when first needed
A session that only browses should not spend a minute of disk time and 6 GB of GPU memory on models. `AiRuntime` no longer loads at startup unless `AppSettings.AiLoadAtStartup` is on (Settings → AI model, off by default; the AI then counts as settled at once, so interrupted jobs need not wait for it). `EnsureChatAsync` and `EnsureEmbeddingsAsync` load one model if it is configured and not running:
- the chat model, from `ImportEnricher.WaitForModelAsync` (every AI pass, re-enrichment, consensus, collection descriptions) and from search by image;
- the embedding model, from `EmbeddingService` on the first query or document embedding, and from the background refresh only when some asset needs an embedding. The refresh also waits for the window.

The UI offers semantic and image search while the models are configured but not loaded (`EmbeddingService.IsAvailable`, `AiRuntime.VisionAvailable` from a configured mmproj). A model that failed to load is not retried until its settings change or the user presses "Load (or restart)", so a broken model file does not cost a load attempt per request. Each load shows as "Loading AI models" in the jobs panel (D15). The first AI request of a session waits for the load (seconds for embeddings, about a minute for the chat model from a USB drive). Models are not unloaded when idle; that could follow if GPU memory matters.

The extraction cache limit defaults to 4 GB (was 20 GB). Eviction already runs after every extraction (least recently used first, entries in use are skipped), and the walker releases each archive as soon as its entries are listed, so the limit holds during an import. The cost is that archives scanned early in a large import may be extracted again for the preview stage, which takes seconds on an SSD. Keeping the cache in RAM instead was considered and not done: FFmpeg, Assimp (models with external textures) and Unity package contents need real file paths, so it would mean a streaming rewrite of the container and media layers, for a small gain over an SSD (whose recent writes Windows serves from memory anyway).

## Risks / Trade-offs

- [WinUI/MAUI in single-file breaks at runtime: missing `.pri`, XAML resources, WebView2 loader] → D6 spike first on a clean machine; allow a few loose files; fallback layout.
- [Self-extracted natives land in `%TEMP%` and may be cleaned by disk cleanup] → .NET re-extracts automatically on the next start; first start after cleanup is slower once.
- [Antivirus scans a 250 MB exe on each start] → measure cold start before and after on the user's drive. If worse, compare with the folder fallback before shipping.
- [Embedded `wwwroot` breaks Debug hot reload] → physical provider in Debug, embedded only in portable publish.
- [`--swa-full` raises VRAM use and fails on small GPUs] → only enabled if measurements need it; the existing fallback to one slot or CPU applies.
- [Reordered prompts change answer quality slightly] → same validation; run the real-model tests (see memory note: `E4B` env vars, non-parallel classes) before and after and compare on a sample pack.
- [One llama build: CPU-only machines fail if Vulkan init crashes instead of failing cleanly] → D4 gate; keep two builds if the spike fails.
- [Deferring AI start delays enrichment by a few seconds] → negligible next to model load time.

## Migration Plan

- Users replace the old folder's program files with the new zip's contents and keep `data/`. Old loose DLLs left behind are harmless but should be deleted. The README "Download/Update" section says: extract to a new folder and move `data/` over, or delete everything except `data/` first.
- Rollback: the previous release zip still works with the same `data/` (no format changes).

## Open Questions

- Which of `--no-mmap` / mmap / direct I/O is fastest on the user's drive (D7). Measured during implementation; the choice does not change specs or tasks.
- Whether `--swa-full` is needed for reuse with Gemma 4 E4B in b12141 (D9). Measured; does not change specs or tasks.
