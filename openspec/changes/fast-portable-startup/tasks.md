# Tasks

## 1. Baseline measurements

- [ ] 1.1 Write `tools/measure-startup.ps1`: it publishes the portable build to a given folder, counts files/folders/top-level entries, and reads from the app log the time from "started" to "Vector index" and each "AI server … ready in N s". Verify by running it against `E:\AssetManager` and recording the numbers (825 files / 213 s chat load) in a "Baseline" note in design.md.
- [ ] 1.2 Measure cold-cache chat model load from E: with the current mmap default vs `--no-mmap` (and `--direct-io` if b12141 has it), by starting `llama-server` by hand after clearing the standby list. Verify by recording the three timings in design.md D7 and choosing the default.

## 2. Single-file spike (design D6)

- [ ] 2.1 In the `Portable=true` property group add `PublishSingleFile`, `IncludeNativeLibrariesForSelfExtract`, `SatelliteResourceLanguages=en`, `DebugType=none`. Publish, list what remains beside the exe, and start the exe. Verify the window opens with styles and the Browse page renders, and note every loose file the runtime still needs.
- [ ] 2.2 Test the spike build in Windows Sandbox (no .NET, no WinAppSDK installed): start it, import a folder of images, open a 3D model's details. Verify all three work. If more than 5 loose files are required besides `portable`/`LICENSE.txt`/`llama`/`ffmpeg`, switch to the folder fallback (design D1-a) and record the decision in design.md.

## 3. Compact package (design D1–D3)

- [ ] 3.1 Embed the published `wwwroot` (including `_content/*` and `_framework/blazor.webview.js`, excluding `.br`/`.gz`) as resources of `AssetManager.exe` in portable publish. Add a `BlazorWebView` subclass whose `CreateFileProvider` serves them, used by `MainPage.xaml`, with the physical provider kept for Debug. Verify by a portable publish with no `wwwroot` folder in which the UI, MudBlazor icons/fonts and the three.js viewer load, and a Debug run that still works.
- [ ] 3.2 Remove the MSIX tile/logo PNGs from publish output (keep `appicon.ico`), and exclude `onnxruntime.dll`/`DirectML.dll`. Verify they are absent from the publish folder and the app still starts and sets its window/taskbar icon.
- [ ] 3.3 Add unit tests for the embedded file provider: index.html, a `_content/MudBlazor` file and a missing path. Verify the tests pass.

## 4. One llama.cpp build (design D4)

- [ ] 4.1 Spike: start the Vulkan build's `llama-server` with `-ngl 0` and Vulkan hidden (no visible devices) and check it serves a chat request on CPU. Verify by recording the result in design.md D4.
- [ ] 4.2 If 4.1 passes: download and ship only the Vulkan build, flattened to `llama/`. Make `LlamaServerHost` run CPU mode on the same exe with GPU offload and Vulkan disabled, and keep the GPU→CPU fallback. If 4.1 fails: keep both builds and record why. Verify `LlamaServerHost` tests pass and, on this PC, both Acceleration=Gpu and Acceleration=Cpu reach "ready".

## 5. Startup ordering and model loading (design D5, D7)

- [ ] 5.1 Change `AiRuntime.StartAsync` to start the embedding server before the chat server, and update the "AI status" logging. Verify with a unit test using fake hosts that embedding is started first and that a failed chat model still leaves embeddings ready.
- [ ] 5.2 Make `AiRuntimeStartupTask` wait for the layout's first-render signal (at most 3 s) before starting, and `CacheCleanupStartupTask` wait until the AI runtime has left `Starting`, then evict at below-normal priority. Verify with unit tests on the ordering and by the log order on a real start (window → embedding ready → chat ready → any eviction line).
- [ ] 5.3 Apply the load option chosen in 1.2 in `LlamaServerHost` arguments (GPU and, per D7, CPU). Verify by a test of the built argument list and by a cold start from E: logging the new chat load time.

## 6. Prompt prefix reuse (design D8, D9)

- [ ] 6.1 Reorder `ItemPass.BuildPromptAsync` into fixed rules → per-source context (pack, instruction, suggested tags) → per-request text ("these N items", item list) → media, so wording no longer depends on the item count before the per-request part. Verify with a unit test that two sheet requests of the same source (8 items and 3 items) share an identical text prefix that ends after the suggested-tags line.
- [ ] 6.2 Apply the same order to the other prompt builders that run many times per source (`PackPass`, `TextureJudge`, `OrientationJudge`, `ConsensusPass`, `CollectionDescriber`, `ImageQuery` where applicable). Verify with a shared-prefix unit test for each builder that runs more than once per source.
- [ ] 6.3 Run the real-model item-pass tests (E4B env vars, non-parallel) and enrich one sample pack before and after the reorder. Verify all answers pass validation and descriptions/tags are of comparable quality, noting any regressions in design.md.

## 7. AI timings (design D10)

- [ ] 7.1 Read llama.cpp `timings` (`prompt_n`, `cache_n`, `prompt_ms`, `predicted_n`, `predicted_ms`) from each chat response in `StructuredChat` into a per-run accumulator. Ignore a missing field. Verify with a unit test that parses a captured llama-server response and one without timings.
- [ ] 7.2 Log the per-run summary line when an import's AI enrichment (and re-enrich) finishes, and a debug line per request. Verify the summary line appears in the log after a real import.
- [ ] 7.3 With 6.x and 7.1 in place, enrich a sample pack of ≥40 images and read `cache_n`. If reuse is ~0, try `--swa-full` and context-checkpoint settings per D9 and keep the cheapest that gives reuse within VRAM. Verify the summary line shows reused tokens on the second and later requests, and record before/after total enrichment time in design.md.

## 8. CI, docs and end-to-end check

- [ ] 8.1 Update the package check in `.github/workflows/build.yml`: require `AssetManager.exe`, `portable`, `LICENSE.txt`, `llama/LICENSE.txt`, `llama/llama-server.exe` (or the kept layout), and the ffmpeg files. Fail on any top-level folder other than `llama`/`ffmpeg` and on more than 5 other top-level files. Verify by running the check script locally against the new publish (passes) and a copy with an extra `de-DE` folder (fails, naming it).
- [ ] 8.2 Update the README Download section: folder contents, updating by keeping `data/`, and that keeping `models` on a fast drive speeds up AI start. Verify the README renders and its instructions match the published layout.
- [ ] 8.3 End-to-end on the user's drive: copy the new build to `E:\AssetManager` (keeping `data/`), reboot or clear the cache, and start. Verify against the specs: the window and catalog appear before the chat model is ready, the embedding server becomes ready before the chat server, and the measured times beat the baseline in 1.1.

## Workflow follow-up

- Sync the delta specs and archive the change after review.
- Tag a release so CI builds the compact zip.
