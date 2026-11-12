## ADDED Requirements

### Requirement: Supported containers
The system SHALL read assets from:
- plain files
- folders, scanned recursively
- zip, rar, 7z, tar, tar.gz and gz archives
- Unity `.unitypackage` files

#### Scenario: Folder import is recursive
- **WHEN** the user adds a folder containing subfolders with images
- **THEN** images in all subfolders are discovered

#### Scenario: Unsupported or corrupt archive
- **WHEN** an archive cannot be opened, for example because it is corrupt or password-protected
- **THEN** the source is reported with an error in the import preview and the rest of the import continues

### Requirement: Nested containers
The system SHALL descend into containers found inside other containers, such as an archive inside an archive or a `.unitypackage` inside an archive or inside another `.unitypackage`. It SHALL do this to a configurable maximum depth, which defaults to 5. Each asset's location SHALL record the full chain from the outermost file to the inner entry.

#### Scenario: Package inside zip
- **WHEN** a zip file contains a `.unitypackage` that contains a texture
- **THEN** the texture is imported and its location shows zip → unitypackage → texture path

### Requirement: Unity package reading
For `.unitypackage` files, the system SHALL reconstruct the original project path of every entry and the entry's Unity GUID. It SHALL use the embedded Unity preview image when no better preview can be generated. It SHALL resolve material-to-texture references through GUIDs, and read the Asset Store metadata embedded in the package header (id, title, publisher, version) when present.

#### Scenario: Original paths restored
- **WHEN** a Unity package is read
- **THEN** each asset shows its original `Assets/...` path instead of the internal GUID folder name

#### Scenario: Asset Store link detected
- **WHEN** a package downloaded from the Unity Asset Store is imported
- **THEN** the source stores the Asset Store package id, title, publisher, version and a store URL

### Requirement: Drive-independent locations
Locations on removable or external drives SHALL be stored relative to a volume identity, such as the volume serial and label, not only a drive letter. This SHALL let a drive that reconnects under a different letter be recognized.

#### Scenario: Drive letter changed
- **WHEN** an external drive formerly mounted as `E:` is reconnected as `F:`
- **THEN** its assets are resolved to `F:` and reported as available without re-import

### Requirement: Offline availability
The catalog SHALL remain fully browsable and searchable when the original files are unavailable, including previews, metadata, tags, licenses and descriptions. The system SHALL show for each asset whether its original is currently available.

#### Scenario: Drive detached
- **WHEN** the external drive is detached and the user searches and opens an asset's details
- **THEN** results, previews and metadata are shown, and the asset is marked "original unavailable"

#### Scenario: Action needing original while offline
- **WHEN** the user tries to open, copy or extract an asset whose original is unavailable
- **THEN** the system reports which drive or path is missing and does nothing else

### Requirement: Stored originals
The user SHALL be able to store an asset's original files (primary and companions) inside the catalog file and later remove that copy, for one asset or a multi-selection, via a compact secondary control (icon button or overflow menu). A stored asset SHALL count as available while its drive is detached, and opening, copying and extracting SHALL use the stored copy.

#### Scenario: Store selected assets
- **WHEN** the user selects 8 assets whose originals are available and chooses "Store in catalog"
- **THEN** the original files of all 8 assets are copied into the catalog file, and the assets are marked as stored

#### Scenario: Use stored copy offline
- **WHEN** an asset is stored and its external drive is detached
- **THEN** the asset is shown as available, and opening, copying and extracting it use the stored copy

#### Scenario: Remove stored copies
- **WHEN** the user selects stored assets and chooses "Remove stored copy"
- **THEN** the stored files are deleted from the catalog, the assets keep their metadata and previews, and their availability again depends on the source location

#### Scenario: Store when original unavailable
- **WHEN** the user tries to store an asset whose original is unavailable and not already stored
- **THEN** that asset is skipped and reported, and the other selected assets are stored

#### Scenario: Space reclaimed
- **WHEN** stored copies totaling 2 GB are removed
- **THEN** the catalog file's free space is reclaimed, at the latest by an explicit or automatic compaction

### Requirement: Temporary extraction cache
Entries inside containers SHALL be extracted to an app-managed temporary cache only when needed: for processing, opening, copying or extracting. The cache SHALL be cleaned automatically, either when it reaches a size limit or when it holds files older than a set age. The cache SHALL NOT be the only copy of any data the catalog depends on.

#### Scenario: Cache cleanup
- **WHEN** the cache exceeds its configured size limit
- **THEN** least-recently-used extracted files are removed until it is under the limit
