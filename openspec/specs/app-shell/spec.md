# app-shell Specification

## Purpose
TBD - created by archiving change build-asset-manager. Update Purpose after archive.

## Requirements

### Requirement: Stock component library UI
The UI SHALL be built with a single, popular Blazor component library, using its stock components and themes. The app SHALL NOT include custom visual styling beyond layout-level adjustments the library cannot express.

#### Scenario: Consistent look
- **WHEN** any page of the app is shown
- **THEN** its controls are the component library's standard components using the library's default theme

### Requirement: Fully offline
The app SHALL load every script, stylesheet, font and icon from local, bundled files, with no CDN or other network requests. There are only two exceptions:
- an explicit, user-initiated download of an AI model
- the update check, when the user has turned it on

#### Scenario: No network
- **WHEN** the app starts with networking disabled
- **THEN** the UI renders completely, with icons and fonts, and all non-download features work

#### Scenario: Default install makes no requests
- **WHEN** the app is used with default settings and no model download is started
- **THEN** it makes no network requests

### Requirement: Dark and light theme
The user SHALL be able to switch between dark, light and system-default themes in the Appearance section of Settings. The app bar SHALL NOT have a theme switch. The choice SHALL apply immediately and SHALL persist across restarts.

#### Scenario: Persisted theme
- **WHEN** the user selects dark theme and restarts the app
- **THEN** the app starts in dark theme

#### Scenario: Theme in Settings
- **WHEN** the user opens Settings → Appearance and selects Light
- **THEN** the app switches to the light theme at once, and the choice shows as selected

### Requirement: Navigation
The app SHALL provide the following sections:
- Dashboard (start page)
- Browse/Search
- Collections
- Sources
- Import (including any staged sessions)
- Background Jobs/Progress
- Settings

#### Scenario: Background progress visible
- **WHEN** AI enrichment is running
- **THEN** an indicator in the app bar shows progress and links to the Jobs page

### Requirement: Multi-selection and bulk actions
In the asset grid, the user SHALL be able to select multiple assets with click, Ctrl/Shift-click and select-all-in-results. A click anywhere on a card SHALL count as a click on that card: on the preview, the name, the type line or the empty space between them. Only the card's checkbox and its overlay controls (such as play) SHALL keep their own behaviour. A double click anywhere on a card SHALL open its details. The user SHALL then be able to run these bulk actions on the selection:
- tag
- untag
- delete
- re-import
- re-enrich
- regenerate previews
- copy
- extract
- store/unstore original in catalog

#### Scenario: Bulk action on selection
- **WHEN** the user shift-selects 30 assets and chooses "Extract to folder"
- **THEN** the export applies to exactly those 30 assets

#### Scenario: Click on the name
- **WHEN** the user clicks the name line under a card's preview
- **THEN** that card becomes the only selected card, as when clicking its preview

#### Scenario: Ctrl-click on the type line
- **WHEN** two cards are selected and the user Ctrl-clicks the type line of a third card
- **THEN** three cards are selected

### Requirement: Settings
The settings SHALL let the user configure:
- the catalog file location
- the AI model files and acceleration (CPU/GPU)
- the default AI depth for imports
- the cache size limit
- the maximum nesting depth
- which extensions count as scripts or ignored files
- which folders imports ignore by default

#### Scenario: Change catalog location
- **WHEN** the user points the settings to a different catalog file
- **THEN** the app opens that catalog, creating it if it does not exist

### Requirement: Preview settings
The user SHALL be able to edit every preview threshold and quality parameter, each with a documented default and a reset-to-default option. These settings SHALL be stored in the catalog and SHALL apply to new previews. Existing previews SHALL change only when they are regenerated.

#### Scenario: Default values
- **WHEN** a new catalog is created
- **THEN** the preview settings have these defaults:

| Setting | Default |
|---|---|
| Image preview maximum edge | 512 px |
| Image preview lossy quality | 80 |
| Keep-as-is maximum dimension | 512 px |
| Keep-as-is maximum file size | 64 KB |
| Pixel-art detection: maximum edge | 128 px |
| Pixel-art detection: maximum color count | 64 |
| Pixel-art detection | on |
| 3D render size | 384 px |
| 3D elevation angles | −15°, 20°, 55° |
| 3D azimuths per ring | 8 |
| 3D render quality | 80 |
| Audio clip maximum length | 10 s |
| Audio bitrate | 48 kbps |
| Audio channels | mono |
| Palette color count | 5 |

#### Scenario: Change keep-as-is threshold
- **WHEN** the user sets the keep-as-is maximum dimension to 256 px and imports a 400×400 PNG
- **THEN** the PNG is stored as a downscaled lossy preview, not as the original

#### Scenario: Existing previews untouched
- **WHEN** the user changes the image preview maximum edge
- **THEN** existing previews are unchanged until the user regenerates them, and new imports use the new value

#### Scenario: Reset to defaults
- **WHEN** the user chooses "Reset to defaults" in preview settings
- **THEN** all preview settings return to their documented default values

#### Scenario: Invalid value rejected
- **WHEN** the user enters a 3D azimuth count of 0 or an audio clip length above 60 s
- **THEN** the value is rejected with a validation message and the previous value is kept

