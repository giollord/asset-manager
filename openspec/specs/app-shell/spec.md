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
The app SHALL load every script, stylesheet, font and icon from local, bundled files, with no CDN or other network requests. The only exception is an explicit, user-initiated download of an AI model.

#### Scenario: No network
- **WHEN** the app starts with networking disabled
- **THEN** the UI renders completely, with icons and fonts, and all non-download features work

### Requirement: Dark and light theme
The user SHALL be able to switch between dark, light and system-default themes. The choice SHALL persist across restarts.

#### Scenario: Persisted theme
- **WHEN** the user selects dark theme and restarts the app
- **THEN** the app starts in dark theme

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
In the asset grid, the user SHALL be able to select multiple assets with click, Ctrl/Shift-click and select-all-in-results. The user SHALL then be able to run these bulk actions on the selection:
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
