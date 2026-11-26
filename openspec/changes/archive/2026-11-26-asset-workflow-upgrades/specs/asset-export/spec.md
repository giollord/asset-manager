## ADDED Requirements

### Requirement: Drag out to other apps
In the desktop app, the user SHALL be able to drag one asset or the current selection from the Browse grid into any application that accepts dropped files, such as Explorer, Unity, Godot or Blender. The drop SHALL deliver each asset's primary and companion files.

#### Scenario: Drag textures into Unity
- **WHEN** the user drags 3 selected textures that live inside a zip onto Unity's Project window
- **THEN** the 3 texture files are extracted to the cache and dropped into Unity

### Requirement: Drag out of packed or unavailable assets
Files inside containers SHALL be extracted to the temporary cache before the drag starts, with a progress indicator when that takes noticeable time. Assets whose originals are unavailable and not stored SHALL be left out and reported, as for the other export actions.

#### Scenario: Unavailable item in selection
- **WHEN** the user drags 4 assets, one of which is on a detached drive and not stored
- **THEN** the 3 available assets are dropped and the unavailable one is reported

### Requirement: Unity project export
When extracting assets that come from a Unity package or a Unity project folder, the user SHALL be able to choose "Unity project export". The option SHALL be on by default when the target folder is inside a Unity project, meaning a parent folder contains both `Assets` and `ProjectSettings`. With the option on, each exported file's original `.meta` SHALL be written next to it, keeping its GUID and import settings. A file without a `.meta` SHALL be exported without one.

#### Scenario: Meta files kept
- **WHEN** the user exports 5 textures from a `.unitypackage` into `MyGame/Assets/Imported`
- **THEN** each texture is written with its original `.meta`, and Unity keeps their GUIDs and import settings

### Requirement: Referenced files in Unity export
With "include referenced files", a Unity project export SHALL add the files that the exported assets reference by GUID from the same source, following references across files. This covers materials and their textures, and the meshes and materials of prefabs. Unity prefabs (`.prefab`) SHALL be cataloged as assets of a new type, Prefab (no preview), so they can be selected and exported. Other Unity YAML files (`.asset`, `.controller`, `.overrideController`, `.anim`) stay noted rather than imported, but SHALL still be written when an exported asset references them, read from the source package or folder.

#### Scenario: Prefab with dependencies
- **WHEN** the user exports a prefab from a `.unitypackage` with "include referenced files"
- **THEN** the prefab, its mesh, its 2 materials and their 4 textures are written with their `.meta` files, and Unity shows the prefab with its materials intact

### Requirement: GUID conflicts in Unity export
Before writing, a Unity project export SHALL report the GUIDs that already exist in the target project. The user SHALL choose to skip those files or continue.

#### Scenario: GUID already in project
- **WHEN** the target project already contains a file with the same GUID as an exported texture
- **THEN** the export lists that texture as a GUID conflict before writing anything

### Requirement: Credits report
The user SHALL be able to export a credits report for a selection, a collection or the current search results, as Markdown, plain text or CSV. The report SHALL group assets by source. For each source it SHALL list:
- the license kind and commercial use
- the attribution text
- the author or publisher
- the links

Assets with their own license override SHALL be listed with that license. The report SHALL be created from the catalog, so it works when drives are detached.

#### Scenario: Credits offline
- **WHEN** all drives are detached and the user exports credits for 50 assets
- **THEN** the report is created with the same content as when the drives are attached

### Requirement: Unknown licenses first in credits
In a credits report, assets whose license is unknown SHALL be listed first under a warning heading.

#### Scenario: Credits for a collection
- **WHEN** the user exports Markdown credits for a collection with assets from 3 sources, one of which has an unknown license
- **THEN** the file starts with a warning listing the source with the unknown license, followed by one section per source with its license, attribution text and links

## MODIFIED Requirements

### Requirement: Open original
The user SHALL be able to open an asset's original. Images, audio and 3D models SHALL open in an in-app viewer: the 3D viewer is interactive and can orbit, and audio plays at full length. When a 3D model's original is not available and not stored in the catalog, the in-app 3D viewer SHALL show the model's stored renders as a turntable (see asset-search "Inline audio and model inspection") with a note that the original is unavailable, instead of only an error. The user SHALL also be able to open the original in the operating system's default application, or reveal it in the file explorer when it is a plain file on disk. Originals inside containers SHALL be extracted to the temporary cache first.

#### Scenario: View model in app
- **WHEN** the user opens a 3D model asset whose original is available
- **THEN** an interactive textured 3D view of the full model is shown

#### Scenario: View model with the drive detached
- **WHEN** the user opens the 3D view of a model whose drive is detached and which is not stored in the catalog
- **THEN** the turntable of its stored renders is shown with a note that the original is not available, and dragging turns it

#### Scenario: Open archived file externally
- **WHEN** the user opens externally a texture that lives inside a zip
- **THEN** the texture is extracted to the cache and opened with the default application
