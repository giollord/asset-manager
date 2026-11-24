# Tasks

## 1. License

- [x] 1.1 Fetch the MIT text with `gh api /licenses/mit --jq .body`, fill in only `[year]` → `2026` and `[fullname]` → `Daniil Barkov`, and save it as `LICENSE` at the repository root. Verify that a diff against the API output shows only the copyright line changed.
- [x] 1.2 Publish `LICENSE` next to the exe as `LICENSE.txt` (a `None` item linked from `AssetManager.csproj` with `CopyToPublishDirectory`), and add `LICENSE.txt` to the `$required` list in the workflow's "Check package contents" step. Verify with a local portable publish (the command in the csproj comment) that `publish/AssetManager/LICENSE.txt` exists.
- [x] 1.3 Add a "License" section to the README stating that the app is MIT licensed, linking to `LICENSE` and pointing to "Bundled native binaries" for third-party licenses. Mention `LICENSE.txt` in the CI section's package-check sentence. Verify that the README renders and the links resolve.

## 2. Repository cleanup and app identity

- [x] 2.1 Untrack `TestResults/` (`git rm -r --cached TestResults`; it stays ignored). Delete `Resources/Images/dotnet_bot.svg` and `Resources/Raw/AboutAssets.txt`, and remove the `dotnet_bot` `MauiImage Update` line; keep the generic `MauiImage`/`MauiAsset` globs. Verify that `git ls-files TestResults` is empty and that `dotnet build AssetManager/AssetManager.csproj -f net10.0-windows10.0.19041.0` succeeds.
- [x] 2.2 Set `ApplicationTitle` "Asset Manager" and `ApplicationId` `io.github.giollord.assetmanager`, plus `Product`, `Company`, `Authors`, `Copyright` and `Description` as listed in design.md "App identity values". Set the `index.html` `<title>` to "Asset Manager". Replace the `Package.appxmanifest` placeholders (`User Name`, `$placeholder$` display names and description) with the same values. Verify that the built exe's Properties → Details shows them; with `-p:Version=1.2.0`, it also shows version 1.2.0.
- [x] 2.3 Start the rebuilt app over an existing data folder from before the id change. Verify that the same catalog, settings and theme open (spec scenario "Existing data kept"). If the WebView2 profile turns out to depend on the id in a way that loses user state, revert the id and note why in design.md.

## 3. Logo artwork

- [x] 3.1 Draw `branding/logo.svg` by hand on the 2:1 dimetric grid in a 512×512 viewBox, following design.md "Composition" and "Palette":
  - the sci-fi crate and the metal bin side by side with level tops
  - the wooden barrel lying across both
  - dark outlines and three-tone shading
  - no background, no editor metadata
  Render it at 512 px (Magick.NET or a browser screenshot into the scratchpad) and show it to the user for approval before continuing. Verify that there are no Companion Cube features (corner plates, heart in a circle).
- [x] 3.2 Draw `branding/logo-mark.svg` (the crate alone, same view and colours, thicker outline and accent). Render both SVGs at 64, 32 and 16 px. Verify by eye that the mark is recognisable at 16 px, and record whether the full logo is readable at 16 px; that decides 4.1.

## 4. Logo in the app and the README

- [x] 4.1 Make `appicon.svg` a copy of the full logo with no background (drop `appiconfg.svg`, `ForegroundFile` and `Color` from `MauiIcon`). Set `splash.svg` to the full logo and its `Color` to `#1E2A33`. Replace the "Loading..." placeholder in `index.html` with the full logo (`wwwroot/logo.svg`) centred on `#1E2A33`, and set the window title to "Asset Manager". Verify after a build and launch that File Explorer and the taskbar show the new icon and the loading screen shows the logo.
- [x] 4.2 Copy the mark to `AssetManager.Shared/wwwroot/logo-mark.svg` and regenerate `favicon.png` (32×32) from it. Use the SVG as `<link rel="icon" type="image/svg+xml">` in `AssetManager/wwwroot/index.html` (replacing `data:,`) and in `AssetManager.Web/Components/App.razor` (keeping the PNG as a fallback). Verify that the web dev host's browser tab shows the icon.
- [x] 4.3 Add the mark as an `<img>` (about 28 px, alt "") before the "Asset Manager" title in `MainLayout.razor`. Verify with the CDP tool (`node tools/cdp.mjs shot`) that it is visible in both the dark and the light theme (spec scenario "Theme switch").
- [x] 4.4 Add a test in `AssetManager.Tests` that reads the repository files and asserts:
  - `splash.svg`, `AssetManager/wwwroot/logo.svg` and `wwwroot/logo-mark.svg` are byte-identical to their masters in `branding/`, and the mark's `crate` group equals the one in `logo.svg`
  - `appicon.svg` is byte-identical to `branding/logo.svg`
  - no file under `AssetManager/Resources` contains the old `#512BD4` colour or the `.NET` mark path
  Verify that `dotnet test --project AssetManager.Tests/AssetManager.Tests.csproj` passes, and fails when a copy is edited.
- [x] 4.5 Show `branding/logo.svg` at the top of the README (a centred `<img>`, about 160 px wide, above the title or beside it, with alt text). Mention `branding/` as the logo source in the Architecture section. Verify that the image renders in a Markdown preview.

## 5. Integration check

- [x] 5.1 Run the full test suite and a local portable publish, then run the workflow's package check locally on `publish/AssetManager`. Verify that it passes with `LICENSE.txt` present and fails when `LICENSE.txt` is deleted.

## Workflow follow-up

- After merging, check on GitHub that the repository sidebar shows "MIT license" and the README logo renders.
- Archive the change once it is merged.
