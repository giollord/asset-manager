# Design

## Context

See proposal.md, "Why". Current state that shapes the approach:

- **App icon:** `AssetManager/AssetManager.csproj` builds the icon from `MauiIcon` (`appicon.svg`, a plain `#512BD4` square, plus the foreground `appiconfg.svg`, the ".NET" lettering). The splash comes from `MauiSplashScreen` with the same colour. MAUI rasterises these SVGs at build time into every size Windows needs, including the exe's `.ico`. The build is unpackaged (`WindowsPackageType=None`), so the appx manifest placeholders are not used at runtime. They are still template text in a public repo.
- **Favicons:** the MAUI page `AssetManager/wwwroot/index.html` has an empty favicon (`data:,`). The web dev host's `App.razor` uses `_content/AssetManager.Shared/favicon.png`, a 32×32 template PNG.
- **App bar:** `MainLayout.razor` shows only the text "Asset Manager". The `app-shell` spec allows only stock MudBlazor components and no custom visual styling. An image in the app bar is content, not styling, so it fits within that.
- **Data folder:** it is `%LOCALAPPDATA%\AssetManager`, built in `AppDataLocation`. It does not depend on `ApplicationId`, and nothing uses MAUI `Preferences` or `FileSystem.AppDataDirectory`.
- **Release check:** `.github/workflows/build.yml` already checks the package for required files. Adding `LICENSE.txt` to that list is enough to enforce the new requirement.

## Goals / Non-Goals

**Goals:**
- One master SVG per logo variant. Every other logo file is a copy of a master, and a test keeps the copies in sync.
- The artwork stays hand-editable: plain SVG polygons and paths on a known grid, with no editor metadata.
- The license text is fetched, not written.

**Non-Goals:**
- An animated splash, a light/dark variant of the logo itself, or a wordmark. The app bar keeps its text title next to the mark.
- A custom multi-resolution `.ico` built outside MAUI's pipeline (see Decisions).

## Decisions

### Projection: 2:1 dimetric on a fixed grid
The receding edges rise 1 unit for every 2 units across (atan(0.5) ≈ 26.57°). This is in the requested 20-30° range. The 2:1 ratio puts every vertex on whole numbers, so edges stay crisp after rasterising and the SVG coordinates are easy to read and edit.
- **Grid:** one unit = 4 px in a 512×512 viewBox. A box of width W (along x), depth D (along y) and height H has its top face at offsets (±2W, ±W) and (±2D, ∓D). Its vertical edges are H long.
- **Rejected:** true isometric at 30° (irrational coordinates, blurrier small sizes) and 45° oblique (looks flat, and the user ruled it out).

### Composition
```
                 .-----------------------.
                (  ====  barrel  ====     )   <- lying on its side, axis along the x edge,
                 '-----------------------'       resting across both tops
        +--------------+  +-----------+
        |  sci-fi      |  |  metal    |
        |  crate       |  |  bin      |       crate and bin stand side by side on the
        |  [glow]      |  |  |||||    |       same ground line; their tops are level so
        +--------------+  +-----------+       the barrel can lie across them
```
- **Sci-fi crate:** a near-cube with bevelled corners and panel seams. A glowing accent strip or emblem (a cyan/teal light) sits on the front face and is the only saturated colour in the logo. It has no corner plates and no heart in a circle, so it does not resemble the Companion Cube.
- **Metal trash bin:** a cylinder drawn as an ellipse plus a body, so it reads as round next to the boxy crate. It has vertical ribs, a slightly domed lid with a handle, and a brushed steel grey.
- **Barrel:** an old wooden barrel lying on its side with its axis parallel to the crate's x edge. It has bulging staves, dark iron hoops and a visible end with the stave ends. Its lowest point touches the tops of both objects below.
- **Heights:** the crate's height equals the height of the bin's rim, so the barrel rests level on both. The lid handle stays below the barrel or is hidden behind it.
- **Outline:** every object has a dark outline about 8 px wide at 512 px, and each has a lit top, a mid face and a dark face (light from the upper left). This keeps the shapes separate on any background and at small sizes.

### Palette

| Role | Colour |
|---|---|
| icon/splash background | deep slate `#1E2A33` |
| outline | `#0E1419` |
| wood | `#A0693A` / `#7A4E2A` / `#5A3820` |
| iron hoops | `#3A3F44` |
| steel | `#B8C2CA` / `#8E9AA4` / `#66727C` |
| crate body | `#4A5560` / `#38424C` / `#2A323A` |
| accent glow | `#3FE0D0` |

The dark slate background replaces the MAUI purple in `MauiSplashScreen Color` and the loading screen. The app icon has no background. The single bright accent makes the crate the focal point.

