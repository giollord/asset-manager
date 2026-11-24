## ADDED Requirements

### Requirement: Sprite sheet detection
An image SHALL be cataloged as a sprite sheet when it holds several frames or sprites on a grid or atlas, and as an image or texture otherwise. The decision SHALL use, in this order:
1. slicing data that comes with the image
2. the grid detector's result
3. the AI's answer, used only when neither of the above decides

A user-set type SHALL never be changed.

#### Scenario: Character run cycle
- **WHEN** a 512×64 PNG with eight equally sized character poses separated by transparent columns is imported
- **THEN** it is cataloged as a sprite sheet with 8 frames of 64×64

### Requirement: AI sprite sheet answers need confirmation
The AI MAY propose that an image is a sprite sheet. The type SHALL become sprite sheet only when the grid detector confirms a grid, or when slicing data exists.

#### Scenario: AI guess rejected
- **WHEN** the AI calls a single seamless tiling texture a sprite sheet and the grid detector finds no grid
- **THEN** the asset stays a texture

### Requirement: Slicing data first
When slicing data exists for an image, it SHALL define the frames exactly. Slicing data is:
- a Unity `.meta` with sprite mode "Multiple" and its sprite rectangles
- a TexturePacker or Aseprite JSON or XML atlas with the same base name next to the image

Frames from slicing data MAY be irregular (atlas rectangles), not only a grid.

#### Scenario: Unity sliced sprite
- **WHEN** a `.unitypackage` contains a PNG whose `.meta` lists 12 named sprite rectangles
- **THEN** the sprite sheet has those 12 frames with their names and rectangles, and the frames' source is recorded as "slicing data"

### Requirement: Algorithmic grid detection
Without slicing data, the grid SHALL be found by analysing the pixels, not by the AI alone. The result SHALL be a grid of:
- cell width and height
- offset and spacing
- columns, rows and the number of non-empty frames

It SHALL come with a confidence. The detector SHALL handle sheets with transparent or solid-colour gutters, and tightly packed sheets without gutters.

#### Scenario: Packed sheet without gutters
- **WHEN** a 256×256 sheet holds a 4×4 grid of 64×64 frames that touch each other
- **THEN** the detector reports cells of 64×64, 4 columns and 4 rows

### Requirement: AI hints and uncertain grids
The AI's frame count or cell size, when given, SHALL only be used as a candidate that the detector scores like any other. Below the confidence threshold, the asset SHALL be marked "grid uncertain" for review, not given a guessed grid.

#### Scenario: Uncertain grid
- **WHEN** a sheet's sprites are of different sizes with no slicing data
- **THEN** no grid is stored and the asset is marked "grid uncertain"

### Requirement: Pixel-art detection
An image SHALL be detected as pixel art from measured properties:
- art scaled up with nearest-neighbour, recorded as a pixel scale of 1, 2, 3 and so on
- hard edges without anti-aliased intermediate colours
- a limited palette

A detected image SHALL get the style "pixel art", and the tag "pixel art" with origin AI, unless the user edited the style. An asset SHALL be able to be both a sprite sheet and pixel art. Previews of pixel art SHALL be scaled without smoothing.

#### Scenario: Pixel-art sprite sheet
- **WHEN** a 1024×256 sheet of 16×16 sprites drawn at 4× scale with a 24-colour palette is imported
- **THEN** it is a sprite sheet with 64×64 cells, pixel scale 4, style "pixel art" and tag "pixel art"

#### Scenario: Large smooth illustration
- **WHEN** a 2048×2048 painted illustration with gradients is imported
- **THEN** it is not marked as pixel art

### Requirement: Grid review and correction
The user SHALL be able to see the detected grid over the sheet and edit it: cell size, offset, spacing, columns, rows and frame count. The user SHALL be able to mark an image as a sprite sheet or not. User edits SHALL NOT be overwritten by re-detection or AI enrichment.

#### Scenario: Fix cell size
- **WHEN** the user changes a detected 32×32 grid to 32×48
- **THEN** the overlay, the frame count, the frame viewer and the exports use 32×48 cells, and re-enrichment keeps 32×48

### Requirement: Frame viewer
The asset details of a sprite sheet SHALL show its frames, and play a row, or a frame range for atlases, as an animation with adjustable speed. This SHALL work from previews stored in the catalog, without the original file.

#### Scenario: Play a row offline
- **WHEN** the drive with the original is detached and the user plays row 2 of a sprite sheet at 12 fps
- **THEN** the row animates from the stored preview

### Requirement: Frame export
The user SHALL be able to export the selected frames of a sprite sheet as separate PNG files, and a row or frame range as an animated WebP. The export SHALL be cut from the original at full resolution.

#### Scenario: Export frames
- **WHEN** the user exports frames 1-8 of an available sprite sheet to a folder
- **THEN** 8 PNG files with the original cell size are written, named with the sheet name and frame number, or the frame name from the slicing data
