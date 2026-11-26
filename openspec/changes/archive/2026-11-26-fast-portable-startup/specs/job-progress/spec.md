# Spec Delta

## ADDED Requirements

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
