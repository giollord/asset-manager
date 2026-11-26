# Proposal

## Why

The portable build is about 830 files in 127 folders (358 DLLs, 46 `.winmd`, 51 tile PNGs, about 100 locale folders), and the user keeps it on an external drive. On 2026-11-26 a start from `E:\AssetManager` took minutes with the drive at 100% busy and low throughput, which points to many small random reads. The log shows the app reached "Vector index" 38 s after start, then the chat model took **213 s** to load. The same model loaded in 6 s when it was already in the OS cache. All of this reading competes for the same drive: app DLLs, the WebView, the cache scan over 16k cache entries, and 5.9 GB of models.

**Root cause of the hang (found during implementation):** the window stays on the splash and shows "Not Responding" even with no AI models. An interrupted import (`Import Kenney`, 18,320 files on `E:\Assets\Kenney`) restarts its scan at every launch and keeps the drive 100% busy with random reads. Database queries run synchronously on the UI thread, so the window waits behind the scan. With that job cancelled, the same cold start is responsive in about 15 s. The scan never completes, because each hung launch is killed and the job starts over.

AI enrichment of images is also slow. Every request starts with text that changes per request ("Enrich these 8 game assets…", pack name, item list), so llama-server cannot reuse the processed prompt between requests. Nothing in the app currently measures where request time goes: prompt, image or generation.

## What Changes

- **Compact package**: the portable build becomes one `AssetManager.exe` holding the .NET runtime, the app, WinUI/Windows App SDK and the Blazor `wwwroot`. Next to it are only `portable`, `LICENSE.txt`, `llama/` and `ffmpeg/` (separate programs, so they stay files). Locale folders, packaged-app tile images, PDBs and unused Windows ML binaries (`onnxruntime.dll`, `DirectML.dll`) are dropped. The CI package check enforces the layout.
- **One llama.cpp build** when the Vulkan build also runs on CPU, removing the duplicate `llama/cpu` folder (about 25 files).
- **Startup that does not fight itself for the disk**: the window shows first. The AI servers start after it, embedding model first, then chat model. Cache cleanup runs once the AI servers are up or failed, not at the same time as them.
- **Responsive window during background work**: job workers run with background (low) disk priority. Interrupted jobs are re-queued only after the window shows the catalog. Startup work and the catalog-wide statistics queries (Dashboard, Sources, Collections, Browse filters) run off the UI thread.
- ~~Faster model loading from slow drives~~: measurement showed memory-mapped loading is already the fastest on this drive, so the loading method stays unchanged.
- **Prompt reuse ("cache the state before the image")**: AI prompts are reordered so a long fixed prefix comes first: system prompt, instructions, suggested tags, pack context. The per-request item list and image come last, and llama-server is configured so that prefix is reused across requests, including for Gemma's sliding-window attention.
- **AI timing in the log**: each enrichment run logs how many prompt tokens were reused, prompt/image time and generation time. Optimizations are then verified by numbers, not guesses.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `app-distribution`: the portable package gets a compact layout requirement (one exe plus a few named entries) and a requirement that a start from a slow drive shows the window before the AI models load.
- `job-progress`: the window stays responsive while background jobs run, and interrupted jobs resume only after the window is ready.
- `app-data-location`: the extraction cache and the unpacked single-file runtime live on the system drive, so importing archives does not keep a slow portable drive busy.
- `ai-enrichment`: new requirements for reusing the shared prompt prefix across enrichment requests, for logging AI request timings, and for loading the AI models when they are first needed, with the load shown in the jobs panel.

## Impact

- `AssetManager/AssetManager.csproj` (portable publish properties: single-file, satellite languages, tile assets, embedded `wwwroot`), `MainPage.xaml(.cs)` / a `BlazorWebView` subclass for serving embedded static files.
- `AssetManager.AI/AssetManager.AI.csproj` (which llama.cpp builds are downloaded and shipped), `Runtime/LlamaServerHost.cs` (server arguments, backend fallback), `Runtime/AiRuntime.cs` (start order and deferral), `Enrichment/ItemPass.cs` and the other prompt builders (prompt order), `Enrichment/StructuredChat.cs` (timings).
- `AssetManager.Processing` `CacheCleanupStartupTask` (when it runs).
- `AssetManager.Data/Jobs/JobQueue.cs` (delayed resume, background I/O priority for workers), `AssetManager.Core/AppStartup.cs` and `CatalogStatsService` (run off the UI thread), and the Browse page (query applied before its first await).
- `.github/workflows/build.yml` package check (new required/forbidden paths, top-level entry limit).
- README "Download" section (what the folder contains, keeping models on a fast drive).
- No catalog, settings or data-folder format changes; existing portable `data/` folders keep working.
