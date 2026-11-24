## MODIFIED Requirements

### Requirement: Combined filters
Search SHALL combine the following filters with AND semantics:
- asset type(s)
- free text
- included tags (all must match)
- excluded tags (none may match)
- collection
- source
- license flags (commercial use allowed, attribution required)
- availability (original available or stored)
- model issues (scale anomaly, orientation uncertain or not verified, textures missing)
- favourites only
- minimum rating
- has notes
- sprite sheet grid uncertain

#### Scenario: Include and exclude tags
- **WHEN** the user searches type "3D model", includes tag "tree" and excludes tag "winter"
- **THEN** only 3D model assets tagged "tree" and not tagged "winter" are returned

#### Scenario: Commercial-safe filter
- **WHEN** the user enables "commercial use allowed"
- **THEN** only assets whose effective license allows commercial use are returned

#### Scenario: Favourites with a minimum rating
- **WHEN** the user turns on "favourites only" and sets the minimum rating to 4
- **THEN** only favourite assets rated 4 or 5 stars are returned

## ADDED Requirements

### Requirement: Search by image
The user SHALL be able to search with an image instead of text, by any of these:
- dropping an image file on Browse
- pasting an image from the clipboard
- choosing a file

The search SHALL rank assets by how similar they are to the image in content and look. The other filters SHALL still apply. The query image SHALL be shown as a removable chip, and SHALL NOT be added to the catalog. When no AI model is installed, image search SHALL be unavailable, with a message saying why.

#### Scenario: Find assets like a screenshot
- **WHEN** the user drops a screenshot of a green low-poly tree on Browse with the type filter "3D model"
- **THEN** 3D models of low-poly trees with similar colours rank highest

#### Scenario: Image not kept
- **WHEN** the user removes the image chip
- **THEN** the search returns to the previous query, and the catalog has no new asset or file from the query image
