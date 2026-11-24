# Proposal

## Why

The app can only be run by building it from source, and everything it creates (catalog, settings, AI models, cache, logs) goes to `%LOCALAPPDATA%\AssetManager`. A portable download that runs from any folder, including the external drive that holds the assets, and keeps all its data beside itself, makes the app easy to install, move between machines and back up. Building it on GitHub Actions makes every release reproducible, so it no longer depends on one developer machine.

## What Changes

- A GitHub Actions workflow builds and tests the app on Windows for every push and pull request to `master`.
- The same workflow publishes a **portable Windows x64 package**: a self-contained Release build, needing no .NET, Windows App SDK or other installs (only the WebView2 runtime that Windows 10/11 ships). It includes the bundled `llama-server`, FFmpeg and native libraries, but not the AI models. Pushing a version tag (`v1.2.3`) attaches the zip to a GitHub Release with that version. Manual runs and branch builds keep the zip as a workflow artifact.
- In the portable build, the app keeps its data in a `data` folder next to `AssetManager.exe` by default:
  - the catalog;
  - `settings.json`;
  - downloaded models;
  - the extraction cache and app temp files;
  - logs;
  - the WebView2 profile.
- If that folder cannot be written (for example under `Program Files`), the app falls back to `%LOCALAPPDATA%\AssetManager` and says so in Settings.
- Development builds (Debug, run from the repo) keep using `%LOCALAPPDATA%\AssetManager`, so cleaning `bin/` never deletes a catalog. An environment variable overrides the data folder for both.
- Paths in the settings that point inside the data folder (the catalog, models picked or downloaded there) are stored relative to it. A portable folder that moves, or a drive that gets a different letter, keeps working. Paths outside the data folder stay absolute, as today.
- Settings shows the data folder and whether the app runs in portable mode, with a button to open the folder.

Assumptions (made for this proposal, open to review):
- AI models are not bundled. They are 6 GB, above what a release asset should be, and their own license terms apply. The portable app downloads them on request into its `data\models` folder, as the installed app does today.
- Existing data in `%LOCALAPPDATA%\AssetManager` is not moved automatically. A user switching to the portable build can open the old catalog via Settings → Catalog ("open another catalog") or copy the folder over.
- Only Windows x64 is built. There is no installer, MSIX or code signing.

## Capabilities

### New Capabilities
- `app-distribution`: how the app is built, tested and packaged for users (the CI workflow, the portable package contents, versioned releases).
- `app-data-location`: where the app keeps its data (portable versus development defaults, the fallback, the override, relative paths in the settings, and how Settings shows it).

### Modified Capabilities
<!-- none: no existing requirement states where app data lives -->

## Impact

- **New:** `.github/workflows/` (build, test, publish and release workflow).
- **Build:**
  - `AssetManager/AssetManager.csproj`: Release publish settings (self-contained, `win-x64`, Windows App SDK self-contained) and a portable marker file included only in the portable publish.
  - `global.json`: pins the SDK so CI and local builds match.
  - The version comes from the tag.
- **Runtime:**
  - `AppPaths` (`AssetManager.Core/Settings/AppSettings.cs`): resolves the data folder: portable, development, fallback or override.
  - `JsonAppSettingsStore`: relative paths.
  - `MauiProgram.cs`: logs and the WebView2 user data folder.
  - `ModelDownloads`, the catalog path default and `ExtractionCache` already go through `AppPaths`.
- **UI:** Settings shows the data folder.
- **Docs:** README gets download, portable-mode and data-folder sections.
- **CI:** first builds download llama.cpp and FFmpeg, about 290 MB, so the download folders are cached. Unit tests run in CI, while the tests that need real AI models stay skipped there, as they are now without their environment variables.