### Requirement: Selectable text
UI chrome SHALL NOT be selectable: page and section headings, labels, buttons, navigation and menu items, tabs, chips and table headers. Text on asset cards in grids (name, type, badges) SHALL NOT be selectable either, so that clicking and dragging over cards never highlights text. Content a user may want to copy SHALL stay selectable:
- names of assets in the asset details panel, and names of sources and collections
- descriptions, file paths, links and license text
- error and job messages
- the values in About
- text in input fields

#### Scenario: Select all
- **WHEN** the user presses Ctrl+A on the Dashboard
- **THEN** no heading, label, navigation item or button text is highlighted

#### Scenario: Copy an asset name
- **WHEN** the user drags over the asset name in the asset details panel and copies it
- **THEN** the clipboard holds that name

#### Scenario: Select all on Browse
- **WHEN** the user presses Ctrl+A on the Browse page, outside a text field and with the details panel closed
- **THEN** all assets matching the filters are selected and no page text is highlighted; in a text field Ctrl+A selects that field's text, and with the details panel open it selects only the panel's text

#### Scenario: Navigation not selectable
- **WHEN** the user drags across the navigation menu
- **THEN** no menu item text is highlighted

#### Scenario: Card text not selectable
- **WHEN** the user double-clicks or drags over the name on a Browse card
- **THEN** no text is highlighted

### Requirement: Content scrolls below the app bar
The app bar SHALL stay fixed at the top. Only the page content below it SHALL scroll, and its scrollbar SHALL start below the app bar. Content SHALL NOT scroll underneath the app bar.

#### Scenario: Long page
- **WHEN** the user scrolls a page longer than the window
- **THEN** the app bar stays in place, the content moves only in the area below it, and the scrollbar's track starts at the app bar's bottom edge

### Requirement: Settings section navigation
The Settings page SHALL list its sections in a side list that stays visible while the page scrolls. Choosing an entry SHALL scroll that section into view and mark it as current. The list SHALL include every section the page shows.

#### Scenario: Jump to a section
- **WHEN** the user chooses "About" in the Settings section list
- **THEN** the About section scrolls into view and "About" is marked as current

### Requirement: Settings search
The Settings page SHALL have a search box. A query SHALL show only the sections whose title or setting names contain it, ignoring case, and the section list SHALL show only those sections. When nothing matches, the page SHALL say so. Clearing the query SHALL show all sections again.

#### Scenario: Find a setting
- **WHEN** the user types "cache" into the Settings search
- **THEN** only the sections with a cache setting are shown, in the page and in the section list

#### Scenario: No match
- **WHEN** the user types a query that matches no section
- **THEN** no sections are shown and a "No settings match" message appears

#### Scenario: Clear search
- **WHEN** the user clears the search box
- **THEN** all sections are shown again

### Requirement: Keyboard navigation
The Browse grid SHALL be usable with the keyboard:
- arrow keys move the focus between cards, and Shift+arrows extend the selection
- Ctrl+A selects all results
- Enter opens the focused asset's details, and Esc closes the details or clears the selection
- Space plays or stops audio, or turns the 3D preview of the focused asset
- `/` focuses the search box

The focused card SHALL be visibly marked and scrolled into view.

#### Scenario: Open with the keyboard
- **WHEN** the user moves to a card with the arrow keys and presses Enter
- **THEN** that asset's details open

### Requirement: Action shortcuts
In Browse:
- `F` SHALL toggle favourite, and `0`-`5` SHALL set the rating, for the focused asset or the selection
- Delete SHALL start the bulk delete with its confirmation
- `?` SHALL show a list of all shortcuts

Shortcuts SHALL NOT fire while the user is typing in a text field.

#### Scenario: Rate with the keyboard
- **WHEN** the user moves to a card with the arrow keys and presses `4`
- **THEN** that asset is rated 4 stars

#### Scenario: Typing in search
- **WHEN** the search box has focus and the user types "f5"
- **THEN** the text "f5" is entered, and no favourite or rating changes

### Requirement: Asset card context menu
Right-clicking a card in the Browse grid, or pressing the context-menu key or Shift+F10 on the focused card, SHALL open a context menu for that asset. Each item SHALL show an icon next to its label. The items SHALL be:
- "Open details"
- "Find similar", disabled with the reason shown when the asset has no embedding or the embedding model is unavailable
- "Copy name", which puts the asset's name on the clipboard as text

The cards SHALL NOT show a "find similar" button of their own.

#### Scenario: Copy a name from the grid
- **WHEN** the user right-clicks a card named "barrel_01" and chooses "Copy name"
- **THEN** the clipboard holds the text "barrel_01" and the selection is unchanged

#### Scenario: Find similar from the menu
- **WHEN** the user right-clicks an enriched low-poly rock model and chooses "Find similar"
- **THEN** Browse lists the assets most similar to it, with a "Similar to" chip

#### Scenario: Not enriched yet
- **WHEN** the user right-clicks an asset whose AI enrichment has not run
- **THEN** "Find similar" is shown disabled with a note that the asset needs AI enrichment first

#### Scenario: Keyboard
- **WHEN** a card has keyboard focus and the user presses Shift+F10
- **THEN** the context menu opens at that card and its first item is focused

### Requirement: Context menu scope
The card context menu SHALL act on the card it was opened on and SHALL NOT change the selection. It SHALL close on Esc, on a click outside it, or after an item is chosen.

#### Scenario: Menu on an unselected card
- **WHEN** two cards are selected and the user right-clicks a third card and chooses "Open details"
- **THEN** the third card's details open and the same two cards stay selected
