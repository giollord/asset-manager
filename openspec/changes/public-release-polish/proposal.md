# Proposal

## Why

The repository is now public, but it has no license, so legally nobody may use or fork the code. The app still ships the MAUI template's identity: a purple square with the ".NET" mark as its icon and splash screen, a `com.companyname` app id, and unused template files. This change gives the project a license and its own visual identity, and removes the template leftovers and stray tracked files.

## What Changes

- Add an MIT `LICENSE` at the repository root, taken verbatim from GitHub's license API (`gh api /licenses/mit`). Only the `[year]` and `[fullname]` placeholders are filled in. Ship it in the portable package as `LICENSE.txt`, have the release check require it, and state the license in the README.
- Design an original logo as SVG: an isometric (2:1 dimetric, about 26.6°) group of a **futuristic sci-fi crate** and a **metal trash bin** standing side by side, with an **old wooden barrel** lying on top across both. A small version (the sci-fi crate alone) is used where the full group would be unreadable.
- Use the logo everywhere the app shows an identity:
  - the Windows app icon (exe, taskbar, title bar)
  - the splash screen
  - the favicon of the MAUI WebView page and of the web dev host
  - the app bar next to the title
  - the README header

  Replace the MAUI purple `#512BD4` with the logo's background colour.
- Replace the template identity: `ApplicationTitle` "Asset Manager", `ApplicationId` `io.github.giollord.assetmanager`, the manifest's `User Name` placeholders, and the exe's file metadata (product, company, copyright, description).
- Remove `Resources/Images/dotnet_bot.svg`, `Resources/Raw/AboutAssets.txt` and their csproj entries. Untrack `TestResults/` (ignored, but committed before the ignore rule).

Out of scope:
- downloading `vec0.dll` at build time instead of keeping it checked in
- rewriting git history to change the commit email
- GitHub repository settings (branch protection, tag rulesets)
- update checks, code signing, winget

## Capabilities

### New Capabilities
- `app-branding`: the app's visual identity (logo artwork and its variants) and where it appears (app icon, splash, favicons, app bar, README), plus the app's name and file metadata.

### Modified Capabilities
- `app-distribution`: the repository is licensed under MIT, and the portable package includes that license. The package check requires it.

## Impact

- `LICENSE` (new), `README.md`, `.github/workflows/build.yml` (package check)
- `AssetManager/AssetManager.csproj` (icon, splash, title, id, metadata, license file in publish), `AssetManager/Resources/**`, `AssetManager/Platforms/Windows/Package.appxmanifest`, `AssetManager/wwwroot/index.html`
- `AssetManager.Shared/Layout/MainLayout.razor`, `AssetManager.Shared/wwwroot/` (favicon, logo files)
- New `branding/` folder with the master SVGs and a README image
- No change to the data folder: `%LOCALAPPDATA%\AssetManager` does not depend on the app id.
