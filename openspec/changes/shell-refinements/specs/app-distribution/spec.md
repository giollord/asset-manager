## MODIFIED Requirements

### Requirement: Versioned releases
Pushing a version tag of the form `v<major>.<minor>.<patch>` SHALL build the portable package with that version and publish it as a GitHub Release named after the tag, with the zip attached. The release SHALL be published only after the tests pass. The app SHALL report that version. Every build SHALL take its version from the git tags: a build of a tagged commit SHALL report the tag's version, and a build of a later commit SHALL report a pre-release version above the latest tag.

#### Scenario: Tagged release
- **WHEN** the tag `v1.2.0` is pushed and the build and tests pass
- **THEN** a GitHub Release `v1.2.0` exists with the zip `AssetManager-1.2.0-win-x64-portable.zip`, and the app built from it reports version 1.2.0

#### Scenario: Failed tagged build
- **WHEN** a version tag is pushed and a test fails
- **THEN** no release is published

#### Scenario: Build after a tag
- **WHEN** the app is built from a commit 5 commits after the tag `v0.1.0`
- **THEN** Settings → About shows a pre-release version of 0.1.1, such as `0.1.1-alpha.0.5`, not the project's default version

#### Scenario: Version mismatch fails the release
- **WHEN** a tagged build's app reports a version other than the tag's
- **THEN** the workflow fails and no release is published
