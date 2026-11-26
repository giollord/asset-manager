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

### Requirement: AI runtime start order
The AI runtime SHALL load the embedding model before the chat model, so semantic search becomes available first. It SHALL load models with sequential reads that suit slow drives. A model that fails to load SHALL be reported as before.

#### Scenario: Both models configured
- **WHEN** the app starts with a chat model and an embedding model configured
- **THEN** the embedding server becomes ready first, then the chat server, and the AI status lists both once ready

#### Scenario: Model load failure
- **WHEN** the configured chat model file is missing
- **THEN** the window still opens, and the AI status reports that the chat model could not be loaded
