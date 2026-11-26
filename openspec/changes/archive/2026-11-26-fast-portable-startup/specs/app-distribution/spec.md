# Spec Delta

## ADDED Requirements

### Requirement: Compact package layout
The portable package SHALL contain one `AssetManager.exe` holding the app, the .NET runtime and the Windows UI runtime, with the app's web content as one archive beside it. Beside the exe SHALL be only `portable`, `LICENSE.txt`, the `llama/`, `ffmpeg/` and `licenses/` (third-party license texts) folders, and at most 5 other files. It SHALL NOT contain locale folders, debug symbols or packaged-app tile images. The package check SHALL fail otherwise.

#### Scenario: Top level of the extracted zip
- **WHEN** the zip of a tagged release is extracted and its top level is listed
- **THEN** it shows `AssetManager.exe`, `portable`, `LICENSE.txt`, `llama`, `ffmpeg`, `licenses` and no more than 5 other files, and no other folders

#### Scenario: Extra folder fails the check
- **WHEN** a build change makes the publish output contain a locale folder such as `de-DE`
- **THEN** the package check fails and names the unexpected entry

#### Scenario: Compact package still runs
- **WHEN** the compact package is extracted on a clean Windows 10 or 11 x64 machine and `AssetManager.exe` is started
- **THEN** the app opens with its full UI (styles, icons, 3D viewer) and can import a folder of images, as before

### Requirement: Window shown before AI models load
When the app is started, it SHALL show its main window and its first page's data before any AI model loads. Loading models SHALL NOT delay the window. When "Load AI models at startup" is on, the models SHALL load after the first page shows its data, and the AI status SHALL show that they are starting until they are ready.

#### Scenario: Start from an external drive
- **WHEN** the portable folder, including its models, is on a slow external drive, "Load AI models at startup" is on, and the app is started with the OS file cache cold
- **THEN** the main window and the Dashboard's numbers appear before the models start loading, and the AI status shows "starting" until they are ready

#### Scenario: Pending AI work at start
- **WHEN** the app is started while enrichment jobs are pending
- **THEN** the window appears first, and the jobs load the chat model when they reach an AI step and then continue
