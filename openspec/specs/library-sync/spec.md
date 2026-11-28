# library-sync Specification

## Purpose
TBD - created by archiving change asset-workflow-upgrades. Update Purpose after archive.

## Requirements

### Requirement: Rescan imported sources
The user SHALL be able to rescan one, several or all imported sources whose originals are available. A rescan SHALL compare each source with its catalog record, without changing the catalog:
- **folder sources:** files added, changed (size or modification time) or removed since the import
- **archive and package sources:** whether the file's fingerprint changed

#### Scenario: New files in a folder pack
- **WHEN** 6 PNG files were added to an imported folder source and the user rescans it
- **THEN** the rescan reports 6 added files for that source, and the catalog is unchanged until the user acts

### Requirement: Rescan follows import rules
A folder rescan SHALL apply the ignored-folder rules and the non-asset file rules the same way the import does, so files the import skipped are not reported as added.

#### Scenario: Ignored folder
- **WHEN** a `__MACOSX` folder with 20 files was added to a folder source and `__MACOSX` is an ignored folder
- **THEN** the rescan reports no added files

### Requirement: Detached sources skipped
Sources on detached drives SHALL be reported as skipped, not as removed.

#### Scenario: Detached drive
- **WHEN** all sources are rescanned and one source's drive is detached
- **THEN** that source is listed as skipped because its drive is unavailable, and none of its assets are reported as removed

### Requirement: Acting on rescan results
For each changed source, the user SHALL be able to:
- **import the added files** into that source; they go into the catalog like any other import, with no review step
- **re-import the changed files' assets**
- **remove the assets whose files are gone**, after a confirmation that states the count
- **update** a changed archive or package; the import replaces that source through the "Update source from new version" flow, with no further confirmation

New assets SHALL get the source's source tags.

#### Scenario: Import added files
- **WHEN** the user chooses to import the 6 added files
- **THEN** an import starts, and once its previews are done the 6 new assets are in the existing source and carry the source's source tags

#### Scenario: Update changed package
- **WHEN** the user chooses "Update source" for a changed `.unitypackage` source that has no Asset Store id
- **THEN** the package is imported again and replaces that source, and the import results show the updated, added and removed counts

### Requirement: Library folders
The user SHALL be able to register library folders in Settings: folders where new packs appear, such as a downloads or assets folder. Scanning library folders SHALL list the immediate entries (folders, archives, `.unitypackage` files) that are not yet imported as a source. Entries that look like new versions of an existing source SHALL be marked as such. The user SHALL be able to import the chosen entries in one import, which adds them to the catalog without a review step. Ignored-folder rules SHALL apply.

#### Scenario: New pack downloaded
- **WHEN** `E:\Assets` is a library folder and the user adds `ForestPack.zip` to it and scans
- **THEN** `ForestPack.zip` is listed as not imported and can be imported from the list

#### Scenario: Marked version imported
- **WHEN** the user imports a library entry marked as a new version of a source only because their paths overlap
- **THEN** it is imported as a new, independent source, following "Update source from new version"

### Requirement: When scans run
Rescans and library scans SHALL run as background jobs with progress and SHALL be cancellable. They SHALL run when the user starts them. They SHALL also run once after startup when the user turned on "check for changes at startup", which is off by default. A startup scan that finds changes SHALL show a notification that opens the results, and SHALL NOT import anything by itself.

#### Scenario: Startup check
- **WHEN** "check for changes at startup" is on and a library folder has two new packs
- **THEN** after startup a notification says 2 new packs were found, and nothing is imported until the user confirms
