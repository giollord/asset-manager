## MODIFIED Requirements

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
- favourites only
- minimum rating
- has notes
- sprite sheet grid uncertain

#### Scenario: Include and exclude tags
- **WHEN** the user searches type "3D model", includes tag "tree" and excludes tag "winter"
- **THEN** only 3D model assets tagged "tree" and not tagged "winter" are returned

#### Scenario: Commercial-safe filter
- **WHEN** the user enables "commercial use allowed"
- **THEN** only assets whose effective license allows commercial use are returned

#### Scenario: Favourites with a minimum rating
- **WHEN** the user turns on "favourites only" and sets the minimum rating to 4
- **THEN** only favourite assets rated 4 or 5 stars are returned

### Requirement: Semantic search
The user SHALL be able to switch the search to a semantic mode, which ranks assets by vector similarity between the query and the asset embeddings. The other filters SHALL still apply. The user SHALL also be able to run "find similar" from any asset: through the asset card's context menu in the grid (see app-shell "Asset card context menu").

#### Scenario: Meaning-based query
- **WHEN** the user searches semantically for "spooky forest ambience"
- **THEN** audio assets described as eerie woodland or night sounds rank highly even without those exact words

#### Scenario: Find similar
- **WHEN** the user chooses "find similar" on a low-poly rock model
- **THEN** other assets with the most similar embeddings are listed

### Requirement: Inline audio and model inspection
From the results and from the asset details, the user SHALL be able to play audio previews. For 3D models, the user SHALL be able to inspect the stored multi-angle renders as a turntable, without the original file:
- dragging horizontally SHALL step through the azimuths of the current elevation ring, wrapping around, so that the model appears to turn with the pointer
- dragging vertically SHALL step between elevation rings, stopping at the highest and lowest ring
- the turntable SHALL start at the primary view, and the left/right and up/down arrow keys SHALL step it while it has focus
- all renders SHALL be loaded before the first drag, so that steps show no loading gaps

The turntable SHALL replace the single image with a strip of thumbnails in the asset details. In the grid, the renders SHALL keep cycling while the pointer rests on a model card.

#### Scenario: Play sound in results
- **WHEN** the user clicks play on a sound asset in the grid
- **THEN** its stored preview clip plays without needing the original file

#### Scenario: Turn a model offline
- **WHEN** the drive of a model with the default 3 × 8 renders is detached and the user drags left to right across its preview in the details panel
- **THEN** the preview steps through the azimuth renders of the current ring in turning order, and a full drag across the preview turns the model about once around

#### Scenario: Tilt
- **WHEN** the user drags upward on the turntable while it shows the middle ring
- **THEN** it shows the render from the next higher ring at the same azimuth, and dragging further up stays at the highest ring

#### Scenario: Renders from older settings
- **WHEN** a model's stored renders were made with a different number of rings or azimuths than the current settings
- **THEN** the turntable still shows them, in one ring in stored order

## ADDED Requirements

### Requirement: Search by image
The user SHALL be able to search with an image instead of text, by any of these:
- dropping an image file on Browse
- pasting an image from the clipboard
- choosing a file

The search SHALL rank assets by how similar they are to the image in content and look. The other filters SHALL still apply. The query image SHALL be shown as a removable chip, and SHALL NOT be added to the catalog. When no AI model is installed, image search SHALL be unavailable, with a message saying why.

#### Scenario: Find assets like a screenshot
- **WHEN** the user drops a screenshot of a green low-poly tree on Browse with the type filter "3D model"
- **THEN** 3D models of low-poly trees with similar colours rank highest

#### Scenario: Image not kept
- **WHEN** the user removes the image chip
- **THEN** the search returns to the previous query, and the catalog has no new asset or file from the query image
