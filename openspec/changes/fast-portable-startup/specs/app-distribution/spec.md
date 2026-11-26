# Spec Delta

## ADDED Requirements

### Requirement: Compact package layout
The portable package SHALL contain one `AssetManager.exe` holding the app, the .NET runtime, the Windows UI runtime and the app's web content. Beside it SHALL be only `portable`, `LICENSE.txt`, the `llama/` and `ffmpeg/` folders, and at most 5 other files the platform requires on disk. It SHALL NOT contain locale folders, debug symbols or packaged-app tile images. The package check SHALL fail otherwise.

#### Scenario: Top level of the extracted zip
- **WHEN** the zip of a tagged release is extracted and its top level is listed
- **THEN** it shows `AssetManager.exe`, `portable`, `LICENSE.txt`, `llama`, `ffmpeg` and no more than 5 other files, and no other folders

#### Scenario: Extra folder fails the check
- **WHEN** a build change makes the publish output contain a locale folder such as `de-DE`
- **THEN** the package check fails and names the unexpected entry

#### Scenario: Compact package still runs
- **WHEN** the compact package is extracted on a clean Windows 10 or 11 x64 machine and `AssetManager.exe` is started
- **THEN** the app opens with its full UI (styles, icons, 3D viewer) and can import a folder of images, as before

### Requirement: Window shown before AI models load
When the app is started, it SHALL show its main window and let the user browse and search the catalog before it starts loading AI models. Loading the models SHALL NOT delay the window. While the models load, the AI status SHALL show that they are starting.

#### Scenario: Start from an external drive
- **WHEN** the portable folder, including its models, is on a slow external drive and the app is started with the OS file cache cold
- **THEN** the main window and the catalog appear before the chat model has finished loading, and the AI status shows "starting" until it is ready

#### Scenario: Pending AI work at start
- **WHEN** the app is started while enrichment jobs are pending
- **THEN** the window appears first, and the jobs continue once the chat model is ready
