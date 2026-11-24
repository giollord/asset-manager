# window-chrome Specification

## Purpose
Defines the desktop window's frame: the app bar serves as the window's title bar, with the system's own caption buttons, so the app has one header instead of a Windows title bar above its own.

## Requirements

### Requirement: App bar as the title bar
The desktop app SHALL NOT show the Windows title bar. The app bar SHALL be the window's title bar. Dragging an empty part of it SHALL move the window, double-clicking SHALL maximize or restore the window, and right-clicking SHALL open the window's system menu. The app bar's own buttons and the drawer toggle SHALL still respond to clicks and SHALL NOT start a drag.

#### Scenario: Move the window
- **WHEN** the user drags the app bar by the area next to the title
- **THEN** the window moves with the pointer

#### Scenario: Maximize by double-click
- **WHEN** the user double-clicks an empty part of the app bar of a restored window
- **THEN** the window is maximized, and a second double-click restores it

#### Scenario: Buttons still work
- **WHEN** the user clicks the navigation toggle or the jobs indicator in the app bar
- **THEN** that control reacts, and the window does not move

### Requirement: System caption buttons
The window SHALL keep the system's minimize, maximize/restore and close buttons at the right end of the app bar, with the system's behaviour, including the snap layouts shown when hovering the maximize button. App bar content SHALL NOT be drawn under these buttons. Their colours SHALL follow the app's current theme.

#### Scenario: Close from the app bar
- **WHEN** the user clicks the close button at the right end of the app bar
- **THEN** the app closes as it does from the Windows title bar

#### Scenario: Snap layouts
- **WHEN** the user hovers over the maximize button
- **THEN** Windows shows its snap layouts

#### Scenario: Theme change
- **WHEN** the user switches from the dark to the light theme
- **THEN** the caption buttons stay clearly visible against the app bar

### Requirement: Browser host unchanged
In the web dev host, which runs in a browser tab, the app bar SHALL work as an ordinary app bar: it SHALL NOT reserve space for caption buttons, and it SHALL NOT try to move a window.

#### Scenario: Web dev host
- **WHEN** the UI runs in the web dev host
- **THEN** the app bar shows its content up to the right edge, and dragging it does nothing
