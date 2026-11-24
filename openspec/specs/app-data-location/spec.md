# app-data-location Specification

## Purpose
Defines where the app keeps its catalog, settings, AI models, cache, logs and browser profile, so that a portable copy keeps everything beside itself while development builds keep using the user profile.

## Requirements

### Requirement: Portable data folder
When the app runs from the portable package, it SHALL keep all data it creates in a `data` folder next to `AssetManager.exe` by default: the default catalog, the settings file, downloaded AI models, the extraction cache, temporary files, logs and the embedded browser's profile. It SHALL NOT write these into the user profile.

#### Scenario: First start of a portable copy
- **WHEN** the portable package is extracted to `E:\Tools\AssetManager` and started for the first time
- **THEN** `E:\Tools\AssetManager\data` holds the new catalog, `settings.json` and the logs, and nothing is created under `%LOCALAPPDATA%\AssetManager`

#### Scenario: Model download in portable mode
- **WHEN** the user downloads the recommended models from Settings in the portable app
- **THEN** the model files are saved in `data\models` next to the app

### Requirement: Development data folder
When the app does not run from the portable package (for example a build started from the repository), it SHALL keep its data in `%LOCALAPPDATA%\AssetManager`, as before.

#### Scenario: Debug build
- **WHEN** a developer runs the app from its build output folder
- **THEN** the catalog, settings and models are read from and written to `%LOCALAPPDATA%\AssetManager`

### Requirement: Fallback when the app folder is read-only
If the portable app cannot create or write its `data` folder, it SHALL use `%LOCALAPPDATA%\AssetManager` instead, keep working, and state in Settings that portable storage was not possible and why.

#### Scenario: Extracted under Program Files
- **WHEN** the portable package is extracted to a folder the user cannot write to and started without administrator rights
- **THEN** the app starts with its data in `%LOCALAPPDATA%\AssetManager`, and Settings explains that the app folder is not writable

### Requirement: Data folder override
An environment variable SHALL be able to set the data folder for any build. When set, the app SHALL use that folder, creating it if needed, instead of the portable or development default.

#### Scenario: Override
- **WHEN** the app starts with the override variable set to `D:\AMData`
- **THEN** the catalog, settings, models, cache and logs are kept in `D:\AMData`

### Requirement: Relocatable settings
Paths in the settings that point inside the data folder (the catalog file and model files) SHALL be stored relative to the data folder and resolved against its current location. Paths outside the data folder SHALL be stored as given.

#### Scenario: Portable folder moved
- **WHEN** a portable copy with a catalog and downloaded models is moved from `E:\Tools\AssetManager` to `F:\AssetManager` (or its drive gets another letter)
- **THEN** the app opens the same catalog and starts the same models from the new location without any setting being changed

#### Scenario: Catalog outside the data folder
- **WHEN** the user opens a catalog at `D:\Catalogs\games.amdb` and restarts the app
- **THEN** the app opens `D:\Catalogs\games.amdb` again

### Requirement: Data folder shown in Settings
Settings SHALL show the data folder in use, whether the app runs in portable mode, and the reason for any fallback, and SHALL offer to open the data folder in Explorer.

#### Scenario: Portable mode shown
- **WHEN** the user opens Settings in a portable copy
- **THEN** Settings shows "Portable" with the path of the `data` folder and a button that opens it in Explorer
