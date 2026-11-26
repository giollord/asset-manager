# Spec Delta

## ADDED Requirements

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
