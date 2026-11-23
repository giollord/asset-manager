## ADDED Requirements

### Requirement: Import input selection
The user SHALL be able to start an import from one or more files, folders, archives and Unity packages, using pickers or drag-and-drop. For the whole batch, the user SHALL be able to enter optional shared tags and an optional free-text instruction for the AI.

#### Scenario: Shared tag for batch
- **WHEN** the user imports 3 folders and 2 archives with shared tag "humble-lowpoly"
- **THEN** every asset created by that import carries the tag "humble-lowpoly"

#### Scenario: AI instruction
- **WHEN** the user enters the instruction "these are all sci-fi UI elements; ignore the PSD sources"
- **THEN** the instruction is given to the AI for every item in this import, and the AI's suggestions reflect it

### Requirement: Ignored folders
An import SHALL skip folders that hold no game assets. A default list SHALL be stored with the catalog and be editable in the settings; it SHALL include at least `node_modules`, `.git`, `.svn`, `.hg`, `__pycache__`, `.vs`, `.vscode`, `.idea`, `bower_components`, `.venv` and `__MACOSX`. For one import, the user SHALL be able to add more rules and to turn the default list off. Each ignored folder SHALL be listed once among the skipped items, with the rule that matched it.

#### Scenario: Default ignore
- **WHEN** the user imports a project folder whose `tools/node_modules` holds 3,000 files
- **THEN** no file from `node_modules` is scanned or staged, and the preview lists `tools/node_modules` once as skipped with the reason "ignored folder (node_modules)"

#### Scenario: Extra ignore for one import
- **WHEN** the user adds the rule `Assets/*/Demo` for one import of a Unity package
- **THEN** every `Demo` folder directly under a folder in `Assets` is skipped in that import, and later imports do not use the rule

### Requirement: Ignore rule matching
A rule without a slash SHALL match a folder of that name at any depth; a rule with a slash SHALL match a folder path relative to the imported folder, archive or package. Rules SHALL be case-insensitive and MAY use `*` wildcards. Rules SHALL apply inside archives and packages too, and containers inside an ignored folder SHALL NOT be opened. An archive or package file counts as a folder: a rule that matches its name or path SHALL keep it closed.

#### Scenario: Ignored folder inside a package
- **WHEN** a zip contains `Pack/.git/` with an archive inside it, and `Pack/Models/`
- **THEN** the models are staged, `Pack/.git` is listed once as ignored, and the archive inside it is not opened

#### Scenario: Ignored archive
- **WHEN** a folder holds `Sounds/` and its copy `Sounds.zip`, and the import adds the rule `Sounds.zip`
- **THEN** the zip is listed once as ignored and not opened, and the folder is imported

### Requirement: Staged import preview
An import SHALL first produce a staged preview and SHALL NOT commit anything to the catalog yet. The preview SHALL show the proposed assets grouped by source, with:
- previews
- type
- name
- tags
- description
- license
- grouped companion files
- items to be skipped, with reasons (scripts, duplicates, unsupported, ignored folders, errors)

AI results SHALL appear as they complete, and the staged session SHALL survive an app restart.

#### Scenario: Nothing committed before confirm
- **WHEN** the user opens the import preview and then cancels the import
- **THEN** no assets, tags or sources are added to the catalog

#### Scenario: Skipped items listed
- **WHEN** a folder contains `.cs` scripts and a corrupt zip
- **THEN** the preview lists them as skipped, with the reasons "script (noted)" and "cannot open archive"

### Requirement: Edit and AI-correct staged items
In the preview, the user SHALL be able to select staged items, edit them directly, include or exclude them, and change how files are grouped. The user SHALL also be able to send a correction message to the AI for the selected items only. The AI SHALL re-process just those items with the message as added context.

#### Scenario: AI correction on selection
- **WHEN** the user selects 20 staged sounds and asks the AI "these are footsteps on gravel, not rain"
- **THEN** only those 20 items are re-enriched, and their descriptions and tags change accordingly

#### Scenario: Exclude item
- **WHEN** the user excludes a staged item
- **THEN** it is not created on confirm

### Requirement: Confirm import
On confirmation, the system SHALL commit the included staged items to the catalog in one atomic operation per source. Pending AI enrichment SHALL continue in the background after commit if it is not finished.

#### Scenario: Confirm before AI finishes
- **WHEN** the user confirms while 300 of 1000 items still await AI enrichment
- **THEN** all 1000 assets are committed with their previews and metadata, and the 300 are enriched in the background afterwards

### Requirement: Duplicate detection
The system SHALL detect files that are already cataloged, by content hash and source location, and mark them as duplicates in the preview. Duplicates SHALL be skipped by default, and the user SHALL be able to include them.

#### Scenario: Re-adding same folder
- **WHEN** the user imports a folder that was already imported unchanged
- **THEN** all items are marked as already cataloged and skipped by default

### Requirement: Re-import selected assets
The user SHALL be able to re-import a selected set of assets. Re-import SHALL re-read the originals, regenerate metadata and previews, and optionally re-run AI enrichment, while keeping user-edited fields and user tags.

#### Scenario: Re-import keeps user tags
- **WHEN** the user re-imports an asset that has the user tag "favorite"
- **THEN** its previews and technical metadata are refreshed and the "favorite" tag remains

### Requirement: Update source from new version
When the Unity Asset Store id matches, or Unity GUIDs or relative paths substantially overlap, the system SHALL propose the import as a new version of an existing source. The user SHALL confirm, reject or pick the source manually. On replacement, assets matched by GUID or path SHALL update in place, keeping user edits; new items SHALL be added and missing ones removed. The preview SHALL show these counts.

#### Scenario: Renamed package version
- **WHEN** the user imports "GKC 3.77c ….unitypackage" and "GKC 3.6 ….unitypackage" is already cataloged with the same Asset Store id
- **THEN** the preview proposes replacing the old source and shows the counts of updated, added and removed assets

#### Scenario: Removed asset in new version
- **WHEN** the new package version no longer contains a texture that the old version had
- **THEN** after confirmation, that texture's asset is removed from the catalog

#### Scenario: User declines replacement
- **WHEN** the user rejects the proposed replacement
- **THEN** the package is imported as a new, independent source
