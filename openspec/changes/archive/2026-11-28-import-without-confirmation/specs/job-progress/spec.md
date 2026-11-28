## MODIFIED Requirements

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
