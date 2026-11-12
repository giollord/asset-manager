## ADDED Requirements

### Requirement: Open original
The user SHALL be able to open an asset's original. Images, audio and 3D models SHALL open in an in-app viewer: the 3D viewer is interactive and can orbit, and audio plays at full length. The user SHALL also be able to open the original in the operating system's default application, or reveal it in the file explorer when it is a plain file on disk. Originals inside containers SHALL be extracted to the temporary cache first.

#### Scenario: View model in app
- **WHEN** the user opens a 3D model asset whose original is available
- **THEN** an interactive textured 3D view of the full model is shown

#### Scenario: Open archived file externally
- **WHEN** the user opens externally a texture that lives inside a zip
- **THEN** the texture is extracted to the cache and opened with the default application

### Requirement: Copy to clipboard as files
The user SHALL be able to copy one or more selected assets to the system clipboard as files, so that they can be pasted into Explorer or a game engine. Each asset SHALL include its primary file and its companion files. Files inside containers SHALL be extracted to a temporary folder first.

#### Scenario: Copy model with textures
- **WHEN** the user copies a model asset that has 3 companion textures and that lives inside a `.unitypackage`
- **THEN** the 4 files are extracted to a temporary folder and placed on the clipboard as a file list, ready to paste

### Requirement: Extract to folder
The user SHALL be able to extract one or more selected assets, including their companion files, to a folder they choose. The user SHALL be able to choose whether to preserve each asset's original relative folder structure or write flat. Name conflicts SHALL be resolved without silently overwriting files.

#### Scenario: Extract preserving structure
- **WHEN** the user extracts 10 assets from a Unity package to `D:\Proj\Assets\Imported` and keeps the structure
- **THEN** the files are written under their original relative `Assets/...` paths below the chosen folder

#### Scenario: Name conflict
- **WHEN** a target file already exists
- **THEN** the user is asked whether to overwrite, skip or rename, and can apply the choice to all remaining conflicts

### Requirement: Export when unavailable
Export actions SHALL use a stored copy in the catalog when one exists. Otherwise they SHALL report the assets whose originals are unavailable and still export the remaining assets.

#### Scenario: Partial availability
- **WHEN** the user copies 5 assets, 2 of which are on a detached drive and not stored
- **THEN** the 3 available assets are copied and the 2 unavailable ones are reported
