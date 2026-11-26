# catalog-backup Specification

## Purpose
TBD - created by archiving change asset-workflow-upgrades. Update Purpose after archive.

## Requirements

### Requirement: Catalog backups
The app SHALL make consistent backup copies of the open catalog file while the app is running and AI or import jobs may be writing. Each backup SHALL be a complete catalog file that opens on its own. Backups SHALL be written to a backup folder, by default `backups` in the data folder, which the user can change. Each backup's name SHALL include the catalog name and the date and time.

#### Scenario: Backup during enrichment
- **WHEN** the user starts a backup while AI enrichment is saving results
- **THEN** the backup completes, and opening it shows a consistent catalog as of one point in time

### Requirement: Backup schedule and retention
The user SHALL be able to:
- back up now from Settings
- choose an automatic schedule: off, at startup when the last backup is older than a day (default), or at startup when it is older than a week
- choose how many automatic backups to keep (default 5)

The oldest automatic backups beyond that number SHALL be deleted. Manual backups SHALL NOT be deleted automatically. Backups SHALL run as background jobs with progress and SHALL NOT block browsing.

#### Scenario: Retention
- **WHEN** 5 automatic backups exist and a sixth is made
- **THEN** the oldest automatic backup is deleted and manual backups are kept

#### Scenario: Not enough space
- **WHEN** the backup folder's drive does not have room for a copy of the catalog
- **THEN** the backup job fails with a message naming the needed and the free space, and no partial backup file is left

### Requirement: Restore
Settings SHALL list the backups with their date, size and kind (automatic or manual). The user SHALL be able to restore one after a confirmation. Restoring SHALL:
- first back up the current catalog as a manual backup
- replace the catalog with the chosen backup
- reopen it

Running jobs SHALL be cancelled before the restore, and the confirmation SHALL say so.

#### Scenario: Restore yesterday's catalog
- **WHEN** the user restores yesterday's backup and confirms
- **THEN** the current catalog is saved as a manual backup, and the app reopens with yesterday's assets, tags and previews
