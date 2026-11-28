## MODIFIED Requirements

### Requirement: Scripts and non-asset content noted
Code and project files SHALL be summarized in the source notes and SHALL NOT be imported as assets. This covers C#, shaders as code, JS, assembly definitions, editor scripts and similar files. The AI SHALL decide whether borderline items are assets. The user SHALL be able to override that decision after the import by deleting an unwanted asset from the catalog.

#### Scenario: Package with scripts
- **WHEN** a Unity package contains a `Scripts/` folder with 40 `.cs` files
- **THEN** no assets are created for them, and the source notes say the package includes 40 scripts and what they appear to do
