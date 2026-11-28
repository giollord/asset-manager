## ADDED Requirements

### Requirement: Imports commit without confirmation
An import SHALL add its assets and sources to the catalog by itself once metadata and previews are done, without asking the user to confirm. Each source SHALL be committed in one atomic operation. AI enrichment that is not finished SHALL continue in the background on the committed assets. Until the commit, the import's assets SHALL NOT appear in the catalog, and an interrupted import SHALL resume after an app restart.

#### Scenario: Assets appear after previews
- **WHEN** an import of 1000 items finishes generating previews while 300 items still await AI enrichment
- **THEN** all 1000 assets are in the catalog with their previews and metadata without any user action, and the 300 are enriched in the background afterwards

#### Scenario: No AI
- **WHEN** an import runs with "No AI"
- **THEN** its assets are in the catalog as soon as their previews are done, and the import finishes

#### Scenario: Restart before commit
- **WHEN** the app is closed while an import is generating previews, and is started again
- **THEN** the import resumes, and its assets reach the catalog once their previews are done

### Requirement: Cancelling an import
Before the commit, cancelling an import SHALL discard it, and nothing SHALL be added to the catalog. After the commit, cancelling SHALL only stop the import's remaining AI work. The committed assets SHALL stay in the catalog, and the user can delete them with the catalog's delete action.

#### Scenario: Cancel during previews
- **WHEN** the user cancels an import while previews are being generated
- **THEN** no assets, tags or sources from it are added to the catalog

#### Scenario: Cancel during AI enrichment
- **WHEN** the user cancels an import after its assets were committed and while the AI item pass runs
- **THEN** the AI work stops, the assets stay in the catalog, and the ones not yet enriched stay without AI enrichment until the user re-enriches them

### Requirement: Import results
After the commit, the import page SHALL show a summary of the import. The summary SHALL include:
- the number of assets added per source
- the sources replaced by a new version, with their updated, added and removed counts
- the skipped items with their reasons: scripts, duplicates, unsupported, ignored folders, errors

The summary SHALL stay available after an app restart until the user dismisses it or starts the next import.

#### Scenario: Skipped items listed
- **WHEN** a folder contains `.cs` scripts and a corrupt zip
- **THEN** the import results list them as skipped, with the reasons "script (noted)" and "cannot open archive"

#### Scenario: Open the imported assets
- **WHEN** the user opens a source from the import results
- **THEN** the catalog shows that source's assets, where they can be edited, re-enriched or deleted

## MODIFIED Requirements

### Requirement: Ignored folders
An import SHALL skip folders that hold no game assets. A default list SHALL be stored with the catalog and be editable in the settings; it SHALL include at least `node_modules`, `.git`, `.svn`, `.hg`, `__pycache__`, `.vs`, `.vscode`, `.idea`, `bower_components`, `.venv` and `__MACOSX`. For one import, the user SHALL be able to add more rules and to turn the default list off. Each ignored folder SHALL be listed once among the skipped items, with the rule that matched it.

#### Scenario: Default ignore
- **WHEN** the user imports a project folder whose `tools/node_modules` holds 3,000 files
- **THEN** no file from `node_modules` is scanned or imported, and the import results list `tools/node_modules` once as skipped with the reason "ignored folder (node_modules)"

#### Scenario: Extra ignore for one import
- **WHEN** the user adds the rule `Assets/*/Demo` for one import of a Unity package
- **THEN** every `Demo` folder directly under a folder in `Assets` is skipped in that import, and later imports do not use the rule

### Requirement: Ignore rule matching
A rule without a slash SHALL match a folder of that name at any depth; a rule with a slash SHALL match a folder path relative to the imported folder, archive or package. Rules SHALL be case-insensitive and MAY use `*` wildcards. Rules SHALL apply inside archives and packages too, and containers inside an ignored folder SHALL NOT be opened. An archive or package file counts as a folder: a rule that matches its name or path SHALL keep it closed.

#### Scenario: Ignored folder inside a package
- **WHEN** a zip contains `Pack/.git/` with an archive inside it, and `Pack/Models/`
- **THEN** the models are imported, `Pack/.git` is listed once as ignored, and the archive inside it is not opened

#### Scenario: Ignored archive
- **WHEN** a folder holds `Sounds/` and its copy `Sounds.zip`, and the import adds the rule `Sounds.zip`
- **THEN** the zip is listed once as ignored and not opened, and the folder is imported

### Requirement: Duplicate detection
The system SHALL detect files that are already cataloged, by content hash and source location. Duplicates SHALL be skipped and listed among the skipped items, unless the user turned on "Include files that are already cataloged" when starting the import; then they SHALL be imported as new assets.

#### Scenario: Re-adding same folder
- **WHEN** the user imports a folder that was already imported unchanged
- **THEN** no assets are added, and the import results list all items as skipped because they are already cataloged

#### Scenario: Duplicates included
- **WHEN** the user turns on "Include files that are already cataloged" and imports a folder that was already imported
- **THEN** its files are added to the catalog again as a new source

### Requirement: Update source from new version
When an import has the same Unity Asset Store id as a cataloged source, the system SHALL replace that source with the import without asking. A match found only by overlapping Unity GUIDs or relative paths SHALL NOT replace anything; the import SHALL become a new, independent source. An import the user started to update a specific source SHALL replace that source. On replacement:
- assets matched by GUID or path SHALL update in place, keeping user edits and user tags
- new items SHALL be added
- missing items SHALL be removed

The import results SHALL show the updated, added and removed counts.

#### Scenario: Renamed package version
- **WHEN** the user imports "GKC 3.77c ….unitypackage" and "GKC 3.6 ….unitypackage" is already cataloged with the same Asset Store id
- **THEN** the old source is replaced, and the import results show the counts of updated, added and removed assets

#### Scenario: Removed asset in new version
- **WHEN** the new package version with the same Asset Store id no longer contains a texture that the old version had
- **THEN** that texture's asset is removed from the catalog

#### Scenario: User declines replacement
- **WHEN** the user imports a folder whose relative paths overlap 80% with a cataloged source, and neither has an Asset Store id
- **THEN** no replacement happens: the folder is imported as a new, independent source, and the cataloged source is unchanged

#### Scenario: Explicit update
- **WHEN** the user chooses "Update source" for a changed archive source in the rescan results
- **THEN** the import of that archive replaces the source, even without an Asset Store id

## REMOVED Requirements

### Requirement: Staged import preview
**Reason**: Imports no longer wait for confirmation, so there is no preview to review before the commit.
**Migration**: The import page shows progress while the import runs and the import results afterwards. The new requirements "Imports commit without confirmation", "Cancelling an import" and "Import results" cover the commit, the cancel behavior and the skipped-items list.

### Requirement: Edit and AI-correct staged items
**Reason**: Without a review step there are no staged items to edit, include, exclude or regroup before the commit.
**Migration**: Edit imported assets in the catalog: metadata editing, bulk tags, Re-enrich, and Bulk delete for unwanted items. An import's AI instruction still applies to every item.

### Requirement: Confirm import
**Reason**: The commit happens on its own once previews are done.
**Migration**: Replaced by "Imports commit without confirmation". Sessions an earlier version left waiting for confirmation are committed when the app starts.
