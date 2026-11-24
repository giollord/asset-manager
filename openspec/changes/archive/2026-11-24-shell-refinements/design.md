# Design

## Context

See proposal.md, "Why". Current state that shapes the approach:

- **Window:** `AssetManager` is an unpackaged MAUI app on Windows App SDK 1.8 with WebView2 SDK 1.0.3179. MAUI draws its own title bar row above the `BlazorWebView`, with the window title "Asset Manager". All UI lives inside the WebView: `MainLayout.razor` has a dense `MudAppBar` (48 px), a clipped mini `MudDrawer` and `MudMainContent`. The same UI runs in a browser through `AssetManager.Web`.
- **Platform services:** platform features reach the shared UI through interfaces in `AssetManager.Core.Platform`, such as `IShellService` and `IFolderPicker`. The Windows host registers real implementations and the web host registers "not supported" ones.
- **Scrolling:** the document itself scrolls. `MudAppBar` is `position: fixed`, so content passes under it and the scrollbar runs the full window height.
- **Selection:** nothing sets `user-select`, so every text node is selectable.
- **List bug:** `Sources.razor` and `Collections.razor` render `MudList T="long"` with `MudListItem`s that have no `Value`. MudList's single selection compares values, every item's value is `default(long)`, and clicking one marks all as selected. The pages also add `mud-selected-item` by hand.
- **Jobs indicator:** `JobsIndicator.razor` renders an `Icons.Material.Filled.Sync` button when idle. `NavMenu` uses the same icon for Jobs.
- **Theme:** `ThemeToggle.razor` in the app bar cycles System → Light → Dark through `IAppSettingsStore`. `MainLayout` applies it.
- **Settings:** `Settings.razor` stacks six section components (Catalog, AI model, Import, Previews, Maintenance, About). The `asset-workflow-upgrades` change will add more (backup, library folders, consensus, update check).
- **Version:** `AppVersion` reads the entry assembly's informational version. Release builds get it from `-p:Version=<tag>` in CI. Local builds report the SDK default 1.0.0. The repository has the tag `v0.1.0`.
- **Styling rule:** the `app-shell` spec allows only layout-level CSS beyond MudBlazor's stock look. Everything below stays at that level: selection, scroll container, drag regions, caption-button inset.

## Goals / Non-Goals

**Goals:**
- One header row on Windows, using only Microsoft APIs (Windows App SDK, WebView2) and no Win32 interop.
- The web dev host keeps working unchanged apart from the shared UI changes.
- Settings search keeps covering sections added later without separate search plumbing.

**Non-Goals:**
- A custom-drawn set of caption buttons, or any change to the window's resize borders.
- Scroll-spy, which would highlight the section in view while scrolling. The current entry changes only when chosen in the list.
- Searching inside setting values (paths, model names); search covers titles and setting names.
- Changing what a release contains or when it is published.

## Decisions

### D1. The app bar becomes the title bar through WebView2 non-client regions
On window creation (MAUI `ConfigureLifecycleEvents` → `OnWindowCreated`), the host:
- sets `AppWindow.TitleBar.ExtendsContentIntoTitleBar = true`
- sets `PreferredHeightOption = Tall`, so the system caption buttons are 48 px, the dense app bar's height
- makes the caption button backgrounds transparent
- removes MAUI's own title bar row

