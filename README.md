<p align="center"><img src="branding/logo.svg" width="160" alt="Asset Manager logo: a sci-fi crate and a metal trash bin with an old wooden barrel lying across them"></p>

# Asset Manager

[![Build](https://github.com/giollord/asset-manager/actions/workflows/build.yml/badge.svg)](https://github.com/giollord/asset-manager/actions/workflows/build.yml)

A local, offline-first catalog for game assets on Windows: 3D models, animations, images, textures, sprite sheets, sounds, music, fonts, video and documents. It imports folders, archives (zip, 7z, rar, tar) and Unity packages, including containers nested inside one another, without unpacking them on disk. It renders previews, reads licenses and links from the files that come with a pack, and can describe and tag everything with a local AI model. Browsing, search and previews keep working when the external drives with the originals are detached.

The specification lives in [`openspec/specs`](openspec/specs), one folder per capability. The original proposal, design (decisions D1–D11) and task list are archived in [`openspec/changes/archive/2026-11-24-build-asset-manager`](openspec/changes/archive/2026-11-24-build-asset-manager); later changes are under [`openspec/changes`](openspec/changes).

## Screenshots

![Browse: a search by meaning for "colorful game props" limited to 3D models, showing a grid of blasters, barrels and banners, with type, tag, license and availability filters on the left](docs/screenshots/browse.png)

| | |
|---|---|
| ![Asset details of a blaster model: a turntable of its stored renders, orientation controls, the AI description, tags and the CC0 license](docs/screenshots/details-model.png) | ![Dashboard: catalog totals, assets by type, AI enrichment, availability, licenses, the license audit and model issues](docs/screenshots/dashboard.png) |
| Asset details | Dashboard |

The screenshots show free CC0 packs by [Kenney](https://kenney.nl).

## Download

Get `AssetManager-<version>-win-x64-portable.zip` from the [Releases](https://github.com/giollord/asset-manager/releases) page, extract it anywhere (a local folder, a USB stick, the external drive that holds your assets), and run `AssetManager.exe`. Nothing needs to be installed: the package includes .NET, the Windows App SDK, the AI runtime and FFmpeg; only the WebView2 runtime that ships with Windows 10 and 11 is used from the system. The app keeps all its data in the `data` folder next to it (see [Data folder](#data-folder)). The AI models are not included; download them in Settings → AI model.

The extracted folder holds only a few entries:

| Entry | What it is |
|---|---|
| `AssetManager.exe` | The app with .NET and the Windows App SDK in one file |
| `wwwroot.zip` | The app's styles, scripts and fonts |
| `portable` | Marks the folder as portable: data goes to `data` next to the exe |
| `llama/` | The AI runtime (`llama-server`, Vulkan build; it also runs on CPU) |
| `ffmpeg/` | FFmpeg for audio and video previews |
| `licenses/`, `LICENSE.txt` | License texts |
| `data/` | Created on first start: catalog, settings, models, cache, logs |

On its first start, the exe unpacks its runtime once into `%TEMP%\.net\AssetManager` on the system drive (about 240 MB; older versions' copies are removed by the app); later starts load it from there, so the app starts quickly even from a slow USB drive. The AI models are not loaded at startup: they load the first time something needs them (an AI step of an import, search by meaning or by image), which the jobs panel shows as "Loading AI models". Settings → AI model can load them at startup instead. Models load faster from an internal SSD: to keep them there, pick the model files in Settings → AI model from any folder instead of the `models` folder in `data`.

The app works fully offline. It goes online only in two cases, both started by you: downloading an AI model in Settings → AI model, and the update check in Settings → About. The update check is off by default. When turned on, it asks GitHub once a day at startup (or when you press "Check now") for the latest release, sending nothing but the request itself, and shows "x.y.z available" with a link to the release page. It never downloads or installs anything. To update, extract the new zip to a new folder and move the old `data` folder into it, or replace everything in the old folder except `data`.

## Building from source

### Requirements

- Windows 10 version 2004 (build 19041) or later, x64.
- .NET SDK 11.0.100 or a later 11.0 feature band (pinned in `global.json`; the projects target `net10.0`) with the MAUI workload: `dotnet workload install maui-windows`.
- Internet access for the first build only, which downloads the pinned FFmpeg and llama.cpp binaries (see [Bundled native binaries](#bundled-native-binaries)).
- Optional: a GPU with Vulkan support for the AI model. Without one, the CPU build is used.

### Build and run

```powershell
git clone <repository> asset-manager
cd asset-manager
dotnet build AssetManager/AssetManager.csproj
dotnet run --project AssetManager/AssetManager.csproj
```

The first build downloads FFmpeg and llama.cpp, verifies their SHA-256 hashes, and keeps them in the git-ignored folders `AssetManager.Processing/native/ffmpeg` and `AssetManager.AI/native/llama`. Later builds work offline.

On first start, the app creates its catalog in its data folder (see below). Settings → Catalog can open or create a catalog elsewhere.

#### Tests

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

## Data folder

Everything the app keeps lives in one data folder: the default catalog (`catalog.amdb`), `settings.json`, catalog backups (`backups`), downloaded AI models (`models`), temporary files (`temp`), logs (`logs`) and the embedded browser profile (`webview`). The extraction cache (files unpacked from archives and packages while importing) is disposable and lives on the system drive, in `%LOCALAPPDATA%\AssetManager\cache`, even for a portable copy: unpacking writes thousands of small files, which an internal SSD handles instantly and a USB hard drive at about 10 per second. It is limited to 4 GB by default (Settings → Import): after each extraction, the least recently used entries above the limit are removed. Settings → Import can also move it. Settings → About shows the folder in use, the mode and an "Open folder" button.

| Mode | When | Data folder |
|---|---|
| Portable | The app folder contains a file named `portable` (the portable package ships it) | `data` next to `AssetManager.exe` |
| Standard | No `portable` file, e.g. a build run from the repository | `%LOCALAPPDATA%\AssetManager` |
| Fallback | A portable copy whose folder cannot be written (e.g. under `Program Files`) | `%LOCALAPPDATA%\AssetManager`; Settings → About says why |
| Custom | The `ASSETMANAGER_DATA_DIR` environment variable is set (any build) | That folder, created if needed |

Paths in `settings.json` that point inside the data folder (the catalog and model files) are stored relative to it. A portable copy can therefore be moved to another folder, or its drive can get another letter, and it still finds its catalog and models. Paths outside the data folder are stored as given.

To use an existing catalog from a portable copy (for example the one in `%LOCALAPPDATA%\AssetManager` from a build run from source), either copy `catalog.amdb` and the `models` folder into the portable `data` folder before the first start, or open it with Settings → Catalog → "Open another catalog" and pick the model files in Settings → AI model.

### Catalog backups

The app backs up the open catalog to the `backups` folder (Settings → Backups can move it, for example to another drive). Each backup is a complete catalog file named after the catalog and the time, such as `catalog-20261124-093000-auto.amdb`, taken as of one moment even while imports or AI enrichment keep writing. By default a backup is made at startup when the last one is more than a day old; this can be changed to weekly or turned off. The 5 newest automatic backups are kept (configurable); backups made with "Back up now" are never deleted automatically. A backup is written to a `.tmp` file first and needs free space for a copy of the catalog, so a full drive or a crash never leaves a broken backup.

To go back to a backup, choose "Restore" next to it in Settings → Backups. Running jobs are cancelled, the current catalog is saved as a manual backup first, and the app then reopens the catalog from the backup. A backup can also be opened directly with Settings → Catalog → "Open another catalog".

## Continuous integration and releases

[`.github/workflows/build.yml`](.github/workflows/build.yml) runs on GitHub Actions (`windows-latest`):

- **Every pull request to `master`** (and manual runs from the Actions tab): run the test suite with the pinned SDK. A failing test fails the run. Nothing is packaged. Pushes to `master` (including merges) do not start a run.
- **A version tag** runs the tests, then builds the portable package (with the MAUI workload), checks its contents with [`tools/check-package.ps1`](tools/check-package.ps1) (the compact layout above, the app's `LICENSE.txt`, the AI runtime and FFmpeg with their licenses, no `.gguf` models or debug symbols) and that `AssetManager.exe` reports the tag's version, and publishes a GitHub Release with `AssetManager-<version>-win-x64-portable.zip`. Nothing is released if a test or the package check fails:

  ```powershell
  git tag v1.2.0
  git push origin v1.2.0
  ```

  The tag must look like `v<major>.<minor>.<patch>`; the app then reports that version in Settings → About and in its log.

The version always comes from the git tags, through [MinVer](https://github.com/adamralph/minver) (a build-time package in [`Directory.Build.props`](Directory.Build.props)): a build of the commit tagged `v1.2.0` is `1.2.0`, and a build five commits later is `1.2.1-alpha.0.5`. Both CI jobs check out the full history with tags; a clone without them builds as `0.0.0-alpha.0`.

The llama.cpp and FFmpeg downloads are cached between runs, keyed on the csproj files that pin them. The same publish runs locally, with the version of the checked-out commit:

```powershell
dotnet publish AssetManager/AssetManager.csproj -c Release -f net10.0-windows10.0.19041.0 -p:Portable=true -o publish/AssetManager
```

## AI models

The AI is optional. Without a model, imports still produce metadata and previews, and assets are marked "not enriched". The models run locally through the bundled `llama-server`. Nothing is sent to a remote service.

Settings → AI model offers a one-click download of the recommended models from Hugging Face into the `models` folder of the [data folder](#data-folder). Downloads resume after interruption and are verified against pinned SHA-256 hashes:

| Package | Files | Used for |
|---|---|
| Gemma 4 E4B (Q4_K_M) | [`gemma-4-E4B-it-Q4_K_M.gguf`](https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF) (5.0 GB) and the vision projector `mmproj-F16.gguf` (1.0 GB) | descriptions, tags, licenses, links, orientation checks, collection descriptions |
| EmbeddingGemma 300M (Q8_0) | [`embeddinggemma-300M-Q8_0.gguf`](https://huggingface.co/ggml-org/embeddinggemma-300M-GGUF) (330 MB) | semantic search and "find similar" |

To use files you already have, pick them in Settings → AI model with "Pick file…". Any GGUF chat model that llama.cpp supports works; vision needs a matching `mmproj` file. Acceleration can be Auto (Vulkan GPU, else CPU), GPU or CPU.

## Unity project export

**Extract to folder** (in the selection bar) can write assets straight into a Unity project so they keep working:

- **Unity project export** writes each file's original `.meta` next to it, so Unity keeps its GUID and import settings. Files from a `.unitypackage` or a Unity project folder have one; other files are written without. The option is on by default when the target folder is inside a Unity project, meaning a parent folder has both `Assets` and `ProjectSettings`. If a file has to be renamed because of a name conflict, its `.meta` gets the same new name.
- **Include referenced files** also writes the files that the exported assets use from the same package or folder. References are followed across files: a prefab brings its meshes, materials and animator controllers, and each material brings its textures. Unity's built-in resources are left out. Before anything is written, the dialog lists these files, marked "dependency". Prefabs are cataloged (type Prefab, no preview), so they can be found and exported. Packages imported before this version pick up their prefabs on re-import.
- **GUID already in project:** if the target project already has one of the exported GUIDs at another path, the dialog lists those files first. "Skip conflicting" leaves them out. "Continue" writes them anyway, and Unity then gives one of the two a new GUID, which can break references to it.

## Keeping the catalog current

The catalog does not watch your drives. Instead, **Sources → Check for changes** (or **Rescan** on one source) compares imported sources with their files and lists what changed. Nothing in the catalog changes until you act on a result:

- **Folder sources** report added, changed (size or content) and removed files. Rescans follow the same ignored-folder and file-type rules as imports, so files an import skipped are not reported. "Import added files" imports the new files into the existing source; they get its source tags. "Re-import changed assets" refreshes their metadata and previews. "Remove missing assets" deletes the assets whose files are gone, after a confirmation.
- **Archive and package sources** report whether the file changed. "Update source" imports it again as the new version of that source and replaces it: assets that match keep their edits and tags, new ones are added and missing ones removed. A nested archive or package has its own "Update" button.
- **Sources on a detached drive** are listed as skipped, never as removed.

**Library folders** (Settings → Library) are the folders where new packs appear, such as a downloads folder or a folder of purchased packs on an external drive. They are stored per drive, like sources, so they still work when the drive gets another letter. Checking for changes also lists every folder, archive and `.unitypackage` directly inside them that is not imported yet, and marks the ones that look like a new version of an imported pack (same name apart from version numbers, or mostly the same files). Pick the ones you want and import them in one go. A pack with the same Unity Asset Store id as an imported one replaces it; a pack that only looks similar is imported as a new source, and you can delete the old one or use "Update source" on it instead.

**Check for changes at startup** (Settings → Library, off by default) runs both checks in the background about 30 seconds after the app starts. When it finds something, a notification opens the results; it never imports anything by itself.

## Keyboard shortcuts

In Browse, the asset grid works with the keyboard. `F` and the digits act on the selected assets, or on the focused one when nothing is selected. Shortcuts do not fire while you type in a text field. Press `?` in Browse to see this list in the app.

| Keys | Action |
|---|---|
| `← → ↑ ↓` | Move between assets |
| `Shift + arrows` | Extend the selection |
| `Ctrl + A` | Select all results |
| `Enter` | Open the details of the focused asset |
| `Esc` | Close the details, or clear the selection |
| `Space` | Play or stop a sound, or turn a 3D model's preview |
| `/` | Focus the search box |
| `F` | Toggle favourite |
| `0 – 5` | Set the rating (0 clears it) |
| `Delete` | Delete the selection from the catalog (asks first) |
| `Menu key, Shift + F10` | Open the menu of the focused asset (also a right click) |
| `?` | Show this list |

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

The logo masters are in `branding/`: `logo.svg` (the full group) and `logo-mark.svg` (the crate alone, used for favicons and the app bar). The app icon, splash, loading screen and favicon SVGs are copies of them, and `BrandingTests` fails when a copy drifts from its master.

Main ideas (details in `design.md`):

- **One catalog file.** Metadata, previews, settings, jobs and optional stored copies of originals live in one SQLite file. Originals stay where they are and are found by volume serial and relative path, so a drive that gets a new letter is still found.
- **Nested containers are virtual.** A zip inside a folder inside a Unity package is walked without unpacking it on disk. Single entries are extracted on demand into a size- and age-limited cache.
- **Imports commit on their own.** An import scans, groups files into assets (a model with its textures, a sprite sheet) and renders previews into staging rows that the catalog does not show yet. Once the previews are done, each source is committed in one transaction, without asking; the AI then enriches the committed assets in the background. Cancelling before the commit leaves no trace; afterwards it only stops the AI, and unwanted assets are deleted from the catalog. The import page then lists what was added per source and every skipped file with its reason. A package with the same Unity Asset Store id as a cataloged one replaces it in place, keeping user edits.
- **Previews.** Images become WebP thumbnails. Models are rendered offscreen by three.js in the WebView from a GLB written by our own `GlbWriter` (Assimp loads the files): 24 views around the model. Animations become a looping animated WebP. Audio gets a short Opus clip and a waveform. Every size and threshold is a setting stored in the catalog.
- **Jobs.** Every long operation (import, re-import, previews, AI, store/unstore, export, compaction) is a persistent job with stages, progress, ETA, per-item errors, "Retry failed", and resume after a restart.

### Debugging the app

Debug builds of the MAUI app expose WebView2 DevTools on `127.0.0.1:9333` (`ASSETMANAGER_CDP_PORT` changes it; `0` disables it). `node tools/cdp.mjs` drives the running app from a terminal: `eval`, `text`, `click`, `type`, `nav`, `wait`, `shot`, `console`. The .NET log is written to `logs\app-YYYYMMDD.log` in the [data folder](#data-folder) (`%LOCALAPPDATA%\AssetManager` for a build run from the repository).

### Web dev host

```powershell
dotnet run --project AssetManager.Web
```

It serves the same UI at `http://localhost:5022` and uses the same catalog and settings under `%LOCALAPPDATA%\AssetManager`. Run it or the MAUI app, not both at once, since both use the same catalog file.

## Bundled native binaries

| Component | Version | License | How it is included |
|---|---|
| [FFmpeg](https://ffmpeg.org) (LGPL shared build from [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)) | 8.1 | LGPL 2.1+ | Downloaded at build time, SHA-256 pinned in `AssetManager.Processing.csproj`, copied to `ffmpeg/` beside the app with its `FFMPEG.LICENSE.txt`. The DLLs are loaded dynamically, unmodified, by the separate `ffmpeg.exe`/`ffprobe.exe` processes. |
| [llama.cpp](https://github.com/ggml-org/llama.cpp) `llama-server` (Vulkan build, which also carries the CPU backends) | b12141 | MIT | Downloaded at build time, SHA-256 pinned in `AssetManager.AI.csproj`, copied to `llama/`. CPU mode runs the same server without the GPU. Its MIT license (from the same tag; the release zips do not include it) is checked in under `AssetManager.AI/licenses` and copied to `llama/LICENSE.txt`. |
| [Assimp](https://github.com/assimp/assimp) through the AssimpNetter NuGet package | 6.x | BSD 3-Clause | NuGet package with the native library. |
| [ImageMagick](https://imagemagick.org) through Magick.NET | Q8 x64 | ImageMagick License (Apache 2.0 style) | NuGet package with the native library. |
| [sqlite-vec](https://github.com/asg017/sqlite-vec) `vec0.dll` | 0.1.9 | MIT | Checked in under `AssetManager.Data/native/win-x64` with its license. |
| [three.js](https://threejs.org) | r186 | MIT | Vendored ES modules under `AssetManager.Shared/wwwroot/lib/three` (see `VERSION.txt`). |
| [Roboto](https://github.com/googlefonts/roboto-classic) font | — | SIL Open Font License 1.1 | Bundled under `AssetManager.Shared/wwwroot/lib/roboto`, so the UI needs no internet. |

The AI models are not bundled. They are downloaded on request, and their licenses (the Gemma terms of use) apply to them.

## License

Asset Manager is released under the [MIT License](LICENSE). The portable package includes it as `LICENSE.txt` next to `AssetManager.exe`. Third-party components keep their own licenses; see [Bundled native binaries](#bundled-native-binaries).
