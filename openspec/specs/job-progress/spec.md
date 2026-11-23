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
4. AI pack pass
5. AI item pass
6. commit

For source version updates, the commit stage SHALL report updated, added and removed counts.

#### Scenario: Stages visible in wizard
- **WHEN** an import is in the AI item pass
- **THEN** the wizard shows the earlier stages as complete, the item pass with its done/total count, and the overall percentage

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
While any job is running, the app bar SHALL show a compact indicator with the number of active jobs and the combined progress. The indicator SHALL open a quick view of the jobs that links to the Jobs page. When a job finishes, the app SHALL show a brief notification with its outcome.

#### Scenario: Notification on completion
- **WHEN** an import job completes with 3 errors
- **THEN** a notification says it finished with 3 errors and links to the job's details

### Requirement: Job errors and history
Each job SHALL keep a list of the items that failed, each with a reason. The user SHALL be able to retry the failed items. Finished jobs SHALL stay in a history with a summary, keeping at least the last 100 jobs, and that history SHALL survive an app restart.

#### Scenario: Retry failed items
- **WHEN** a preview job finished with 7 failed models and the user chooses "Retry failed"
- **THEN** a new job processes only those 7 models

#### Scenario: History after restart
- **WHEN** the app is restarted
- **THEN** the Jobs page still lists recently finished jobs with their summaries
