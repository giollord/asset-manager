## ADDED Requirements

### Requirement: Combined filters
Search SHALL combine the following filters with AND semantics:
- asset type(s)
- free text
- included tags (all must match)
- excluded tags (none may match)
- collection
- source
- license flags (commercial use allowed, attribution required)
- availability (original available or stored)
- model issues (scale anomaly, orientation uncertain or not verified, textures missing)

#### Scenario: Include and exclude tags
- **WHEN** the user searches type "3D model", includes tag "tree" and excludes tag "winter"
- **THEN** only 3D model assets tagged "tree" and not tagged "winter" are returned

#### Scenario: Commercial-safe filter
- **WHEN** the user enables "commercial use allowed"
- **THEN** only assets whose effective license allows commercial use are returned

### Requirement: Full-text search
Text search SHALL match asset names, file names, original paths, descriptions, tags and source names. It SHALL support word-prefix matching and rank results by relevance.

#### Scenario: Prefix match
- **WHEN** the user types "barr"
- **THEN** assets named or described with "barrel" are returned

### Requirement: Semantic search
The user SHALL be able to switch the search to a semantic mode, which ranks assets by vector similarity between the query and the asset embeddings. The other filters SHALL still apply. The user SHALL also be able to run "find similar" from any asset.

#### Scenario: Meaning-based query
- **WHEN** the user searches semantically for "spooky forest ambience"
- **THEN** audio assets described as eerie woodland or night sounds rank highly even without those exact words

#### Scenario: Find similar
- **WHEN** the user chooses "find similar" on a low-poly rock model
- **THEN** other assets with the most similar embeddings are listed

### Requirement: Responsive results
Search results SHALL be shown as a virtualized grid of previews with paging or infinite scroll. For a catalog of 200,000 assets, the first page SHALL appear within 1 second for non-semantic queries on typical hardware.

#### Scenario: Large catalog
- **WHEN** the catalog holds 200,000 assets and the user filters by a tag
- **THEN** the first page of results is shown within 1 second

### Requirement: Inline audio and model inspection
From the results and from the asset details, the user SHALL be able to play audio previews. For 3D models, the user SHALL be able to cycle through the multi-angle previews.

#### Scenario: Play sound in results
- **WHEN** the user clicks play on a sound asset in the grid
- **THEN** its stored preview clip plays without needing the original file
