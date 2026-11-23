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
