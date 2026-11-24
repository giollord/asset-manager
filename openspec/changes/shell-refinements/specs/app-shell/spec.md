## MODIFIED Requirements

### Requirement: Dark and light theme
The user SHALL be able to switch between dark, light and system-default themes in the Appearance section of Settings. The app bar SHALL NOT have a theme switch. The choice SHALL apply immediately and SHALL persist across restarts.

#### Scenario: Persisted theme
- **WHEN** the user selects dark theme and restarts the app
- **THEN** the app starts in dark theme

#### Scenario: Theme in Settings
- **WHEN** the user opens Settings → Appearance and selects Light
- **THEN** the app switches to the light theme at once, and the choice shows as selected

## ADDED Requirements

### Requirement: Selectable text
UI chrome SHALL NOT be selectable: page and section headings, labels, buttons, navigation and menu items, tabs, chips and table headers. Content a user may want to copy SHALL stay selectable:
- names of assets, sources and collections
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

#### Scenario: Navigation not selectable
- **WHEN** the user drags across the navigation menu
- **THEN** no menu item text is highlighted

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
