# asset-previews Specification

## Purpose
TBD - created by archiving change build-asset-manager. Update Purpose after archive.

## Requirements

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

### Requirement: Materials that need texture matching
Texture matching SHALL work per material. A material SHALL need matching when both hold:
- it has no base-colour texture after the existing resolution (embedded textures, Unity `.mat` references, material paths, same file name), because the referenced file was not found or nothing is referenced
- a mesh using it has texture coordinates

Models whose textures the user edited SHALL NOT be matched.

#### Scenario: Model without texture coordinates
- **WHEN** a model's meshes have no texture coordinates
- **THEN** no texture matching is done for it

### Requirement: Texture candidates
For a material that needs matching, the candidates SHALL be the textures of the same source: standalone image and texture assets, and textures attached to other models of the source. They SHALL be ranked by:
- name similarity after removing common prefixes (`T_`, `TX_`, `SM_`, `SK_`, `M_`, `MI_`, `MAT_`) and map suffixes (albedo, diffuse, normal, roughness and similar)
- name similarity to the material and the model
- folder proximity

Non-colour maps (normal, roughness, metallic, occlusion and similar) SHALL NOT be base-colour candidates. They are recognised by name suffix, and normal maps also by colour.

When the material references no missing file, only candidates with name or folder evidence SHALL be considered, so untextured models that use material colours are left alone.

#### Scenario: Untextured low-poly pack
- **WHEN** a pack's models use material colours, reference no textures, and the pack's images are renders named after other models
- **THEN** no texture matching or AI request is made for them

#### Scenario: Normal map is not a colour candidate
- **WHEN** a source has `T_Rock_Normal.png` (blue-violet) and `T_Rock_Albedo.png` next to `SM_Rock.fbx`
- **THEN** only `T_Rock_Albedo.png` is a base-colour candidate

### Requirement: Texture name match
When the stripped names are equal and only one base-colour candidate has that name, the texture SHALL be attached to the material without the AI, with origin "name match". Equal names means the material, the model or the missing reference `Orc` (`Orc.jpg`) and the texture `T_Orc_Albedo` (or `Orc.png` in another folder). A chosen base-colour texture SHALL bring along the source's normal and emissive maps with the same stripped name, in their slots. The previews SHALL be rendered with them.

#### Scenario: Reference with another extension and folder
- **WHEN** a model's material references `C:\Users\artist\Desktop\Color 2.jpg`, and the pack ships `Textures/Color 2.png`
- **THEN** `Color 2.png` is attached to that material with origin "name match"

#### Scenario: Prefixed names
- **WHEN** a source contains `Meshes/SK_Orc.fbx`, with a material `Orc` that references no texture, and `Textures/T_Orc_Albedo.png` and `Textures/T_Orc_Normal.png`
- **THEN** both are attached to the model, as base colour and normal map with origin "name match", and the previews show the textured model

### Requirement: Attaching AI texture matches
An AI texture choice (see the AI enrichment capability) at or above the texture-match threshold SHALL be attached with origin "AI". The default threshold is 0.7, configurable in Settings → AI. The "textures missing" flag SHALL be cleared once every missing base colour is filled. The previews SHALL be re-rendered before the model's item pass. An AI-matched texture SHALL also stay its own asset.

#### Scenario: Two materials
- **WHEN** `FBX/Tree_3.fbx` has the materials `Tree_Leaves` and `Bark` without references, and the pack ships `Textures/Tree_Leaves.png`, `Tree_Bark.jpg`, `Birch_Bark.png` and `Color Variations/Bark_Dead.png`
- **THEN** `Tree_Leaves.png` is attached to `Tree_Leaves` by name match, the AI picks `Tree_Bark.jpg` for `Bark`, and the previews show both

#### Scenario: Atlas shared by a pack
- **WHEN** a low-poly pack has 40 models whose material `Palette` references the missing `palette.png`, and the pack ships `Textures/PolygonFarm_Texture_01.png`
- **THEN** the AI picks that texture with high confidence, it is attached with origin "AI", and the models' renders are coloured

### Requirement: Texture suggestions
An AI choice below the texture-match threshold, but at or above the suggestion threshold (default 0.4), SHALL be stored as a suggestion. A suggestion SHALL NOT change the previews.

#### Scenario: Unsure choice
- **WHEN** the AI picks a candidate with confidence 0.5
- **THEN** nothing is attached, the previews stay untextured, and the details drawer offers the candidate as a suggestion

### Requirement: Pack texture suggestions
After a source's AI item pass, with no AI request, a texture SHALL be a pack texture for a material name when at least 3 of the source's models use it as that material's base colour, and at least half of the models with that material textured do. Each model of the source with that material still untextured (and with texture coordinates) SHALL get the pack texture as a suggestion, with the share of models as its confidence. Models with user-edited textures SHALL get none.

#### Scenario: Pack texture suggestion
- **GIVEN** a farm pack where `Barn`, `Silo` and `Windmill` use `Textures/Color 2.png` for material `Material`
- **WHEN** the AI item pass of the source finishes and `Tractor` and `Well` still have `Material` untextured
- **THEN** both get `Color 2.png` as a suggestion ("used for "Material" by 3 of 3 models in this pack"), and the filter "texture suggestions" shows them

#### Scenario: No clear pack texture
- **WHEN** only 2 models use a texture for a material name
- **THEN** no pack suggestion is made

### Requirement: Applying texture suggestions in bulk
Browse SHALL offer the model-issue filter "texture suggestions". A bulk action "Apply texture suggestions" SHALL attach every suggestion of the selected models as a user choice, and re-render those models in a job.

#### Scenario: Apply in bulk
- **WHEN** the user selects the suggested models and runs "Apply texture suggestions"
- **THEN** the textures are attached with origin "user", the models' textures are marked as user-edited, and they are re-rendered

### Requirement: Reviewing a model's textures
A model's details drawer SHALL list its textures, each with:
- material and slot
- file name and a thumbnail
- how it was found: file reference, name match, AI or user

The user SHALL be able to detach a texture, apply or dismiss a suggestion, and choose a texture from the source's textures for a material and slot. Each change SHALL re-render the previews. Browse SHALL offer the model-issue filter "textures matched by AI".

#### Scenario: Choose a texture
- **WHEN** the user picks `Textures/Barrel_Color.png` for material `Wood` of a barrel model
- **THEN** it is attached with origin "user" and the previews are re-rendered with it

### Requirement: User texture edits are kept
Any texture change made by the user SHALL mark the model's textures as user-edited. Texture matching and re-enrichment SHALL NOT change them afterwards.

#### Scenario: Wrong AI match
- **WHEN** the user detaches a texture the AI matched to a model
- **THEN** the previews are re-rendered without it, and re-enriching the model does not attach it again
