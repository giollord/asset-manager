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
