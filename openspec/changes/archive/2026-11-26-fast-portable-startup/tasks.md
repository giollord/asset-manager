# Tasks

## 1. Baseline measurements

- [x] 1.1 Write `tools/measure-startup.ps1`: it publishes the portable build to a given folder, counts files/folders/top-level entries, and reads from the app log the time from "started" to "Vector index" and each "AI server … ready in N s". Verify by running it against `E:\AssetManager` and recording the numbers (825 files / 213 s chat load) in a "Baseline" note in design.md.
- [x] 1.2 Measure cold-cache chat model load from E: with the current mmap default vs `--no-mmap` (and `--direct-io` if b12141 has it), by starting `llama-server` by hand after clearing the standby list. Verify by recording the three timings in design.md D7 and choosing the default.

## 2. Responsive window during background jobs (design D11–D13)

- [x] 2.1 Add a reproduction script `tools/repro-busy-start.ps1` that purges the OS cache for a portable folder, starts it and reports every 5 s whether the window responds. Verify it reproduces "Not Responding" on the `E:\AM-test` copy with the interrupted Kenney import pending (set job 9 back to Pending in the copy first).
- [x] 2.2 Implement `BackgroundIo` (D11) and enter it around each job run in `JobQueue` (and the startup cache eviction). Verify with unit tests: code in the scope and its `Task.Run`/continuations reports background mode, the same pool threads report normal mode outside the scope, and a thread with a `SynchronizationContext` is never switched.
- [x] 2.3 Delay re-queuing interrupted jobs until the "window ready" signal plus 5 s, with a fallback timer (D12); add the signal to `AppStartup`, raised by `MainLayout` after the catalog renders. Verify with unit tests: resumed jobs are published as pending at once but not run before the signal, and jobs enqueued by the user run immediately.
- [x] 2.4 Run startup tasks and `CatalogStatsService` computations on the thread pool (D13), and make Browse apply its URL query before awaiting filter options. Verify with unit tests: stats queries started from a thread with a synchronization context run on another thread (and on it when `ComputeOnThreadPool` is off); the full test suite passes.
- [x] 2.5 Re-run 2.1 on `E:\AM-test` with the Kenney import pending, using a portable publish of this branch. Verify the window never shows "Not Responding", the Dashboard and Browse show content while the scan runs, and the import resumes after the window is ready (log order). If pages still stall while rows stream, add `Task.Run` at the heaviest service entry points per D13 and re-verify.

## 3. Single-file spike (design D6)

- [x] 3.1 In the `Portable=true` property group add `PublishSingleFile`, `IncludeNativeLibrariesForSelfExtract`, `SatelliteResourceLanguages=en`, `DebugType=none`. Publish, list what remains beside the exe, and start the exe. Verify the window opens with styles and the Browse page renders, and note every loose file the runtime still needs.

## 4. Compact package (design D1–D3)

- [x] 4.1 Ship the web content as one `wwwroot.zip` (post-publish zip without `.br`/`.gz`), served by an `AppBlazorWebView` subclass through `ZipFileProvider`, with the stock provider kept for Debug (design D2 outcome). Verify by a portable publish with no `wwwroot` folder in which the UI, MudBlazor icons/fonts and the three.js viewer load, and a Debug run that still works.
- [x] 4.2 Remove the MSIX tile/logo PNGs from publish output (keep `appicon.ico`), and exclude `onnxruntime.dll`/`DirectML.dll`. Verify they are absent from the publish folder and the app still starts and sets its window/taskbar icon.
- [x] 4.3 Add unit tests for the zip file provider: index.html, a `_content/MudBlazor` file and a missing path. Verify the tests pass.

## 5. One llama.cpp build (design D4)

- [x] 5.1 Spike: start the Vulkan build's `llama-server` with `-ngl 0` and Vulkan hidden (no visible devices) and check it serves a chat request on CPU. Verify by recording the result in design.md D4.
- [x] 5.2 If 5.1 passes: download and ship only the Vulkan build, flattened to `llama/`. Make `LlamaServerHost` run CPU mode on the same exe with GPU offload and Vulkan disabled, and keep the GPU→CPU fallback. If 5.1 fails: keep both builds and record why. Verify `LlamaServerHost` tests pass and, on this PC, both Acceleration=Gpu and Acceleration=Cpu reach "ready".

## 6. Startup ordering (design D5, D7)

