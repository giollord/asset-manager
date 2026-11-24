## MODIFIED Requirements

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

## ADDED Requirements

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
