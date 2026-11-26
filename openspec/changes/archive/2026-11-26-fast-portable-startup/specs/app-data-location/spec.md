# Spec Delta

## MODIFIED Requirements

### Requirement: Portable data folder
When the app runs from the portable package, it SHALL keep all data it creates in a `data` folder next to `AssetManager.exe` by default: the default catalog, the settings file, downloaded AI models, temporary files, logs and the embedded browser's profile. It SHALL NOT write these into the user profile.

#### Scenario: First start of a portable copy
- **WHEN** the portable package is extracted to `E:\Tools\AssetManager` and started for the first time
- **THEN** `E:\Tools\AssetManager\data` holds the new catalog, `settings.json` and the logs, and nothing except the extraction cache, once an archive is imported, is created under `%LOCALAPPDATA%\AssetManager`

#### Scenario: Model download in portable mode
- **WHEN** the user downloads the recommended models from Settings in the portable app
- **THEN** the model files are saved in `data\models` next to the app

## ADDED Requirements

### Requirement: Disposable stores on the system drive
Two disposable stores SHALL be kept on the system drive, because a portable folder may be on a slow drive: the extraction cache, in `%LOCALAPPDATA%\AssetManager\cache` unless the user picks another folder in Settings, and the runtime that the single-file `AssetManager.exe` unpacks once per version under `%TEMP%`. The app SHALL remove the unpacked runtime of older versions.

#### Scenario: Importing archives from a portable copy on a USB hard drive
- **WHEN** the portable copy is on a USB hard drive and the user imports a folder of zip archives
- **THEN** the archives are extracted into the cache on the system drive, and the USB drive is not kept busy by the extraction

#### Scenario: Cache folder chosen by the user
- **WHEN** the user picks another extraction cache folder in Settings → Import and restarts the app
- **THEN** archives are extracted into that folder

#### Scenario: Older version's runtime removed
- **WHEN** a new version of the portable app is started after an older one
- **THEN** the folder the older version unpacked under `%TEMP%` is removed once the window is ready, unless that version is still running
