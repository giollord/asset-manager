# Spec Delta

## ADDED Requirements

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
