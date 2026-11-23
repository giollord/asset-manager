# Asset Manager

A local, offline-first catalog for game assets on Windows: 3D models, animations, images, textures, sprite sheets, sounds, music, fonts, video and documents. It imports folders, archives (zip, 7z, rar, tar) and Unity packages, including containers nested inside one another, without unpacking them on disk. It renders previews, reads licenses and links from the files that come with a pack, and can describe and tag everything with a local AI model. Browsing, search and previews keep working when the external drives with the originals are detached.

The specification and design live in [`openspec/changes/build-asset-manager`](openspec/changes/build-asset-manager): `proposal.md`, `design.md` (decisions D1–D11), the capability specs under `specs/`, and `tasks.md`.

## Requirements

- Windows 10 version 2004 (build 19041) or later, x64.
- .NET 10 SDK with the MAUI workload: `dotnet workload install maui-windows`.
- Internet access for the first build only, which downloads the pinned FFmpeg and llama.cpp binaries (see [Bundled native binaries](#bundled-native-binaries)).
- Optional: a GPU with Vulkan support for the AI model. Without one, the CPU build is used.

## Build and run

```powershell
git clone <repository> asset-manager
cd asset-manager
dotnet build AssetManager/AssetManager.csproj
dotnet run --project AssetManager/AssetManager.csproj
```

The first build downloads FFmpeg and llama.cpp, verifies their SHA-256 hashes, and keeps them in the git-ignored folders `AssetManager.Processing/native/ffmpeg` and `AssetManager.AI/native/llama`. Later builds work offline.

On first start, the app creates its catalog at `%LOCALAPPDATA%\AssetManager\catalog.amdb`. Settings → Catalog can open or create a catalog elsewhere.

### Tests

```powershell
dotnet test --project AssetManager.Tests/AssetManager.Tests.csproj
```

Tests that need the real AI models or the sample asset library are skipped unless these environment variables are set:

| Variable | Value |
|---|---|
| `ASSETMANAGER_AI_MODEL` | path of `gemma-4-E4B-it-Q4_K_M.gguf` |
| `ASSETMANAGER_AI_MMPROJ` | path of `gemma-4-E4B-it-mmproj-F16.gguf` |
| `ASSETMANAGER_AI_EMBED` | path of `embeddinggemma-300M-Q8_0.gguf` |
| `ASSETMANAGER_SAMPLES` | a folder of real asset packs (e.g. `E:/Assets`) |

Test classes that start a real model server belong to the non-parallel `AiModelCollection`, since several multi-GB models loading at once on one GPU time out. Timing tests run alone in the `Performance` collection.

## AI models

The AI is optional. Without a model, imports still produce metadata and previews, and assets are marked "not enriched". The models run locally through the bundled `llama-server`. Nothing is sent to a remote service.

Settings → AI model offers a one-click download of the recommended models from Hugging Face into `%LOCALAPPDATA%\AssetManager\models`. Downloads resume after interruption and are verified against pinned SHA-256 hashes:

| Package | Files | Used for |
|---|---|---|
| Gemma 4 E4B (Q4_K_M) | [`gemma-4-E4B-it-Q4_K_M.gguf`](https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF) (5.0 GB) and the vision projector `mmproj-F16.gguf` (1.0 GB) | descriptions, tags, licenses, links, orientation checks, collection descriptions |
| EmbeddingGemma 300M (Q8_0) | [`embeddinggemma-300M-Q8_0.gguf`](https://huggingface.co/ggml-org/embeddinggemma-300M-GGUF) (330 MB) | semantic search and "find similar" |

To use files you already have, pick them in Settings → AI model with "Pick file…". Any GGUF chat model that llama.cpp supports works; vision needs a matching `mmproj` file. Acceleration can be Auto (Vulkan GPU, else CPU), GPU or CPU.

## Architecture

The UI is Blazor with MudBlazor, hosted in a .NET MAUI `BlazorWebView` (WebView2). All logic is in plain .NET libraries, so the same UI also runs in a browser through the web dev host.

| Project | Contents |
|---|---|
| `AssetManager` | The MAUI Windows host: window, WebView2 setup (DevTools port, file drop), and the Windows platform services (clipboard file lists, folder and file pickers, open and reveal in Explorer, volume ids). |
| `AssetManager.Web` | Web dev host (Blazor Server) for fast UI iteration. Platform features (clipboard, pickers, Explorer) report "not supported" there. |
| `AssetManager.Shared` | The Razor UI: pages (Dashboard, Browse, Collections and tags, Sources, Import, Jobs, Settings), shared components (`AssetCard`, `AssetDetailsDrawer`, `BulkActionBar`, `ModelViewer`, `OriginalViewer`, `JobProgressView`) and the three.js preview renderer (`wwwroot/js/modelRenderer.js`). |
| `AssetManager.Core` | Domain entities, `Location` (volume id + path + entries inside nested containers), search queries, job and platform abstractions, app settings. |
| `AssetManager.Data` | The catalog: EF Core on SQLite with FTS5 full-text search, sqlite-vec vector search (with an in-memory fallback), migrations, the persistent job queue, statistics, and tag, collection, source, edit and deletion services. |
| `AssetManager.Processing` | Container readers (zip, 7z, rar, tar, gzip, `.unitypackage`), the virtual file system and extraction cache, location resolution, file classification and grouping, type handlers (image, model, audio, video, font, document), previews, the import staging pipeline, re-import, stored originals and export. |
| `AssetManager.AI` | The `llama-server` host and model downloads, structured prompts (pack pass, item pass, orientation judgment, collection descriptions), embeddings, semantic search, and the AI queue. |
| `AssetManager.Tests` | xUnit v3 and bUnit tests, with generated fixtures (zip, 7z, tar, Unity packages, FBX/OBJ scenes). |

Main ideas (details in `design.md`):

- **One catalog file.** Metadata, previews, settings, jobs and optional stored copies of originals live in one SQLite file. Originals stay where they are and are found by volume serial and relative path, so a drive that gets a new letter is still found.
- **Nested containers are virtual.** A zip inside a folder inside a Unity package is walked without unpacking it on disk. Single entries are extracted on demand into a size- and age-limited cache.
- **Staged imports.** An import scans, groups files into assets (a model with its textures, a sprite sheet), renders previews and runs the AI into staging rows. The user reviews and edits, then confirms; nothing enters the catalog before that. A new version of a cataloged package is detected and replaces the old one in place, keeping user edits.
- **Previews.** Images become WebP thumbnails. Models are rendered offscreen by three.js in the WebView from a GLB written by our own `GlbWriter` (Assimp loads the files): 24 views around the model. Animations become a looping animated WebP. Audio gets a short Opus clip and a waveform. Every size and threshold is a setting stored in the catalog.
- **Jobs.** Every long operation (import, re-import, previews, AI, store/unstore, export, compaction) is a persistent job with stages, progress, ETA, per-item errors, "Retry failed", and resume after a restart.

### Debugging the app

Debug builds of the MAUI app expose WebView2 DevTools on `127.0.0.1:9333` (`ASSETMANAGER_CDP_PORT` changes it; `0` disables it). `node tools/cdp.mjs` drives the running app from a terminal: `eval`, `text`, `click`, `type`, `nav`, `wait`, `shot`, `console`. The .NET log is written to `%LOCALAPPDATA%\AssetManager\logs\app-YYYYMMDD.log`.

### Web dev host

```powershell
dotnet run --project AssetManager.Web
```

It serves the same UI at `http://localhost:5022` and uses the same catalog and settings under `%LOCALAPPDATA%\AssetManager`. Run it or the MAUI app, not both at once, since both use the same catalog file.

## Bundled native binaries

| Component | Version | License | How it is included |
|---|---|---|---|
| [FFmpeg](https://ffmpeg.org) (LGPL shared build from [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)) | 8.1 | LGPL 2.1+ | Downloaded at build time, SHA-256 pinned in `AssetManager.Processing.csproj`, copied to `ffmpeg/` beside the app with its `FFMPEG.LICENSE.txt`. The DLLs are loaded dynamically, unmodified, by the separate `ffmpeg.exe`/`ffprobe.exe` processes. |
| [llama.cpp](https://github.com/ggml-org/llama.cpp) `llama-server` (Vulkan and CPU builds) | b12141 | MIT | Downloaded at build time, SHA-256 pinned in `AssetManager.AI.csproj`, copied to `llama/vulkan` and `llama/cpu` with its license. |
| [Assimp](https://github.com/assimp/assimp) through the AssimpNetter NuGet package | 6.x | BSD 3-Clause | NuGet package with the native library. |
| [ImageMagick](https://imagemagick.org) through Magick.NET | Q8 x64 | ImageMagick License (Apache 2.0 style) | NuGet package with the native library. |
| [sqlite-vec](https://github.com/asg017/sqlite-vec) `vec0.dll` | 0.1.9 | MIT | Checked in under `AssetManager.Data/native/win-x64` with its license. |
| [three.js](https://threejs.org) | r186 | MIT | Vendored ES modules under `AssetManager.Shared/wwwroot/lib/three` (see `VERSION.txt`). |
| [Roboto](https://github.com/googlefonts/roboto-classic) font | — | SIL Open Font License 1.1 | Bundled under `AssetManager.Shared/wwwroot/lib/roboto`, so the UI needs no internet. |

The AI models are not bundled. They are downloaded on request, and their licenses (the Gemma terms of use) apply to them.
