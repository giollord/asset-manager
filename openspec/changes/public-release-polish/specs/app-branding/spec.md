## ADDED Requirements

### Requirement: Original logo
The app SHALL have its own original logo, kept in the repository as vector (SVG) sources. The logo SHALL show a futuristic sci-fi crate and a metal trash bin with a lid standing side by side, with an old wooden barrel lying on its side on top of both. All three SHALL be drawn in an isometric 2:1 dimetric view, with receding edges at about 26.6° from horizontal.

#### Scenario: Full logo
- **WHEN** the full logo SVG is opened at 512×512
- **THEN** it shows the sci-fi crate and the metal bin side by side, with the barrel lying across their tops, all in the same dimetric view

### Requirement: No borrowed marks
The logo SHALL NOT reproduce other products' characters or marks. In particular it SHALL NOT contain the .NET or MAUI marks, and the crate SHALL NOT have the features of Portal's Weighted Companion Cube: corner plates and a pink heart in a circle.

#### Scenario: Crate review
- **WHEN** the sci-fi crate is compared with the Weighted Companion Cube
- **THEN** it has neither corner plates nor a heart in a circle

### Requirement: Compact logo mark
Besides the full group, there SHALL be a compact mark: the sci-fi crate alone, in the same view and colours. The mark SHALL stay recognisable at 16×16 pixels.

#### Scenario: Small sizes
- **WHEN** the compact mark is rendered at 16×16 and 32×32 pixels
- **THEN** the crate's silhouette and its accent colour are still distinguishable

### Requirement: Logo in the app
The app SHALL show its logo, and none of the template's images, in these places:
- the Windows icon of `AssetManager.exe`, including the taskbar
- the loading screen shown while the app starts (MAUI shows no splash screen for the unpackaged Windows build, so the page's own placeholder takes its place)
- the favicon of the app's page in the desktop app and in the web dev host
- the app bar, next to the title

The window title bar keeps MAUI's stock look with the text "Asset Manager" and no icon.

#### Scenario: Built app
- **WHEN** the portable package is built and `AssetManager.exe` is started
- **THEN** File Explorer and the taskbar show the logo icon, the window title reads "Asset Manager", and the loading screen and app bar show the logo, not the .NET mark

### Requirement: Logo backgrounds
The app icon SHALL have no background: it is transparent around the drawing. The splash and loading screen backgrounds SHALL use the logo's background colour instead of the MAUI template purple.

#### Scenario: Taskbar icon
- **WHEN** the app runs and its icon shows in the taskbar
- **THEN** the logo is drawn on the taskbar itself, with no coloured square behind it

### Requirement: App bar logo in both themes
The app bar logo SHALL remain clearly visible in both the dark and the light theme.

#### Scenario: Theme switch
- **WHEN** the user switches between the dark and the light theme
- **THEN** the app bar logo stays clearly visible against the app bar

### Requirement: Logo in the README
The README SHALL show the full logo at its top.

#### Scenario: Repository page
- **WHEN** the repository page is opened on GitHub
- **THEN** the README starts with the logo image

### Requirement: App identity
The app SHALL identify itself as "Asset Manager" with its own app id, not the template's placeholders. The Windows file properties of `AssetManager.exe` SHALL show the product name, a description, the author as company, the MIT copyright line and the app version. The repository SHALL NOT contain unused template images or template placeholder text.

#### Scenario: File properties
- **WHEN** the user opens Properties → Details for `AssetManager.exe` from a `v1.2.0` release
- **THEN** it shows the product name "Asset Manager", the copyright line and the version 1.2.0

#### Scenario: Existing data kept
- **WHEN** a user who used an earlier version starts the version with the new app id
- **THEN** the app opens the same data folder, catalog and settings as before
