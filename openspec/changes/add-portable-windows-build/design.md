# Design

## Context

- **Where data goes today:** every path comes from the static `AppPaths.Root` (`AssetManager.Core/Settings/AppSettings.cs`), hard-wired to `%LOCALAPPDATA%\AssetManager`. The catalog default, `settings.json`, `models`, `cache`, `temp`, the Serilog `logs` folder and `ModelDownloads.Folder` all derive from it.
  - `AppSettings.CatalogPath` reads `AppPaths.Root` when a settings object is constructed, so the root must be final before the settings store loads.
  - Two places bypass `AppPaths` and use `%TEMP%`: `AssetExporter.TempRoot` (clipboard staging) and the FFmpeg scratch folder in `Ffmpeg.cs`.
  - The WebView2 profile goes wherever MAUI's default puts it: `%LOCALAPPDATA%\<app>\…`.
- **Settings file:** `settings.json` stores absolute paths for the catalog and the three model files.
- **The MAUI app** (`AssetManager.csproj`) is unpackaged (`WindowsPackageType=None`) and has only ever been built in Debug. Debug-only code (the CDP port and offline test mode in `MauiProgram.cs`) is under `#if DEBUG`.
- **Native binaries:** llama.cpp (Vulkan and CPU) and FFmpeg are downloaded by MSBuild targets on first build into `AssetManager.AI/native/llama/` and `AssetManager.Processing/native/ffmpeg/` (SHA-256 pinned, about 290 MB).
- **Toolchain:** the projects target `net10.0`, but the machine builds them with SDK 11.0.100 and workload `maui-windows` 11.0.100. `global.json` pins only the test runner, not the SDK.
- **Tests:** tests that need real AI models are skipped unless their environment variables are set (see README). The full suite takes a few minutes.

## Goals / Non-Goals

**Goals:**
- One decision point for the data folder at startup, before logging, settings or DI run.
- The CI build is reproducible from a clean runner using the same SDK and workload as local builds.
- The portable zip is produced by the same `dotnet publish` command locally and in CI.

**Non-Goals:**
- Installer, MSIX, auto-update, code signing, ARM64, other operating systems.
- Moving existing `%LOCALAPPDATA%` data into a portable folder.
- Bundling AI models.

## Decisions

**1. Portable mode is marked by a file next to the exe.** A publish with `-p:Portable=true` adds an empty `portable` file beside `AssetManager.exe`. At startup, `AppDataLocation.Resolve` picks the root in this order:
1. The `ASSETMANAGER_DATA_DIR` environment variable, if set.
2. `<exe dir>\data`, if the marker exists and the folder is writable. The check creates the folder, then writes and deletes a probe file.
3. `%LOCALAPPDATA%\AssetManager`. This is a fallback, with the reason recorded, when the marker exists but the folder is not writable; otherwise it is the development default.

The result (root, mode `Portable | Development | Override | Fallback`, reason) is assigned to `AppPaths` once, as the first statement of `MauiProgram.CreateMauiApp`, and registered as a singleton for Settings to show.
- *Alternative:* a compile-time constant or assembly attribute. It cannot be changed without a rebuild, and a user cannot turn a copied dev build into a portable one by adding a file.
- *Alternative:* always storing data next to the exe. Rejected because cleaning `bin/` would wipe the developer's catalog and models.

**2. Everything the app writes goes under the root.**
- `AppPaths` gains `Logs`, `WebView` and an `Export` subfolder of `Temp`. `AssetExporter.TempRoot` and the FFmpeg scratch folder use `AppPaths.Temp` instead of `%TEMP%`.
- `MauiProgram` sets `WEBVIEW2_USER_DATA_FOLDER` to `AppPaths.WebView` before the BlazorWebView is created. It already sets `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` the same way.
- Third-party libraries may still use the system temp folder internally. That is accepted.
- `AssetManager.Web` (the dev web host) keeps the development default. It never runs portable.

**3. Relative paths are handled in the settings store.** On save, `JsonAppSettingsStore` turns `CatalogPath`, `ChatModelPath`, `MmprojPath` and `EmbeddingModelPath` into paths relative to the root when they are inside it. On load, it resolves relative paths against the current root. In memory the settings stay absolute, so no consumer changes. Existing absolute paths keep working, and the next save rewrites them as relative if they are inside the root.
- *Alternative:* storing a `%DATA%` token. Rejected: plain relative paths are what users expect when they read the file, and they also work when the drive letter changes.