- [x] 6.1 Change `AiRuntime.StartAsync` to start the embedding server before the chat server, and update the "AI status" logging. Verify with a unit test using fake hosts that embedding is started first and that a failed chat model still leaves embeddings ready.
- [x] 6.2 Make `AiRuntimeStartupTask` wait for the layout's first-render signal (at most 3 s) before starting, and `CacheCleanupStartupTask` wait until the AI runtime has left `Starting`, then evict at below-normal priority. Verify with unit tests on the ordering and by the log order on a real start (window → embedding ready → chat ready → any eviction line).
- [x] 6.3 Keep llama-server's default load mode (mmap, per the 1.2 measurements) and add a test of the built argument list asserting no `--no-mmap`/`--load-mode` override. Verify the test passes.

## 7. Prompt prefix reuse (design D8, D9)

- [x] 7.1 Reorder `ItemPass.BuildPromptAsync` into fixed rules → per-source context (pack, instruction, suggested tags) → per-request text ("these N items", item list) → media, so wording no longer depends on the item count before the per-request part. Verify with a unit test that two sheet requests of the same source (8 items and 3 items) share an identical text prefix that ends after the suggested-tags line.
- [x] 7.2 Apply the same order to the other prompt builders that run many times per source (`PackPass`, `TextureJudge`, `OrientationJudge`, `ConsensusPass`, `CollectionDescriber`, `ImageQuery` where applicable). Verify with a shared-prefix unit test for each builder that runs more than once per source.
- [x] 7.3 Run the real-model item-pass tests (E4B env vars, non-parallel) and enrich one sample pack before and after the reorder. Verify all answers pass validation and descriptions/tags are of comparable quality, noting any regressions in design.md.

## 8. AI timings (design D10)

- [x] 8.1 Read llama.cpp `timings` (`prompt_n`, `cache_n`, `prompt_ms`, `predicted_n`, `predicted_ms`) from each chat response in `StructuredChat` into a per-run accumulator. Ignore a missing field. Verify with a unit test that parses a captured llama-server response and one without timings.
- [x] 8.2 Log the per-run summary line when an import's AI enrichment (and re-enrich) finishes, and a debug line per request. Verify the summary line appears in the log after a real import.
- [x] 8.3 With 7.x and 8.1 in place, enrich a sample pack of ≥40 images and read `cache_n`. If reuse is ~0, try `--swa-full` and context-checkpoint settings per D9 and keep the cheapest that gives reuse within VRAM. Verify the summary line shows reused tokens on the second and later requests, and record before/after total enrichment time in design.md.

## 9. CI, docs and end-to-end check

- [x] 9.1 Move the package check into `tools/check-package.ps1` (run by `.github/workflows/build.yml`): require `AssetManager.exe`, `portable`, `LICENSE.txt`, `wwwroot.zip`, `llama/LICENSE.txt`, `llama/llama-server.exe` and the ffmpeg files. Fail on models, debug symbols, any top-level folder other than `llama`/`ffmpeg`/`licenses` and on more than 5 other top-level files. Verify by running the check script locally against the new publish (passes) and a copy with an extra `de-DE` folder (fails, naming it).
- [x] 9.2 Update the README Download section: folder contents, updating by keeping `data/`, and that keeping `models` on a fast drive speeds up AI start. Verify the README renders and its instructions match the published layout.
- [x] 9.3 End-to-end on the user's drive (confirmed by the user on 2026-11-26 after the D12/D14/D16 follow-ups; times not formally measured): copy the new build to `E:\AssetManager` (keeping `data/`), reboot or clear the cache, and start. Verify against the specs: the window and catalog appear before the chat model is ready, the embedding server becomes ready before the chat server, and the measured times beat the baseline in 1.1.

## 10. Follow-ups from the end-to-end check (design D5, D12, D14, D15)

- [x] 10.1 Start order instead of I/O priorities: the window is ready once the first page has its data; the AI starts after it, interrupted jobs after the AI. Verify on a copy of the catalog with the Kenney import interrupted.
- [x] 10.2 Extraction cache on the system drive by default, with a folder setting. Verify by measuring writes during the Kenney scan.
- [x] 10.4 Load the AI models when first needed (setting to load at startup); cache limit 4 GB by default (design D16). Verify with unit tests and in the app: no model loads at startup, a search by meaning loads the embedding model.
- [x] 10.3 Show model loading in the jobs panel; jobs chip opens the Jobs page; readable, live quick view with the current file. Verify with UI tests and in the app.

## Workflow follow-up

- Sync the delta specs and archive the change after review.
- Tag a release so CI builds the compact zip.
