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
With "include referenced files", a Unity project export SHALL add the files that the exported assets reference by GUID from the same source, following references across files. This covers materials and their textures, and the meshes and materials of prefabs.

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
