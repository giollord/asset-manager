## MODIFIED Requirements

### Requirement: Tags
Tags SHALL be case-insensitive, unique by normalized name and stored in English. An asset SHALL be able to have any number of tags. The user SHALL be able to add and remove tags on one or many selected assets at once. Each tag on an asset SHALL record its origin: user, import-wide, AI or source.

#### Scenario: Bulk tag add
- **WHEN** the user selects 50 assets and adds tag "medieval"
- **THEN** all 50 assets carry the tag "medieval" and assets already carrying it are unchanged

#### Scenario: Case-insensitive tag identity
- **WHEN** the user adds the tag "Sci-Fi" and the tag "sci-fi" already exists
- **THEN** the existing tag is reused and no duplicate tag is created

## ADDED Requirements

### Requirement: Source tags
A source SHALL be able to carry source tags, each added by the user or by the AI. Every asset of the source SHALL carry each source tag with origin "source", including assets added to the source later by rescan, re-import or a new version. Source tags SHALL work in search, collections and counts exactly like asset tags. The Sources page SHALL show each source's source tags and let the user add and remove them.

#### Scenario: Tag the whole pack
- **WHEN** the user adds the source tag "pixel art" to a source with 240 assets
- **THEN** all 240 assets carry "pixel art" with origin "source", and a collection requiring "pixel art" includes them

#### Scenario: New assets inherit
- **WHEN** a source has the source tag "low poly" and a rescan imports 10 new assets into it
- **THEN** the 10 new assets carry "low poly"

### Requirement: Removing source tags
Removing a source tag SHALL remove it from the source's assets where its origin is "source". Copies of the same tag that the asset got from the user, the import or the AI SHALL be kept.

#### Scenario: Remove source tag keeps user copy
- **WHEN** the source tag "medieval" is removed and one asset also had "medieval" added by the user
- **THEN** that asset keeps "medieval", and the others lose it

### Requirement: Per-asset opt-out of a source tag
Removing a source tag from a single asset SHALL affect only that asset. The tag SHALL stay removed from that asset, also after later AI runs, re-imports and source updates.

#### Scenario: Opt-out survives re-import
- **WHEN** the user removes the source tag "pixel art" from one HD splash image and later re-imports the source
- **THEN** that image still does not carry "pixel art", and the other assets do

### Requirement: Favourites, ratings and notes
Each asset SHALL have a favourite flag, a rating from 0 (unrated) to 5 stars, and free-text notes. The user SHALL be able to set them on one asset in its details, and set the favourite flag and the rating on many selected assets at once. These fields belong to the user: AI enrichment, re-import and source updates SHALL never change them. The notes SHALL be included in full-text search.

#### Scenario: Rating survives re-import
- **WHEN** an asset rated 4 stars with the note "use for level 2 rocks" is re-imported with AI enrichment
- **THEN** it still has 4 stars and the same note

#### Scenario: Bulk favourite
- **WHEN** the user selects 20 assets and marks them as favourites
- **THEN** all 20 are favourites
