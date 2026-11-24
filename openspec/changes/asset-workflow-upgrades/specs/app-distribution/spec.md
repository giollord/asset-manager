## ADDED Requirements

### Requirement: Update check
The app SHALL offer an update check that is off by default. The user SHALL be able to turn it on in Settings → About and to check now. When it is on, the app SHALL ask the project's GitHub Releases for the latest release at most once a day, at startup, and compare that release's version with its own version. The check SHALL send no user data beyond the request itself. It SHALL NOT download or install anything.

#### Scenario: Default off
- **WHEN** the user never turned the update check on
- **THEN** the app makes no request to GitHub

### Requirement: Update notification
When a newer version exists, About SHALL show it, and the app SHALL show one notification per new version, each linking to the release page. A failed check SHALL be silent, apart from a "last check failed" note in About.

#### Scenario: Newer release
- **WHEN** the update check is on, the app is version 1.2.0, and the latest release is v1.3.0
- **THEN** About shows "1.3.0 available" with a link to the release page, and one notification is shown

#### Scenario: Offline
- **WHEN** the update check is on and there is no network
- **THEN** no error is shown, and About says the last check failed

### Requirement: Screenshots in the README
The README SHALL show a few screenshots of the app below its introduction and above "Download": at least the Browse grid, the asset details with a 3D model, and the Dashboard. The screenshots SHALL be PNG files kept in the repository under `docs/screenshots/` and referenced by relative path, each with alt text. They SHALL show the current UI, and SHALL only show assets whose licenses allow redistribution (for example CC0 packs). The logo SHALL stay at the top of the README.

#### Scenario: README on GitHub
- **WHEN** someone opens the repository page on GitHub
- **THEN** the README shows the logo, then the introduction, then the screenshots, all rendered from files in the repository