### Variants and files
`branding/logo.svg` (full group) and `branding/logo-mark.svg` (crate alone) are the masters. Neither has a background. Each object in `logo.svg` is a group (`<g id="crate">`, `bin`, `barrel`). The mark holds a byte-identical copy of the `crate` group, with a square viewBox cropped to it. A cross-file `<use href="logo.svg#crate">` is not used, because SVGs loaded through `<img>` (favicons, the app bar, GitHub's README) never load other files, and MAUI's rasteriser does not resolve them either. The copies are:
- `AssetManager/Resources/AppIcon/appicon.svg`: the full logo, with no foreground file and no `Color`, so the icon is transparent around the drawing (the user wants no background square in the taskbar). The logo's own margin in its 512 canvas is the padding.
- `AssetManager/Resources/Splash/splash.svg` and `AssetManager/wwwroot/logo.svg` (loading screen): the full logo.
- `AssetManager.Shared/wwwroot/logo-mark.svg`: used by the app bar `<img>` at about 28 px, and as the favicon of both host pages (`<link rel="icon" type="image/svg+xml">`). `favicon.png` is regenerated from the mark at 32×32 as a fallback.
- The README shows `branding/logo.svg` at the top with an `<img>` tag. GitHub renders SVG files from the repository.

A test in `AssetManager.Tests` checks that each copy is byte-identical to its master, so an edit to only one file is caught. It also checks that the mark's `crate` group equals the one in `logo.svg`.

### Icon at small sizes
MAUI generates every icon size from one SVG, so the exe icon uses the full group at all sizes. The design keeps the silhouettes bold enough to survive at 16 px. If a rendering at 16 px is unreadable, the icon foreground switches to the mark, and the full group stays on the splash and in the README.
- **Outcome (review at 16 px):** the full group keeps a recognisable silhouette (barrel over bin, dark crate, cyan dot) but loses its details. The mark stays crisp. The user chose the full group for the icon at every size.
- **Rejected:** a hand-built `.ico` with different art per size, set through `ApplicationIcon`. It works around MAUI's pipeline and has to be maintained by hand.

### Splash and title bar on Windows
MAUI shows no splash screen for an unpackaged Windows app. While the WebView loads (about a second), the window shows the placeholder inside `#app` in `wwwroot/index.html`. That placeholder becomes the full logo (`AssetManager/wwwroot/logo.svg`, a copy of the master) centred on `#1E2A33`, which also removes the white flash. `splash.svg` is kept for MAUI. The window title bar is drawn by MAUI without an app icon. It stays stock, with the window title set to "Asset Manager", and the app bar right below it shows the mark.
- **Rejected:** a MAUI `TitleBar` with an icon. It changes the title bar's look and height and needs another image copy, for little gain next to the app bar logo.

### Checking the artwork
Each SVG is rasterised with Magick.NET, which is already a dependency, or with a browser screenshot, at 512, 64, 32 and 16 px. The renders are reviewed by eye before they are wired in. The renders are not committed.

### App identity values
- `ApplicationTitle`: Asset Manager
- `ApplicationId`: `io.github.giollord.assetmanager` (reverse DNS of the GitHub Pages domain the account owns)
- `Product`: Asset Manager
- `Company` and `Authors`: Daniil Barkov
- `Copyright`: Copyright (c) 2026 Daniil Barkov
- `Description`: Offline catalog for game assets

These go in `AssetManager.csproj`, so the exe's version resource gets them. The appx manifest's `User Name` and `$placeholder$` values become the same name and title, even though the unpackaged build ignores them.

### License source
`gh api /licenses/mit --jq .body` returns the text GitHub itself uses. Only `[year]` → `2026` and `[fullname]` → `Daniil Barkov` are replaced, and the diff against the API output must show only those two lines. The file is published next to the exe as `LICENSE.txt` with a `None` item linked to the root `LICENSE` (`CopyToPublishDirectory`), and `LICENSE.txt` is added to the workflow's `$required` list.
- **Rejected:** writing the text by hand, or copying it from a random repository.

## Risks / Trade-offs

- **The app id may affect the WebView2 profile folder.** If the WebView2 user-data folder is derived from the app id, changing the id starts a fresh profile, which loses only the WebView's own cache. The theme and settings live in `settings.json`. → Mitigation: after the change, start the app over an existing data folder and check that the catalog, settings and theme are kept (the "Existing data kept" scenario). If the profile matters, keep the old id.
- **Hand-drawn SVG quality.** Without an illustrator, the art may look amateurish. → Mitigation: work on the strict grid, use few shapes, review the renders at each size, and iterate with the user on the 512 px render before wiring it in.
- **The full group in a 16 px icon.** → Mitigation: the fallback to the mark described under "Icon at small sizes".
- **Resemblance to existing IP.** → Mitigation: the crate's design rules above. The accent is a light strip or emblem, not a heart in a circle.

## Open Questions

- The exact shape of the crate's accent (light strip, chevron, hex emblem) can be settled while drawing, using the review renders.
