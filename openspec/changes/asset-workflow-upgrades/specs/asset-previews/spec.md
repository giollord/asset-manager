## ADDED Requirements

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
