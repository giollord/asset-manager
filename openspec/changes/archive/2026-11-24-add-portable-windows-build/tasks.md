# Tasks

## 1. Data folder resolution

- [x] 1.1 Add `AppDataLocation.Resolve(exeDir, environment)` in `AssetManager.Core`. It returns the root, the mode (`Portable`, `Development`, `Override` or `Fallback`) and the fallback reason, in the order from design decision 1 (override variable `ASSETMANAGER_DATA_DIR`, then the `portable` marker plus a writable `data` folder probed with a temp file, then `%LOCALAPPDATA%\AssetManager`). Verify with unit tests for all four modes, including a read-only `data` folder that leads to `Fallback` with a reason.
- [x] 1.2 Extend `AppPaths` with `Logs`, `WebView` and `Export`. Move `AssetExporter.TempRoot` and the FFmpeg scratch folder from `%TEMP%` to `AppPaths.Temp`. Verify that the existing exporter and FFmpeg tests pass and that a grep finds no `GetTempPath` in app code outside the design-time `CatalogDbContext` factory.
- [x] 1.3 In `MauiProgram.CreateMauiApp`, resolve the location first, assign `AppPaths.Root`, register the result as a singleton, point Serilog at `AppPaths.Logs` and set `WEBVIEW2_USER_DATA_FOLDER` to `AppPaths.WebView`. Verify by running the Debug build: logs and settings stay in `%LOCALAPPDATA%\AssetManager` (Development mode). Then add a `portable` file next to the Debug exe and run again: `data\` next to the exe gets `catalog.amdb`, `settings.json`, `logs\` and `webview\`, and `%LOCALAPPDATA%\AssetManager` is untouched. Delete the marker and that `data\` folder afterwards.

## 2. Relocatable settings

- [x] 2.1 In `JsonAppSettingsStore`, save `CatalogPath`, `ChatModelPath`, `MmprojPath` and `EmbeddingModelPath` relative to `AppPaths.Root` when they are strictly inside it (full paths, case-insensitive), and resolve relative paths against the root on load. Verify with unit tests: a path inside the root round-trips as relative; a path outside the root stays absolute; a settings file written under one root and loaded under another (the moved-folder case) resolves to the new location; an old file with absolute paths still loads.
- [x] 2.2 Check that `CatalogLocationService.SwitchAsync` and the model picker and downloader store their paths through the store unchanged, so the relative handling applies. Verify with a test that downloads (fake) into `AppPaths.Models` and reloads the settings from a moved root.

## 3. Settings → About

- [x] 3.1 Add an About section to `Settings.razor`: app version, mode (Portable, Development, Override, or Fallback with its reason), and the data folder with an "Open folder" button that uses `IShellService.OpenAsync`. Verify with a bUnit test for the Portable and Fallback texts, and in the running app.
- [x] 3.2 Update README: data folder locations per mode, the `ASSETMANAGER_DATA_DIR` override, relative paths, and how to reuse an existing `%LOCALAPPDATA%` catalog from a portable copy. Remove the hard-coded `%LOCALAPPDATA%` wording from the model download text. Verify the README describes every mode in the `app-data-location` spec.

## 4. Portable publish

- [x] 4.1 Pin the SDK in `global.json` (`11.0.100`, `rollForward: latestFeature`) next to the existing test runner setting. Verify that `dotnet --version` in the repo reports 11.0.x and that the full build and tests still pass.
- [x] 4.2 Add the `Portable` publish settings to `AssetManager.csproj`: self-contained, `win-x64`, Windows App SDK self-contained, the `portable` marker file only when `Portable=true`, and the version passed in through `Version` and `ApplicationDisplayVersion`. Log the version at startup. Verify that the publish command from design decision 4 completes, that the output contains `AssetManager.exe`, `portable`, `llama/vulkan`, `llama/cpu`, `ffmpeg` with their license files and no `.gguf`, and that the log shows the passed version.
- [x] 4.3 Smoke-test the published folder on this machine, copied to a new folder outside the repo. Verify:
  - it starts in Portable mode with `data\` beside it;
  - a small folder import (No AI) produces previews;
  - models picked from `%LOCALAPPDATA%\AssetManager\models` enrich assets on the GPU;
  - export works;
  - moving the whole folder to another path keeps the catalog and models.

  Fix any Release-only failures found, each with a test where possible.

## 5. GitHub Actions

- [x] 5.1 Add `.github/workflows/build.yml` as in design decision 5:
  - triggers;
  - `setup-dotnet` from `global.json`;
  - `maui-windows` workload;
  - cache for the `native/` download folders;
  - build, test and publish steps;
  - zip naming;
  - artifact upload for non-tag runs;
  - a release job with `contents: write`, gated on build and tests, that runs `gh release create` for `v*.*.*` tags.

  Verify that `actionlint` (or a YAML lint) passes locally.
- [x] 5.2 Make the test suite pass on a clean `windows-latest` runner, and package only for version tags (scope change from the user: pull requests and pushes run the tests only). Push a branch and read the run; fix tests that assume dev-machine state so they skip cleanly with a reason rather than fail. Verify that a pull-request run is green, runs only the `test` job, and produces no package.
- [x] 5.3 Document the CI and release process in README (what runs on push and pull requests, how to cut a release by pushing `vX.Y.Z`, where to download the zip) and add a build status badge. Verify the documented tag command matches the workflow trigger.

## 6. Release verification

- [x] 6.1 With the user's go-ahead, push tag `v0.1.0`. Verify that the GitHub Release `v0.1.0` exists with `AssetManager-0.1.0-win-x64-portable.zip`. Download it, extract it to a fresh folder, and check that Settings → About shows version 0.1.0 and Portable mode, and that a small import works.

## Workflow follow-up

- Archive the change after review with `openspec-archive-change`.