Before the first navigation (`BlazorWebViewInitializing`/`Initialized`), the host sets `CoreWebView2.Settings.IsNonClientRegionSupportEnabled = true`. `app.css` then marks the app bar `app-region: drag` and its buttons, menus and the drawer toggle `app-region: no-drag`. WebView2 treats drag regions as the caption area: drag moves the window, double-click maximizes, right-click opens the system menu.
- **Alternative (fallback):** Windows App SDK `InputNonClientPointerSource.SetRegionRects(NonClientRegionKind.Caption, …)`, with the app bar's rectangle minus its buttons reported from JS on layout changes. It is also official and also free of interop, but it needs rectangles kept in sync. It is used only if the spike (task 1) shows that WebView2's regions don't work in MAUI's WinUI WebView2.
- **Spike outcome (task 1):** WebView2's regions do not work in MAUI's WinUI WebView2. With the content extended into the title bar, the system's default caption strip (the title bar height across the window, minus the caption buttons) wins over the page: the app bar dragged even with `app-region: no-drag` on it, and the drawer toggle did not click. The fallback is used in a simpler form: the system's default caption strip stays as the drag area (it is exactly the 48 px app bar at the `Tall` height), and `js/windowChrome.js` reports the app bar's interactive elements (those that compute to `app-region: no-drag` in `app.css`) on layout changes. The host sets them as `NonClientRegionKind.Passthrough` rectangles through `InputNonClientPointerSource`. `IsNonClientRegionSupportEnabled` is not set. `IWindowChrome` gains a fourth member, `SetInteractiveRegions`, for this.
- **MAUI's title row:** it is the template part `AppTitleBarContainer` of MAUI's `WindowRootView`, laid over the page, and the page's NavigationView `ContentGrid` gets a top margin of its height. The host collapses that part and pins `ContentGrid`'s margin to 0 once the window is first laid out, both found by name in the WinUI visual tree. This relies on MAUI internals (MAUI 10.0.110) and must be rechecked when MAUI is upgraded.
- **Drag area height:** MAUI sets the window's drag rectangles from its title row (full width × 32 DIPs, stale after the row is collapsed) on every resize, so only the upper two thirds of the 48 px app bar moved the window. The host sets the drag rectangle to the full width × the tall title bar's height, queued after MAUI's handlers on every resize, maximize and display scale change. Verified at 100% and 150%.
- **Display scale:** the caption-button width and the passthrough rectangles use the WinUI `XamlRoot.RasterizationScale`, read once the window's content is laid out (it is not available at first activation).
- **Rejected:** MAUI's `TitleBar` control (a native row above the WebView, so two headers). Third-party window-chrome packages: none for MAUI Blazor is both widely used and from a known maintainer. P/Invoke on `WM_NCHITTEST`: the user ruled out "magic with system DLLs".

### D2. A window-chrome platform service feeds the shared UI
`AssetManager.Core.Platform.IWindowChrome` has four members:
- `bool IsCustomTitleBar`
- `double CaptionButtonsWidth` in CSS pixels, with a `Changed` event for DPI or size changes
- `void SetCaptionColors(string foreground, string hoverBackground)`
- `void SetInteractiveRegions(IReadOnlyList<ChromeRect>)`: the app bar's clickable elements in CSS pixels (D1, spike outcome)

The Windows implementation reads `AppWindow.TitleBar.RightInset` divided by the display scale. The web host registers a no-op version (`IsCustomTitleBar = false`). `MainLayout` reserves the width as right padding on the app bar, and updates the caption colours from the MudBlazor palette (`AppbarText`, `ActionDefault`) whenever the theme changes.

### D3. Content scrolls in `MudMainContent`
In `app.css`:
- `html, body` get `height: 100%; overflow: hidden`
- `.mud-main-content` gets `position: fixed` below the app bar: `top: var(--mud-appbar-height)`, `bottom: 0`, `left` and `right` following the drawer through MudBlazor's own layout variables, `overflow-y: auto`, and the top padding MudBlazor adds removed

