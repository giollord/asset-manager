## ADDED Requirements

### Requirement: Previews stored in catalog
Previews SHALL be generated during import and stored inside the catalog file. They SHALL be displayable without access to the original files.

#### Scenario: Preview without original
- **WHEN** the original drive is detached
- **THEN** all previews of imported assets are still displayed

### Requirement: Configurable preview parameters
Every size, threshold, quality, viewpoint and clip-length value used for preview generation SHALL come from the user-editable preview settings. The defaults stated in this spec SHALL be those settings' default values.

#### Scenario: Custom audio clip length
- **WHEN** the user sets the audio clip maximum length to 5 s and imports a 30-second sound
- **THEN** the stored preview clip is at most 5 seconds long

### Requirement: Image and texture previews
For image-like assets, the system SHALL store a downscaled preview with a configurable maximum edge, which defaults to 512 px. The preview SHALL be encoded in a lossy format that preserves transparency. If the original is already within the size limit and below a small-file threshold, the system SHALL store it unchanged, or losslessly re-encoded if its format cannot be displayed. Pixel-art images SHALL be scaled with nearest-neighbour sampling.

#### Scenario: Large texture
- **WHEN** a 4096×4096 PNG texture with alpha is imported
- **THEN** a preview with a maximum edge of 512 px, lossy-compressed and with its alpha channel preserved, is stored

#### Scenario: Small sprite
- **WHEN** a 32×32 PNG sprite is imported
- **THEN** the original image is stored as the preview without lossy re-encoding

#### Scenario: Non-web format
- **WHEN** a PSD, TGA, EXR or DDS image is imported
- **THEN** a displayable preview is generated from it

### Requirement: 3D model previews
For 3D model assets, the system SHALL render the textured model on a transparent background from multiple viewpoints. The viewpoints SHALL be arranged as several horizontal rings at different elevations around the model, with several evenly spaced azimuths per ring. The defaults are 3 elevations × 8 azimuths. One viewpoint SHALL be marked as the primary thumbnail. The renders SHALL use lossy compression that preserves transparency.

#### Scenario: Multi-angle render
- **WHEN** an FBX model with textures is imported with default settings
- **THEN** 24 renders are stored (3 elevation rings × 8 azimuths), each with a transparent background and textures applied

#### Scenario: Missing textures
- **WHEN** a model's referenced textures cannot be found
- **THEN** the model is rendered with a neutral default material and the asset is flagged "textures missing"

#### Scenario: Unrenderable model
- **WHEN** a model format cannot be loaded, for example `.blend` without an available converter
- **THEN** the asset is still cataloged, the Unity-embedded preview or a type placeholder is used, and the failure reason is recorded

### Requirement: Animation clips
A model file whose content is a motion clip SHALL be cataloged as an animation, not a 3D model. A file SHALL count as a clip when it has a skeleton and no mesh, or when it has exactly one real clip (at least 0.2 s long) and says so through a Unity `model@clip` name, a Mixamo take, or a folder whose name contains "anim". Characters with several clips SHALL stay 3D models. The user's type edits SHALL always win. For an animation, the system SHALL store one animated preview: the clip played from a single three-quarter viewpoint at 4 frames per second, at most 10 seconds long, at 256 px, as a looping lossy WebP that keeps transparency. The middle frame SHALL be the thumbnail. A clip without a mesh SHALL be drawn as a stick figure of its skeleton. The frame rate, length, size and quality SHALL be preview settings.

#### Scenario: Mixamo download with a stand-in character
- **WHEN** a Mixamo FBX with one "mixamo.com" take and a skinned character is imported from an "Animations" folder
- **THEN** the asset type is animation, and an animated WebP of the motion from one angle is stored instead of the 24 model views

#### Scenario: Rigged character with several clips
- **WHEN** a character FBX with idle, run and jump clips is imported
- **THEN** it stays a 3D model with multi-angle renders

#### Scenario: Clip in the details
- **WHEN** the user opens an animation's details and switches to the 3D view
- **THEN** the clip plays in a loop in the interactive viewer

### Requirement: Model scale anomalies
For every 3D model, the system SHALL compute its real-world size from the file's unit scale and node transforms. It SHALL flag a scale anomaly when any of these holds:
- the largest dimension is implausibly small (default under 1 cm)
- the largest dimension is implausibly large (default over 1 km)
- the unit scale factor or root scale is not 1
- the scale is non-uniform

Previews SHALL be framed by the model's bounds, so they look the same regardless of scale.

#### Scenario: Model exported 100× too large
- **WHEN** an FBX with a root scale of 100 and a resulting height of 180 m is imported
- **THEN** the asset's metadata records its size, the unit scale and root scale, and a "scale anomaly: too large" flag, and its renders are framed normally

#### Scenario: Normal scale
- **WHEN** a 1.8 m character model with a unit scale of 1 is imported
- **THEN** no scale anomaly is flagged

### Requirement: Model orientation correction
The system SHALL determine each model's upright orientation, using the file's up-axis metadata first and the AI's judgment from candidate views second. When a model lies on its side or upside down, it SHALL apply an axis-aligned rotation to the previews and the in-app viewer. It SHALL record the rotation and how it was decided (metadata, AI or user), and never modify the original file.

#### Scenario: Model lying on its side
- **WHEN** a tree model is imported whose trunk lies along the horizontal axis and the file has no usable up-axis metadata
- **THEN** the AI picks the upright candidate, the renders show the tree standing upright, and the metadata records "orientation corrected: rotated 90° about X (AI)"

#### Scenario: No natural up direction
- **WHEN** the AI judges that a model has no meaningful up direction, such as a flat floor tile or an abstract shape
- **THEN** no rotation is applied, and orientation is recorded as "as authored"

#### Scenario: User override
- **WHEN** the user rotates a model's orientation in the asset details and confirms
- **THEN** its previews are re-rendered with the user's rotation, and the orientation is recorded as decided by the user and is not changed by later AI runs

#### Scenario: Original untouched on export
- **WHEN** a model with an applied orientation correction is copied or extracted
- **THEN** the exported file is byte-identical to the original

#### Scenario: AI unavailable
- **WHEN** no AI model is installed
- **THEN** orientation uses only the file's up-axis metadata, and the asset is marked "orientation not verified"

### Requirement: Audio previews
For audio assets, the system SHALL store a lossy-compressed preview clip of at most 10 seconds, taken from the start of the audio, with a low bitrate. The system SHALL also store a waveform image. Audio that is shorter than 10 seconds SHALL be stored in full.

#### Scenario: Long music track
- **WHEN** a 3-minute WAV music track is imported
- **THEN** a preview of at most 10 seconds is stored in a lossy format and can be played in the app

#### Scenario: Short sound effect
- **WHEN** a 1.2-second sound effect is imported
- **THEN** the full 1.2 seconds are stored as the lossy preview

### Requirement: Preview regeneration
The user SHALL be able to regenerate previews for selected assets when their originals are available.

#### Scenario: Regenerate after settings change
- **WHEN** the user changes the 3D render settings and regenerates previews for selected models
- **THEN** the stored renders are replaced with ones using the new settings
