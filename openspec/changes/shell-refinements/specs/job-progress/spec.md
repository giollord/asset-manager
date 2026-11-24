## MODIFIED Requirements

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