That element becomes the scroll container, and its scrollbar starts at the app bar's bottom edge.
- **Settled in task 3.1:** `position: absolute` inside `.mud-layout` (given `height: 100vh`), not `fixed`. A fixed element is a stacking context, which trapped the pages' own fixed layers (the asset details drawer, z-index 1302) below the app bar (1300).
- **Browse** (changed at the user's request): its asset grid no longer has its own scroll area. The list scrolls with the content area like every other page. The filter panel and the result header are sticky, and the list runs to the window's bottom edge.
- **Temporary drawers** (the asset details drawer) open below the app bar instead of over it. Over it, their header buttons sat under the system caption buttons and inside the strip that moves the window. `MainLayout` resets that container's scroll position on `LocationChanged`, since Blazor's automatic scroll-to-top only resets the document. The exact CSS (fixed versus a flex column) is settled in the task against MudBlazor's drawer variants. The requirement is the observable behaviour in the spec.

### D4. Opt-in selection
`app.css` sets `body { user-select: none }`. Selection is re-enabled for `.selectable`, `input`, `textarea` and `[contenteditable]`. The `selectable` class goes on these elements:
- the asset name, description, details table values, paths, links and license text in the details drawer
- source names, paths, descriptions and errors
- collection names on their pages
- `JobProgressView`'s current item and error, and job history messages
- About values
- in Settings, the open catalog's path and size, and the recommended models' texts (added during implementation at the user's request)
- asset names on cards, alert and snackbar messages (CSS, no class needed)
- asset names in import review

An opt-in class keeps the default safe for new UI, so new chrome is never selectable by accident.
- **Ctrl+A on Browse** (added during implementation at the user's request): a page-level key handler in `browse.js` selects all matching assets and suppresses the browser's select-all, except in text fields and dialogs. With the details panel open, it selects only the panel's text. It replaces the grid's own handler, which only worked while the grid had focus.
- **Rejected:** opt-out per element (`.no-select` on headings and nav). Every new heading would need it, and Ctrl+A would still catch whatever was missed.

### D5. Lists select by value
`MudListItem`s get `Value="s.Id"` / `Value="c.Id"`, and the `MudList` gets `SelectedValue` from the route id. The hand-added `mud-selected-item` classes are removed, so MudBlazor's own selection styling applies to exactly one item. Child-source lists in `Sources.razor` get the same treatment.

### D6. Jobs indicator only while active
`JobsIndicator` renders nothing when no job is active, which matches the existing `job-progress` text. The Jobs navigation icon changes from `Sync` to `ManageHistory`, so it no longer reads as "refresh".

### D7. Theme moves to an Appearance section
A new `AppearanceSettings` section offers a `MudToggleGroup` (or radio group) with System / Light / Dark, bound to `Settings.Current.Theme`. `ThemeToggle.razor` and its app-bar slot are removed. `MainLayout` already reacts to `Settings.Changed`, so applying the theme is unchanged.

### D8. Settings page: section registry, side list and search
`Settings.razor` builds a list of section descriptors (`id`, `title`, `keywords`, component). Each section component exposes its title and setting names as a static `SearchTerms` array next to its markup, so a section added later (including by `asset-workflow-upgrades`) brings its own terms.

The page lays out two columns:
- **Left:** a `MudTextField` search with clearing, and a `MudList` of the visible sections, sticky in the scroll container. Choosing an entry calls `scrollIntoView` on the section's anchor and marks it current.
- **Right:** the visible sections, each in a `MudPaper` with an anchor id. When none match, a "No settings match" alert shows instead.

Matching is a case-insensitive substring test on the title and terms.
- **Drift guard:** a bUnit test renders each section and checks that every visible field label appears in that section's `SearchTerms`.
- **Rejected:** searching the rendered DOM text through JS. It never drifts, but it is not testable in bUnit and also matches help text and values.

### D9. Version from git tags with MinVer
A root `Directory.Build.props` adds `MinVer` as a build-only package reference (`PrivateAssets="all"`) for every project, with `MinVerTagPrefix=v`. A tagged commit then builds as `0.1.0`, and a later commit as `0.1.1-alpha.0.<height>`. `AppVersion` keeps stripping the `+commit` suffix.
- **CI:**
  - both checkouts use `fetch-depth: 0`, so tags and history are present
  - the publish step drops `-p:Version` and `-p:ApplicationDisplayVersion`
  - the existing "Version" step still validates the tag format and names the zip
  - a new check compares `AssetManager.exe`'s product version with the tag and fails on a mismatch
- **MAUI's `ApplicationDisplayVersion`:** only used for packaged builds. If MAUI derives the Windows file version from it rather than from `Version`, a small target after MinVer copies `MinVerVersion` into it. The task checks this.
- **Rejected:** Nerdbank.GitVersioning (version.json and commit height, not tags) and a hand-written `git describe` target (more to maintain). The user chose MinVer.

## Risks / Trade-offs

- **[WinUI's WebView2 may not honour `app-region` (composition hosting)]** → Task 1 is a spike that checks drag, double-click, right-click and snap layouts before anything else is built on it. If it fails, D1's fallback (`InputNonClientPointerSource`) is used with the same UI, and design.md is updated.
- **[Removing MAUI's title bar row]** → MAUI has no public switch for its default title bar on Windows. The spike decides how to collapse it: a zero-height `Window.TitleBar`, or setting the title bar element through the WinUI window. It is verified at 100% and 150% scaling.
- **[Windows 10]** → `AppWindowTitleBar` customisation works on Windows 10 with Windows App SDK 1.2+. Checked on the developer machine only (Windows 11). If `AppWindowTitleBar.IsCustomizationSupported()` returns false, the host keeps the standard title bar and `IsCustomTitleBar` is false.
- **[Drag region swallowing clicks]** → Every interactive app bar element gets `app-region: no-drag`, and a test asserts that the rule covers buttons and menus. It is checked by clicking each control in the running app.
- **[Scroll container change breaks pages that rely on window scroll]** → `Virtualize` and MudBlazor popovers use the nearest scroll ancestor. Browse, Collections, Import review and the details drawer are checked in the running app, including popover placement after scrolling.
- **[Ctrl+A now selects nothing in most pages]** → This is the intended behaviour. Inputs keep normal Ctrl+A inside themselves.
- **[MinVer needs full history]** → A shallow checkout gives `0.0.0-alpha.0`. Both CI jobs set `fetch-depth: 0`, and the version check fails a tagged build that reports anything else.
- **[Section search terms drift]** → The drift-guard test in D8.

## Migration Plan

No data migration. The saved theme setting is unchanged, only where it is edited moves. Rollback is reverting the change. The window falls back to the standard title bar if customisation is unsupported.
