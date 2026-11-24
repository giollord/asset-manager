# Tasks

## 1. Window title bar spike

- [x] 1.1 In the Windows host only, enable `IsNonClientRegionSupportEnabled` before the first navigation, extend content into the title bar (`AppWindow.TitleBar`, `PreferredHeightOption = Tall`, transparent caption-button backgrounds), and remove MAUI's own title bar row. Add `app-region: drag` to the app bar and `no-drag` to its buttons in `app.css`. Verify in the running app at 100% and 150% scaling:
  - dragging the app bar moves the window
  - double-click maximizes and restores
  - right-click opens the system menu
  - hovering maximize shows snap layouts
  - the drawer toggle still clicks
  If WebView2's regions do not work in MAUI's WebView2, switch to the `InputNonClientPointerSource` fallback (design D1) and record the outcome in design.md before continuing.

## 2. Window chrome integration

- [x] 2.1 Add `IWindowChrome` to `AssetManager.Core.Platform` (design D2), with the Windows implementation (caption-button width in CSS px from `RightInset`, a `Changed` event on DPI or size changes, caption colours) and a no-op web implementation, both registered in DI. If `AppWindowTitleBar.IsCustomizationSupported()` returns false, the Windows host keeps the standard title bar. Verify with a unit test of the web implementation and a bUnit test that `MainLayout` reserves the reported width on the app bar.
- [x] 2.2 In `MainLayout`, reserve the caption-button width on the app bar and set the caption colours from the current theme's palette on every theme change. Verify in the running app (CDP `shot` plus a screen capture of the window's top right) that:
  - no app bar content sits under the caption buttons in either theme
  - the buttons are visible in both themes
  - close, minimize and maximize work
  - the web dev host shows the app bar up to the right edge (spec "Browser host unchanged")

## 3. Layout: scrolling and text selection

- [x] 3.1 Make `MudMainContent` the scroll container below the fixed app bar (design D3), and reset its scroll position on navigation. Verify in the running app with CDP that:
  - on a long page (Browse with many results, Settings), scrolling leaves the app bar in place
  - the scrollbar track starts at the app bar's bottom edge
  - navigating to another page starts at the top
  - Browse paging, the details drawer and popovers (menus, selects) still position correctly after scrolling
- [x] 3.2 Make text non-selectable by default and add the `selectable` class to the content listed in design D4: details drawer, Sources, Collections, `JobProgressView` and job history, About, import review. Verify with bUnit tests that the asset name, description, source path, job error and About version elements carry `selectable`. Verify with CDP on the Dashboard, Browse and Settings pages that after Ctrl+A, `getSelection().toString()` contains no heading or navigation text, and that the asset name in the details drawer can be selected and copied.

## 4. Lists and jobs indicator

- [x] 4.1 Give the `MudListItem`s in `Sources.razor` (sources and child sources) and `Collections.razor` their ids as `Value`, drive `SelectedValue` from the route id, and remove the hand-added `mud-selected-item` classes. Verify with bUnit tests that after selecting the second of three items, exactly one item has MudBlazor's selected class and it is the second. Extend `CollectionsPageTests` and add a Sources equivalent.
- [x] 4.2 Make `JobsIndicator` render nothing when no job is active, and change the Jobs navigation icon to `ManageHistory`. Verify with `JobUiTests`: no indicator markup with zero active jobs, the indicator appears for an active job and disappears when it finishes (spec scenarios "Idle app bar" and "Indicator appears and disappears").

## 5. Settings page

- [x] 5.1 Add an `AppearanceSettings` section (System / Light / Dark) bound to the theme setting, and remove `ThemeToggle` from the app bar and the codebase. Replace `ThemeToggleTests` with tests that choosing Light in Appearance persists `ThemeMode.Light` and that the app bar has no theme button. Verify in the running app that the theme switches immediately.
- [x] 5.2 Give each settings section component a static `SearchTerms` (its title and setting names), and build `Settings.razor` from a section registry with an anchor per section (design D8). Verify with a bUnit drift-guard test that every field label rendered by each section is in its `SearchTerms`.
- [x] 5.3 Add the sticky side list of visible sections (choosing one scrolls it into view and marks it current) and the search box (case-insensitive match on title and terms, "No settings match" when empty, clearing restores all). Verify with bUnit tests for the spec scenarios "Find a setting" ("cache"), "No match", "Clear search" and the section list contents, and in the running app that choosing "About" scrolls to it inside the new scroll container.

## 6. Version from git tags

- [x] 6.1 Add a root `Directory.Build.props` with the latest stable `MinVer` (`PrivateAssets="all"`, `MinVerTagPrefix=v`). If needed, make the Windows exe's version follow it (design D9). Remove the hard-coded versions from `AssetManager.csproj` that conflict with it. Verify:
  - a local build reports a `0.1.1-alpha.0.<n>` version in Settings → About (CDP `text #about-version`) and in the exe's file properties
  - a build of a checkout at the tag `v0.1.0` (a temporary `git worktree`) reports `0.1.0`
  - `AppVersionTests` (or a new test) still strips the `+commit` suffix
- [x] 6.2 Update `.github/workflows/build.yml`: `fetch-depth: 0` on both checkouts, no `-p:Version`/`-p:ApplicationDisplayVersion` in the publish, and a step after "Check package contents" that fails when `AssetManager.exe`'s product version differs from the tag version. Update the README's release and local-publish instructions and the csproj comment to match. Verify by running the new check step's script locally on a portable publish of the `v0.1.0` worktree (passes with tag `v0.1.0`, fails when given `v0.1.1`).

## 7. Integration check

- [x] 7.1 Run the full test suite, build the Windows app and the web dev host, and walk through every spec scenario of this change in the running app (window chrome, selection, scrolling, lists, jobs indicator, Appearance, Settings navigation and search, version). Verify that all tests pass and each scenario behaves as specified. Record any deviation in design.md before marking this task done.

## Workflow follow-up

- Commit on the current branch (no new branch), then archive the change and sync its specs.
