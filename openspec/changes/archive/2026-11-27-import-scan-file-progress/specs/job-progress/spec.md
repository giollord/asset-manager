# Spec Delta

## ADDED Requirements

### Requirement: Import scan counts files processed out of total
During the scan, an import job SHALL show how many files it has processed out of the total, counting only files that are not inside an archive or package. An archive or package counts as one file and is done once all of its contents are scanned. The total SHALL be known when the scan starts and SHALL leave out files the scan does not process. This done/total SHALL drive the scan's percentage, progress bar, time left and the overall import percentage.

#### Scenario: Folder with plain files and archives
- **WHEN** an import scans a folder holding 1,998 plain files and 2 zip archives, and has finished 600 of those 2,000 items
- **THEN** the job shows the scan stage with "600 / 2,000" and 30%, and the progress bar is not indeterminate

#### Scenario: Files inside an archive do not move the count
- **WHEN** the scan works through the 5,000 files inside `Forest.zip`, which is the 601st of 2,000 top-level files
- **THEN** the done count stays at 600 while those files show as the current file, and goes to 601 once the archive is finished

#### Scenario: Inner files still go into the found counts
- **WHEN** the scan has gone through `Forest.zip` with its 5,000 files
- **THEN** the "files found" and "importable" counts include the files inside the archive

#### Scenario: Overall percentage moves during the scan
- **WHEN** an import with 6 stages is halfway through its scan
- **THEN** the overall percentage shown in the wizard, on the Jobs page and in the app-bar quick view is about 8%, not 0%

#### Scenario: Ignored folders left out of the total
- **WHEN** an import scans a folder with 1,000 files, 200 of which are in a folder an ignore rule skips
- **THEN** the scan total is 800

#### Scenario: Rescan importing only the added files
- **WHEN** a rescan imports 12 added files from a source folder that holds 3,000 files
- **THEN** the scan total is 12

#### Scenario: Several inputs
- **WHEN** an import has two folders as inputs, with 300 and 700 top-level files
- **THEN** the scan total is 1,000 from the start, and the percentage does not go down when the scan moves on to the second folder

#### Scenario: Scan finishes
- **WHEN** the scan has processed every top-level file
- **THEN** the done count equals the total before the grouping stage starts
