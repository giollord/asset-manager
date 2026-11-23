# catalog-stats Specification

## Purpose
TBD - created by archiving change build-asset-manager. Update Purpose after archive.

## Requirements

### Requirement: Catalog overview dashboard
The app SHALL provide a dashboard page as its start page. It SHALL show:
- the total asset count, with a breakdown by asset type
- the number of sources, collections and tags
- the catalog file size, split into previews, stored originals and other data

All counts SHALL come from the catalog and SHALL be shown even when the external drives are detached.

#### Scenario: Totals shown
- **WHEN** the catalog holds 1,200 images, 300 sounds and 80 models from 15 sources
- **THEN** the dashboard shows 1,580 assets in total, the per-type counts 1,200 / 300 / 80, and 15 sources

#### Scenario: Works offline
- **WHEN** all external drives are detached
- **THEN** the dashboard shows the same counts as when they are attached

### Requirement: Status breakdowns
The dashboard SHALL show these counts, each clickable to open the matching filtered search:
- AI enrichment state: enriched, pending, failed, not enriched
- availability: original available, unavailable, stored in catalog
- license: commercial use allowed, not allowed, unknown, and attribution required
- model issues: scale anomaly, orientation uncertain or not verified, textures missing

#### Scenario: Drill down from a count
- **WHEN** the dashboard shows "Failed enrichment: 14" and the user clicks it
- **THEN** the Browse page opens filtered to those 14 assets

### Requirement: Collection, source and tag counts
Each collection SHALL show its current member count and a per-type breakdown in the collection list and on its page. Each source SHALL show its asset count. The tag list SHALL show how many assets carry each tag and SHALL be sortable by that count.

#### Scenario: Collection count follows tags
- **WHEN** a tag required by a collection is added to 5 more assets
- **THEN** the collection's displayed count increases by 5 the next time the list is shown

#### Scenario: Most-used tags
- **WHEN** the user sorts the tag list by usage
- **THEN** the tags are ordered by the number of assets carrying them, highest first

### Requirement: Search result count
The Browse page SHALL show the total number of assets matching the current filters. Semantic mode SHALL instead show the number of results returned.

#### Scenario: Filtered count
- **WHEN** the user filters to type "audio" and includes tag "footsteps"
- **THEN** the page shows how many assets match in total, not just how many are visible
