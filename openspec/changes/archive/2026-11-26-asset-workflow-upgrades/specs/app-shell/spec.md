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

#### Scenario: Card text not selectable
- **WHEN** the user double-clicks or drags over the name on a Browse card
- **THEN** no text is highlighted

#### Scenario: Select all on Browse
- **WHEN** the user presses Ctrl+A on the Browse page, outside a text field and with the details panel closed
- **THEN** all assets matching the filters are selected and no page text is highlighted; in a text field Ctrl+A selects that field's text, and with the details panel open it selects only the panel's text

#### Scenario: Navigation not selectable
- **WHEN** the user drags across the navigation menu
- **THEN** no menu item text is highlighted

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