**4. Publish settings live in the csproj, under a `Portable` condition.** The settings are `SelfContained=true`, `RuntimeIdentifier=win-x64`, `WindowsAppSDKSelfContained=true` and `PublishReadyToRun=false`, with no trimming, because MAUI on Windows does not support it. The marker file is included only when `Portable=true`. The publish command is:

```
dotnet publish AssetManager/AssetManager.csproj -c Release -f net10.0-windows10.0.19041.0 -p:Portable=true -p:Version=<ver> -p:ApplicationDisplayVersion=<ver> -o publish/AssetManager
```

The version also goes into the Serilog start line and into Settings → About, which shows the version, the data folder and the mode.

**5. One workflow, `.github/workflows/build.yml`, on `windows-latest`.**
- **Triggers:** push and pull requests to `master`, tags `v*.*.*`, and `workflow_dispatch`.
- **Toolchain:** `actions/setup-dotnet` reads `global.json`, which now pins `sdk.version` to `11.0.100` (`rollForward: latestFeature`). Then `dotnet workload install maui-windows`.
- **Cache:** `actions/cache` covers the two `native/` download folders, keyed on the hashes of `AssetManager.AI.csproj` and `AssetManager.Processing.csproj`, so llama.cpp and FFmpeg are fetched only when their pinned versions change.
- **Steps:** build, then `dotnet test --project AssetManager.Tests/AssetManager.Tests.csproj`, then publish. The zip is created with `Compress-Archive` and named `AssetManager-<ver>-win-x64-portable.zip`.
- **Version:** from a tag it is the tag without the `v`. Otherwise it is `0.0.0` and the zip is named with the short SHA.
- **Outputs:** branch and manual runs upload the zip with `actions/upload-artifact`. Tag runs create the release with `gh release create <tag> <zip> --generate-notes`, using `GITHUB_TOKEN` with `contents: write` set only on that job. The release job needs the build-and-test job, so a failed test publishes nothing.

**6. Settings → About.** A small new section shows:
- the version;
- the mode (Portable, Development, Override or Fallback, with the reason);
- the data folder path, with "Open folder" through the existing `IShellService.OpenAsync`.

## Risks / Trade-offs

- [The app has never been built in Release or published self-contained, so issues may appear only there: `#if DEBUG` paths, XAML source generation, static web assets, native DLLs from NuGet packages missing in publish output.] → Smoke-test the published folder locally on this machine before writing the workflow: start it, import, preview, run the AI, export. Also test on a machine or VM without the .NET runtime, or at least with `DOTNET_ROOT` unset.
- [Hosted runners may not have SDK 11.0.100 or matching MAUI workload versions.] → `setup-dotnet` installs the exact SDK from `global.json`. Pin the workload version through the SDK's workload set so CI resolves the same `maui-windows` version as local builds.
- [A data folder on a slow USB drive makes the catalog, cache and model loading slower.] → That is the point of portable mode. The override variable and the existing "open another catalog" setting let users put the catalog or models elsewhere.
- [The zip is large: about 300 MB with both llama.cpp builds and FFmpeg.] → Acceptable for a release asset. The Vulkan and CPU builds are both needed for the GPU-then-CPU fallback.
- [Tests in CI might depend on things only present on the dev machine, such as a GPU, fonts or WebView2 for the model renderer.] → The first CI run decides. Fix tests to skip cleanly when a dependency is missing, rather than excluding whole test classes.
- [A wrong relative-path conversion could lose the catalog path.] → Convert only paths that are strictly inside the root, with full-path comparison, case-insensitive on Windows. Cover it with unit tests, including a moved root.

## Migration Plan

- Development builds keep the same folder, so nothing changes for the existing `%LOCALAPPDATA%` data.
- Settings files with absolute paths are read as before.
- First release: merge, push tag `v0.1.0`, download the zip from the release, run it from a fresh folder.
- Rollback: delete the release and tag. The workflow has no effect on the app's behaviour.
