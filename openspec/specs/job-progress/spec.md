# job-progress Specification

## Purpose
TBD - created by archiving change build-asset-manager. Update Purpose after archive.

## Requirements

### Requirement: Progress for every long-running operation
Every long-running operation SHALL run as a job with live progress: import, re-import, source update, AI enrichment and corrections, preview regeneration, store/unstore, export and compaction. Each job SHALL show its kind and target, current stage, done/total and percentage, current item, elapsed time and ETA, and warning and error counts.

#### Scenario: Import progress
- **WHEN** the user imports a folder with 2,000 files
- **THEN** a job shows the stage "Generating previews" with a done/total count, a percentage, the current file name and an estimated time remaining

#### Scenario: Re-import progress
- **WHEN** the user re-imports 150 selected assets
- **THEN** a "Re-import" job shows progress from 0 to 150 with its current stage

### Requirement: Multi-stage import progress
An import session SHALL show progress per stage and overall, both in the import wizard and on the Jobs page. The stages are:
1. scan
2. grouping
3. metadata and previews
4. commit
5. AI pack pass
6. AI item pass

The commit SHALL start on its own once metadata and previews are done. For source version updates, the commit stage SHALL report updated, added and removed counts.

#### Scenario: Stages visible in wizard
- **WHEN** an import is in the AI item pass
- **THEN** the wizard shows the earlier stages as complete, including the commit, the item pass with its done/total count, and the overall percentage

#### Scenario: Version update counts
- **WHEN** a source version update is committed
- **THEN** the job summary shows how many assets were updated, added and removed

### Requirement: AI enrichment queue progress
The app SHALL show the overall AI queue with:
- the pending item count
- the processing rate (items per minute)
- an estimated time to drain the queue
- the counts of items completed and failed in the current run

The user SHALL be able to pause, resume and cancel the queue.

#### Scenario: Queue overview
- **WHEN** 5,000 assets await enrichment
- **THEN** the Jobs page shows 5,000 pending, the current rate and an estimated completion time

#### Scenario: Pause and resume
- **WHEN** the user pauses the AI queue and later resumes it
- **THEN** no AI requests are made while it is paused, and processing continues from the remaining items after resume

### Requirement: Global progress indicator
While any job is running, the app bar SHALL show a compact indicator with the number of active jobs and the combined progress. The indicator SHALL open a quick view of the jobs that links to the Jobs page. When no job is running, the app bar SHALL show no jobs indicator or jobs button; the Jobs page stays reachable from the navigation menu. When a job finishes, the app SHALL show a brief notification with its outcome.

#### Scenario: Notification on completion
- **WHEN** an import job completes with 3 errors
- **THEN** a notification says it finished with 3 errors and links to the job's details

#### Scenario: Idle app bar
- **WHEN** no job is running
- **THEN** the app bar shows no jobs indicator or jobs button

#### Scenario: Indicator appears and disappears
- **WHEN** an import starts and later finishes
- **THEN** the indicator appears while the import runs and disappears once no job is running

### Requirement: Job errors and history
Each job SHALL keep a list of the items that failed, each with a reason. The user SHALL be able to retry the failed items. Finished jobs SHALL stay in a history with a summary, keeping at least the last 100 jobs, and that history SHALL survive an app restart.

#### Scenario: Retry failed items
- **WHEN** a preview job finished with 7 failed models and the user chooses "Retry failed"
- **THEN** a new job processes only those 7 models

#### Scenario: History after restart
- **WHEN** the app is restarted
- **THEN** the Jobs page still lists recently finished jobs with their summaries

### Requirement: Window stays responsive during background jobs
While background jobs (imports, scans, AI enrichment, embeddings, backups) run, the app window SHALL stay responsive: it SHALL never show as "Not Responding", and navigating and opening pages SHALL work. Background jobs SHALL yield disk access to the window's own reads, even when both use the same slow drive.

#### Scenario: Import scanning a large folder on the same drive
- **WHEN** an import is scanning a folder of 18,000 files on the external drive that also holds the app and its catalog
- **THEN** the window responds to input throughout, and opening the Dashboard or Browse shows its content while the scan continues

### Requirement: Interrupted jobs resume after the window is ready
Jobs that were pending or running when the app closed SHALL be queued again only after the main window's first page has shown its data, and not sooner than 5 seconds after start. When the AI models load at startup, the jobs SHALL also wait for them to load (at most 2 minutes). Until then, they SHALL be listed as waiting in the jobs panel.

#### Scenario: Interrupted import at start
- **WHEN** the app is started from a cold drive while an interrupted import is pending
- **THEN** the Dashboard shows its numbers first, and the import resumes afterwards and shows as running in the jobs panel

### Requirement: Job clock ticks on its own
While a job runs, its elapsed time SHALL go up every second wherever its progress is shown: the Jobs page, the app-bar quick view and the import wizard. It SHALL keep ticking when the job reports no new progress, when the current item stays the same, and while other data on the page is still loading. Its estimated time left SHALL be refreshed on the same tick.

#### Scenario: Long step with no progress events
- **WHEN** an import spends 30 seconds on one large file and reports no progress in that time
- **THEN** the job's elapsed time on the Jobs page goes up about once a second through those 30 seconds

#### Scenario: Slow page data does not hold the clock
- **WHEN** the Jobs page's AI queue status takes several seconds to load during an import
- **THEN** the import job's elapsed time keeps ticking every second in that time

#### Scenario: Clock stops when the job finishes
- **WHEN** a job finishes
- **THEN** its elapsed time stops at its total run time

### Requirement: Current archive shown while working inside it
While a job works on a file inside an archive or package, its progress SHALL show the container being read, next to the current file. For nested containers it SHALL show the whole chain, outermost first. When the job starts to open a container, the container's name SHALL show before its contents are extracted. When the job moves on to plain files outside any container, the container line SHALL disappear.

#### Scenario: File inside a zip
- **WHEN** the import scan reaches `Textures/bark.png` inside `E:\Packs\Forest.zip`
- **THEN** the job shows the current container `Forest.zip` and the current file `Textures/bark.png`

#### Scenario: Nested package
- **WHEN** the scan is inside `Forest.unitypackage`, which is inside `Packs.zip`
- **THEN** the container shows as `Packs.zip → Forest.unitypackage`

#### Scenario: Extracting a large archive
- **WHEN** the scan starts opening a 4 GB archive that takes a minute to extract
- **THEN** the job shows that archive's name during the extraction, not the name of the last file before it

#### Scenario: Back to plain files
- **WHEN** the scan has finished an archive and moves on to a plain file in the folder
- **THEN** no container is shown

### Requirement: Import shows what it found so far
During the scan, an import job SHALL show running counts of files found and of importable files, meaning files of a supported asset type. The counts SHALL update while the scan runs. When grouping finishes, the job SHALL add the number of assets, with each primary file and its companion files counting as one asset. The counts SHALL stay visible for the rest of the import, both on the Jobs page and in the import wizard.

#### Scenario: Counts grow during the scan
- **WHEN** the scan of a folder has gone through 1,200 files, of which 800 have a supported asset type
- **THEN** the job shows "1,200 files found" and "800 importable"

#### Scenario: Asset count after grouping
- **WHEN** grouping has combined those 800 importable files into 650 assets
- **THEN** the job also shows "650 assets" and keeps all three counts through the later stages

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
