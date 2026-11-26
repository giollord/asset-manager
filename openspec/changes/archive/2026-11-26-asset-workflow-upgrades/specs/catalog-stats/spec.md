## ADDED Requirements

### Requirement: License audit
The app SHALL provide a license audit listing each source, with its asset count, under each problem it has:
- **unknown license:** no license kind, or commercial use unknown
- **attribution missing:** attribution required, no attribution text
- **non-commercial:** commercial use not allowed
- **overrides differ:** asset overrides differ from the source license

The audit SHALL update when licenses are edited and work with drives detached.

#### Scenario: Missing attribution text
- **WHEN** a source's license says attribution is required and its attribution text is empty
- **THEN** the source appears under "attribution missing", and after the user enters an attribution text it disappears from that list

### Requirement: License audit on the dashboard
The dashboard SHALL show the number of affected sources and assets for each license audit problem. Clicking a problem SHALL open the audit list filtered to it. From the list, the user SHALL be able to open the source for editing, or open Browse filtered to its affected assets.

#### Scenario: Dashboard tile
- **WHEN** 3 sources with 410 assets have an unknown license
- **THEN** the dashboard shows "Unknown license: 3 sources, 410 assets", and clicking it opens the audit list filtered to those 3 sources
