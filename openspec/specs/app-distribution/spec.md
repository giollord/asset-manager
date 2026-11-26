# app-distribution Specification

## Purpose
Defines how the app is built, tested and packaged for users: automated builds on GitHub Actions and a portable Windows package that runs without installing anything.

## Requirements

### Requirement: Automated build and tests
Every pull request to `master` SHALL run the automated tests on Windows in GitHub Actions, and SHALL NOT build the portable package. Pushes to `master` SHALL NOT start a run; the tests already ran on the pull request. The run SHALL fail when the code does not compile or any test fails. Tests that need locally installed AI models SHALL be skipped, not failed, in that environment.

#### Scenario: Broken change
- **WHEN** a pull request makes a test fail
- **THEN** the workflow run for that pull request fails and names the failing test

#### Scenario: Passing change
- **WHEN** a pull request compiles and every test passes or is skipped
- **THEN** the workflow run succeeds without producing a package

#### Scenario: Merge to master
- **WHEN** a pull request is merged, or a commit is pushed to `master` directly
- **THEN** no workflow run starts

### Requirement: Portable Windows package
For a version tag, the workflow SHALL produce a portable package for Windows x64: one zip that, once extracted to any folder, runs `AssetManager.exe` without installing .NET, the Windows App SDK or anything else beyond the WebView2 runtime included in Windows 10 and 11. The package SHALL include the app's own MIT license as `LICENSE.txt`, and the AI runtime, FFmpeg and other native components with their license files. It SHALL NOT include AI model files. The package check SHALL fail when the app's license file is missing.

#### Scenario: Run on a clean machine
- **WHEN** the zip is extracted on a Windows 10 or 11 x64 machine without the .NET SDK or runtime and `AssetManager.exe` is started
- **THEN** the app opens, creates an empty catalog and can import a folder of images

#### Scenario: No models in the package
- **WHEN** the package contents are listed
- **THEN** it contains `LICENSE.txt`, and the `llama-server` builds and FFmpeg with their licenses, and no `.gguf` model file

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

### Requirement: Open-source license
The repository SHALL be licensed under the MIT License. The `LICENSE` file at the repository root SHALL contain the standard MIT text exactly as published by an authoritative source (GitHub's license API or the SPDX license list), changed only by filling in the copyright year and the author's name. The README SHALL state the license. The licenses of bundled third-party components SHALL remain listed separately.

#### Scenario: License recognised
- **WHEN** the repository page is opened on GitHub
- **THEN** GitHub detects and shows the MIT License

#### Scenario: Text unchanged
- **WHEN** `LICENSE` is compared with the MIT text from the authoritative source
- **THEN** the only differences are the year and the copyright holder

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

### Requirement: Compact package layout
The portable package SHALL contain one `AssetManager.exe` holding the app, the .NET runtime and the Windows UI runtime, with the app's web content as one archive beside it. Beside the exe SHALL be only `portable`, `LICENSE.txt`, the `llama/`, `ffmpeg/` and `licenses/` (third-party license texts) folders, and at most 5 other files. It SHALL NOT contain locale folders, debug symbols or packaged-app tile images. The package check SHALL fail otherwise.

#### Scenario: Top level of the extracted zip
- **WHEN** the zip of a tagged release is extracted and its top level is listed
- **THEN** it shows `AssetManager.exe`, `portable`, `LICENSE.txt`, `llama`, `ffmpeg`, `licenses` and no more than 5 other files, and no other folders

#### Scenario: Extra folder fails the check
- **WHEN** a build change makes the publish output contain a locale folder such as `de-DE`
- **THEN** the package check fails and names the unexpected entry

#### Scenario: Compact package still runs
- **WHEN** the compact package is extracted on a clean Windows 10 or 11 x64 machine and `AssetManager.exe` is started
- **THEN** the app opens with its full UI (styles, icons, 3D viewer) and can import a folder of images, as before

### Requirement: Window shown before AI models load
When the app is started, it SHALL show its main window and its first page's data before any AI model loads. Loading models SHALL NOT delay the window. When "Load AI models at startup" is on, the models SHALL load after the first page shows its data, and the AI status SHALL show that they are starting until they are ready.

#### Scenario: Start from an external drive
- **WHEN** the portable folder, including its models, is on a slow external drive, "Load AI models at startup" is on, and the app is started with the OS file cache cold
- **THEN** the main window and the Dashboard's numbers appear before the models start loading, and the AI status shows "starting" until they are ready

#### Scenario: Pending AI work at start
- **WHEN** the app is started while enrichment jobs are pending
- **THEN** the window appears first, and the jobs load the chat model when they reach an AI step and then continue
