## ADDED Requirements

### Requirement: Open-source license
The repository SHALL be licensed under the MIT License. The `LICENSE` file at the repository root SHALL contain the standard MIT text exactly as published by an authoritative source (GitHub's license API or the SPDX license list), changed only by filling in the copyright year and the author's name. The README SHALL state the license. The licenses of bundled third-party components SHALL remain listed separately.

#### Scenario: License recognised
- **WHEN** the repository page is opened on GitHub
- **THEN** GitHub detects and shows the MIT License

#### Scenario: Text unchanged
- **WHEN** `LICENSE` is compared with the MIT text from the authoritative source
- **THEN** the only differences are the year and the copyright holder

## MODIFIED Requirements

### Requirement: Portable Windows package
For a version tag, the workflow SHALL produce a portable package for Windows x64: one zip that, once extracted to any folder, runs `AssetManager.exe` without installing .NET, the Windows App SDK or anything else beyond the WebView2 runtime included in Windows 10 and 11. The package SHALL include the app's own MIT license as `LICENSE.txt`, and the AI runtime, FFmpeg and other native components with their license files. It SHALL NOT include AI model files. The package check SHALL fail when the app's license file is missing.

#### Scenario: Run on a clean machine
- **WHEN** the zip is extracted on a Windows 10 or 11 x64 machine without the .NET SDK or runtime and `AssetManager.exe` is started
- **THEN** the app opens, creates an empty catalog and can import a folder of images

#### Scenario: No models in the package
- **WHEN** the package contents are listed
- **THEN** it contains `LICENSE.txt`, and the `llama-server` builds and FFmpeg with their licenses, and no `.gguf` model file
