## MODIFIED Requirements

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
