## ADDED Requirements

### Requirement: Source tag consensus candidates
After the AI has enriched a source's assets, the system SHALL look for tags and styles that many of them share. A tag or style is a candidate when both hold:
- it is on at least the consensus share of the source's game assets (default 60%)
- the source has at least the minimum number of assets (default 5)

Tags that are already source tags SHALL NOT be candidates. The thresholds SHALL be configurable in Settings → AI, and the step SHALL be able to be turned off.

#### Scenario: Below threshold
- **WHEN** a tag is on 30% of a source's assets
- **THEN** it is not sent to the AI as a candidate

### Requirement: Source tag consensus decision
The AI SHALL receive:
- the source's name, description and per-type asset counts
- each candidate with its count and share, overall and per type
- sample previews of assets that have the candidate and of assets that lack it

For each candidate, it SHALL answer whether the candidate applies to the whole source, with a short reason. Accepted candidates SHALL become source tags with origin AI. Rejected ones SHALL change nothing. The AI SHALL NOT remove tags.

#### Scenario: Pixel-art pack
- **WHEN** 190 of 240 images in a source have the style "pixel art", and the other 50 are small UI icons drawn in the same style
- **THEN** the AI is asked about "pixel art", accepts it, and all 240 assets carry "pixel art" as a source tag

#### Scenario: Mixed pack
- **WHEN** 65% of a source's assets are tagged "low poly", but the rest are realistic HDR skyboxes
- **THEN** the AI rejects "low poly" for the whole source and no source tag is added

### Requirement: When consensus runs
The consensus step SHALL run:
- after the item pass of an import
- after re-enriching a source, or a selection that covers a whole source
- when the user asks for it on a source from the Sources page

#### Scenario: Manual run
- **WHEN** the user chooses "suggest source tags" on a source
- **THEN** the consensus step runs for that source as a background AI job

### Requirement: Suggested tag vocabulary
The catalog SHALL hold a list of suggested tags, which the AI is shown whenever it chooses tags for items and packs. A new catalog SHALL start with a built-in list of generic tags covering dimension, style, view, kind and technical properties. The AI SHALL use a suggested tag whenever it applies, instead of a synonym, and MAY add other tags.

#### Scenario: Built-in list
- **WHEN** a new catalog is created
- **THEN** its suggested tags are:
  - dimension: 2d, 3d
  - style: pixel art, low poly, hand-painted, realistic, stylized, cartoon
  - view: top-down, isometric, side-view
  - kind: ui, icon, tileset, character, environment, prop, vfx
  - technical: seamless, pbr, animated, rigged

#### Scenario: Synonym avoided
- **WHEN** an item clearly is low-poly art and the AI would describe it as "lowpoly" or "low-poly style"
- **THEN** the item gets the suggested tag "low poly"

### Requirement: Editing suggested tags
The user SHALL be able to view, add and remove suggested tags in Settings → AI, and reset the list to the built-in one. Suggested tags SHALL be normalized like other tags. Changes SHALL apply to later AI requests and SHALL NOT change existing assets.

#### Scenario: Add a suggested tag
- **WHEN** the user adds "voxel" to the suggested tags and imports a voxel model pack
- **THEN** the AI requests for that import include "voxel", and the voxel items are tagged "voxel"

### Requirement: Sprite sheet hint in item enrichment
For images, the item enrichment output SHALL also say whether the item looks like a sprite sheet, and if so the estimated number of columns and rows or frames. The output SHALL also say whether it looks like pixel art. These answers SHALL be passed to sprite-sheet and pixel-art detection as hints. They SHALL NOT by themselves set the type, the grid or the pixel-art flag.

#### Scenario: Hint checked by the detector
- **WHEN** the AI says an image is a sprite sheet with 6 frames, and the grid detector finds a 6×1 grid with high confidence
- **THEN** the asset becomes a sprite sheet with that grid

### Requirement: Choosing a model's texture
For each material that needs texture matching and has no name match, the system SHALL render the model from one view: once as it is, and once with each of the material's best candidates (up to 4) as its base colour. The AI SHALL see:
- the numbered renders
- the model's name and the material's name
- each candidate's file name and folder

It SHALL answer the candidate that makes the model look as intended (0 for none), a confidence from 0 to 1 and a short reason. The step SHALL run in the item pass, before the model's description, and SHALL be able to be turned off in Settings → AI.

#### Scenario: None fits
- **WHEN** every candidate render shows the texture smeared or mismatched on the model
- **THEN** the AI answers 0, nothing is attached or suggested, and the model stays as it was

### Requirement: Texture choice consistency
The candidates SHALL be asked a second time in another order, and a different answer SHALL count as unsure (confidence 0). Without image input, the best-ranked candidate SHALL only become a suggestion.

#### Scenario: Order changes the answer
- **WHEN** the AI picks candidate 2 and then, with the order reversed, a different texture
- **THEN** the choice counts as unsure and nothing is attached
