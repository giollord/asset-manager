# Proposal

## Why

The app still feels like a web page in a window rather than a desktop app:
- **Selection:** Ctrl+A selects every heading, label and menu item.
- **Window frame:** Windows draws its own title bar above the app's header.
- **Scrolling:** the scrollbar runs under the app bar.
- **Settings:** the page is one long list with no way to jump or search, and it shows the project's default version instead of the version the build came from.

On top of that, two bugs:
- Selecting an item in the Sources or Collections list highlights every item.
- A refresh-like jobs button sits in the app bar even when no job runs, which the `job-progress` spec already rules out.

## What Changes

- **Text selection:** UI chrome (headings, labels, buttons, navigation, menus, chips) is not selectable. Content worth copying stays selectable:
  - asset, source and collection names
  - descriptions, paths, links and license text
  - error and job messages
  - the About values
  - input fields
- **Window title bar:** the Windows title bar is removed. The app bar becomes the window's title bar: dragging it moves the window, double-clicking it maximizes or restores, and right-clicking it opens the system menu. Windows still draws minimize, maximize and close at its right end, including snap layouts. This uses Microsoft's own APIs (Windows App SDK extended title bar and WebView2 non-client regions), with no third-party package and no Win32 interop.
- **Scrolling:** the app bar stays fixed and the page content scrolls in the area below it, with its scrollbar starting under the bar.
- **List selection bug:** in Sources and Collections, only the clicked item shows as selected.
- **Jobs indicator:** the app bar shows the jobs indicator only while a job runs. The idle "sync" button is removed, and the Jobs navigation entry gets an icon that does not look like refresh.
- **Theme:** the theme switch moves from the app bar to a new Appearance section in Settings (System / Light / Dark).
- **Settings navigation and search:** the Settings page gets a section list for jumping between sections, and a search box that filters the sections to those matching the query.
- **Version from git:** the build version comes from the latest git tag (MinVer). A tagged build reports exactly the tag's version, and a build after a tag reports a pre-release version of the next patch. CI stops passing `-p:Version` and fetches tags instead.

## Capabilities

### New Capabilities
- `window-chrome`: the desktop window's frame: the app bar acting as the title bar, the system caption buttons, and what happens in the web dev host where there is no window frame.

### Modified Capabilities
- `app-shell`:
  - theme selection moves to Settings
  - text selection rules
  - scrolling below a fixed app bar
  - Settings section navigation and search
- `job-progress`: the Global progress indicator is hidden when no job is active (made explicit with a scenario).
- `app-distribution`: Versioned releases. The app version comes from the git tag at build time, including untagged builds.

## Impact

- **UI:**
  - `AssetManager.Shared/Layout/MainLayout.razor` and `NavMenu.razor`, `wwwroot/app.css`
  - `Pages/Settings.razor` and the settings section components
  - `Components/ThemeToggle.razor` (replaced by an Appearance section), `Components/JobsIndicator.razor`
  - `Pages/Sources.razor`, `Pages/Collections.razor`
  - the components that show copyable content: asset details, sources, jobs, About, import review
- **Windows host:** `AssetManager/MauiProgram.cs` (window lifecycle events), `MainPage.xaml.cs` (WebView2 settings), and a small platform service so the shared UI can learn the caption-button width and set the caption-button colours for the theme
- **Build:**
  - a new `Directory.Build.props` with the MinVer package reference
  - `AssetManager.csproj` (version properties)
  - `.github/workflows/build.yml` (full-history checkout, no `-p:Version`, check that the built version matches the tag)
  - the README's release section
- **Tests:**
  - the UI tests for the theme toggle, the jobs indicator, the About version and the Collections list are updated
  - new tests for Settings search and section navigation, and for list selection
- **Dependencies:** MinVer (build-time only, MIT license). No new runtime dependency.
- **Other change in flight:** `asset-workflow-upgrades` adds Settings sections and an update check that compares the app version with releases. The Settings search and navigation must cover sections added later, and MinVer's pre-release versions are valid SemVer, so that comparison keeps working.
