# ai-enrichment Specification

## Purpose
TBD - created by archiving change build-asset-manager. Update Purpose after archive.

## Requirements

### Requirement: Embedded local inference
AI inference SHALL run fully locally, using a runtime shipped with the app. It SHALL NOT require Python, a separately installed server, or network access during use. The model SHALL be a small multimodal model of the Gemma 4 E4B class, and the model file SHALL be configurable. If no model is installed, the app SHALL still import and catalog assets without AI enrichment.

#### Scenario: Offline enrichment
- **WHEN** the machine has no network connection and a model is installed
- **THEN** AI enrichment of imported assets works

#### Scenario: No model installed
- **WHEN** no model file is configured
- **THEN** imports complete with technical metadata and previews, and the AI fields are marked "not enriched"

### Requirement: Asset enrichment output
For each asset, the AI SHALL produce structured English output: description, tags, type refinement, named dominant colors, style descriptors, and whether the item is a usable game asset. Inputs are the previews, technical metadata, names and paths, source context and the user's import instructions. Output SHALL be schema-validated; invalid output is retried or marked failed.

#### Scenario: Non-English input
- **WHEN** an asset's file names and bundled readme are in another language
- **THEN** the generated description and tags are in English

#### Scenario: Visual input used
- **WHEN** a 3D model or image is enriched
- **THEN** the AI receives preview image(s) of the asset, and its description reflects the visual content

### Requirement: Audio understanding
For audio assets, the AI SHALL receive the audio clip directly when the installed runtime and model support audio input. Otherwise, it SHALL receive the waveform or spectrogram image and the audio metadata.

#### Scenario: Audio fallback
- **WHEN** the installed runtime cannot accept audio input
- **THEN** sounds are still enriched using the spectrogram image, metadata and names, and the asset records which input mode was used

### Requirement: License and link extraction
During import, the system SHALL find documents near or inside a source and give them to the AI:
- plain text, markdown, HTML, PDF and `.url` files
- names indicating license, readme, credits, terms or links

The AI SHALL extract:
- the license kind
- whether commercial use is allowed
- whether attribution is required
- the attribution text
- related URLs

Each extracted value SHALL reference the document it came from.

#### Scenario: License in PDF
- **WHEN** a source folder contains `license.pdf` stating royalty-free commercial use
- **THEN** the source license is recorded with commercial use "allowed" and references `license.pdf`

#### Scenario: Link shortcut
- **WHEN** a folder contains an `Itch.io.url` shortcut
- **THEN** its URL is stored as a related link of the source

### Requirement: Scripts and non-asset content noted
Code and project files SHALL be summarized in the source notes and SHALL NOT be imported as assets. This covers C#, shaders as code, JS, assembly definitions, editor scripts and similar files. The AI SHALL decide whether borderline items are assets, and the user SHALL be able to override that decision in the import preview.

#### Scenario: Package with scripts
- **WHEN** a Unity package contains a `Scripts/` folder with 40 `.cs` files
- **THEN** no assets are created for them, and the source notes say the package includes 40 scripts and what they appear to do

### Requirement: Model orientation judgment
For 3D models without trustworthy up-axis metadata, the AI SHALL be shown the model rendered under each candidate up-axis. It SHALL choose the upright candidate or answer "no natural up", along with a confidence. A choice that differs from the current orientation SHALL trigger a re-render of the previews. Item enrichment SHALL use the upright renders.

#### Scenario: Correction triggers re-render
- **WHEN** the AI picks a candidate up-axis different from the one used for the existing renders
- **THEN** the model's previews are re-rendered with the corrected orientation before its description is generated

#### Scenario: Low confidence
- **WHEN** the AI's confidence is below the threshold
- **THEN** the orientation is left unchanged and marked "orientation uncertain" for user review

### Requirement: Collection descriptions
The user SHALL be able to have the AI generate or refresh a collection's English description from a sample of its member assets.

#### Scenario: Describe collection
- **WHEN** the user requests an AI description for a collection with 300 assets
- **THEN** a description summarizing the collection's content and style is generated from a representative sample and saved

### Requirement: Text embeddings for semantic search
The system SHALL compute a vector embedding for each asset from its name, description, tags and type. It SHALL store the embedding in the catalog and recompute it whenever those fields change.

#### Scenario: Embedding refresh
- **WHEN** an asset's description is edited
- **THEN** its embedding is recomputed

### Requirement: Background processing and progress
AI work SHALL run in a background queue. The queue SHALL show visible progress, SHALL be cancellable, and SHALL resume after an app restart. AI work SHALL NOT block browsing or search.

#### Scenario: Resume after restart
- **WHEN** the app is closed while 500 assets are pending enrichment
- **THEN** enrichment of the remaining assets continues after the next start

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

### Requirement: Shared prompt prefix reuse
Enrichment requests SHALL begin with text that is the same for every request of the same kind within an import of one source. That text covers the instructions, the suggested tags and the pack context. Item details and previews SHALL come after it. The AI runtime SHALL reuse the already processed shared beginning instead of processing it again for each request.

#### Scenario: Second request of a pack
- **WHEN** a pack of 40 images is enriched in contact-sheet requests
- **THEN** every request after the first one on the same server slot reuses the processed shared beginning, and the log reports those tokens as reused

#### Scenario: Results unchanged in kind
- **WHEN** the same pack is enriched before and after this change
- **THEN** each asset still gets an English description, tags, type, colors and style that pass the same validation

### Requirement: AI request timings
For each enrichment run, the app SHALL log how many requests it made and, summed over them: prompt tokens, prompt tokens reused from cache, prompt processing time (including images), generated tokens and generation time. With debug logging on, it SHALL log these values for each request.

#### Scenario: Import with AI
- **WHEN** an import's AI enrichment finishes
- **THEN** the log contains one line with the request count, prompt and reused tokens, prompt time, generated tokens and generation time for that run

### Requirement: AI models load when needed
The AI models SHALL NOT load at startup unless "Load AI models at startup" is on. The chat model SHALL load when an AI step, a collection description or a search by image first needs it; the embedding model SHALL load when a search by meaning, "find similar" or an asset without an embedding first needs it. When both load together, the embedding model SHALL load first.

#### Scenario: Session without AI work
- **WHEN** the app is started with both models configured and the user only browses the catalog
- **THEN** no AI model is loaded and the AI status says the models load when needed

#### Scenario: Search by meaning
- **WHEN** the user turns on search by meaning while no model is loaded
- **THEN** the embedding model loads, the results appear, and the chat model stays unloaded

#### Scenario: AI import step
- **WHEN** an import reaches its AI step while the chat model is not loaded
- **THEN** the job shows that it is loading the AI model, the model loads, and the AI step runs

### Requirement: AI model load progress and failures
Each model load SHALL show in the jobs panel as "Loading AI models" with the model being loaded. A model that fails to load SHALL be reported with the reason and SHALL NOT be tried again until its settings change or the user restarts the AI.

#### Scenario: Loading is visible
- **WHEN** a model starts loading
- **THEN** the jobs panel shows "Loading AI models" with the model's file name until it is ready

#### Scenario: Model load failure
- **WHEN** the configured chat model file is missing and an import reaches its AI step
- **THEN** the jobs panel shows the load as failed with the reason, the assets are marked not enriched, and the next AI step does not try to load the same file again
