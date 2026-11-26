# Design

## Context

Facts measured on the user's portable install at `E:\AssetManager` (v0.2.0), 2026-11-26:

| What | Count / size |
|---|---|
| App files outside `data/` | ~825 files, 127 folders, 590 MB |
| …of which top level | 358 `.dll`, 46 `.winmd`, 51 `appicon*Tile/Logo*.png`, 5 `.pdb`, 4 `.pri`, ~100 locale folders |
| `llama/` | 51 files: `cpu/` and `vulkan/` are two near-identical llama.cpp b12141 builds |
| `wwwroot/` | 74 files (JS, CSS, fonts, `.br`/`.gz` duplicates) |
| `data/cache` | 16,053 files (extraction cache; user data, not part of the package) |
| `data/models` | 5.9 GB (Gemma 4 E4B Q4_K_M + mmproj F16 + embeddinggemma) |

From the log for the same day: a warm start loaded the chat model in 6.1 s. A cold start took 38 s from "started" to "Vector index", then **213 s** for the chat model and 5 s for embeddings. At the same time the drive was 100% busy with little throughput, which is the signature of random small reads.

How the code works today:
- Portable publish (`AssetManager.csproj`, `Portable=true`) is self-contained, `WindowsAppSDKSelfContained`, not single-file, not trimmed.
- `IStartupTask`s run once from the Blazor layout (`AppStartup`), so the window already exists when they run. `AiRuntimeStartupTask` (order 30) immediately starts the chat server, then the embedding server. `CacheCleanupStartupTask` (order 20) enumerates the whole cache in the background at the same moment.
- `LlamaServerHost` runs `llama-server -c <ctx*np> -np <n> --no-kv-unified -ngl 999 --jinja --reasoning off [--mmproj]` with default mmap loading. It tries Vulkan, then CPU, each from its own folder.
- `ItemPass.BuildPromptAsync` builds one user message whose first line already differs per request ("Enrich this game asset." / "Enrich these N game assets."). Next come pack name/description, instruction, fixed rules, the suggested-tags line, the per-item text, then the image or audio. `StructuredChat` puts a fixed system prompt first. llama-server's `cache_prompt` is on by default, but the shared prefix is only the system prompt. Gemma's sliding-window attention can also limit prefix reuse unless the server keeps a full-size SWA cache or context checkpoints.
- No request timings are read: the OpenAI-compatible response's llama.cpp `timings` object (`prompt_n`, `cache_n`, `prompt_ms`, `predicted_n`, `predicted_ms`) is ignored.

## Goals / Non-Goals

**Goals:**
- A portable folder with a handful of entries, and far fewer files read at start.
- Cold start from a slow drive: the window and catalog appear quickly, and model loading uses sequential reads and is not slowed by other startup I/O.
- Measurable AI speed-up from prefix reuse, verified by logged `cache_n`.

**Non-Goals:**
- Shrinking `data/cache` or changing the extraction cache format (user data; a separate concern).
- An installer, MSIX, or moving the data folder off the portable folder. The WebView2 profile stays in `data/webview` to keep the portable promise.
- Changing the AI model, quantization or the enrichment output schema.
- Trimming (`PublishTrimmed`) or NativeAOT: MAUI/Blazor/EF reflection makes it risky, and the target is file count and I/O pattern, not size.

## Decisions

### D1. Single-file publish for the app, with native libraries self-extracted
Portable publish sets `PublishSingleFile=true` and `IncludeNativeLibrariesForSelfExtract=true` (plus `IncludeAllContentForSelfExtract` only if the spike shows WinUI needs content files on disk). Managed assemblies are mapped straight from the bundle. Native DLLs (WinAppSDK, Magick, assimp, sqlite, vec0) are extracted once per version to `%TEMP%\.net\AssetManager\<hash>` on the system drive. Later starts then load them from the internal disk, not the external one, which helps the user's scenario directly. `EnableCompressionInSingleFile` stays **off**, because decompressing ~250 MB on every start costs more than reading it.

*Alternatives:* (a) Keep the folder and only drop locales, tiles and PDBs. That cuts about 200 entries but leaves ~600 DLL/winmd files. It is the fallback if single-file fails (see D6). (b) A tiny launcher exe at the top with everything in a `bin\` subfolder. That looks tidy but does not reduce I/O; rejected unless D1 and (a) both fall short of the layout requirement.

*Assumption (recorded):* extracting native libraries to the user's temp folder is acceptable for a portable app. It is a cache that is recreated when missing, not user data, and the catalog and settings stay in `data/`.

### D2. Static web content embedded in the exe
`wwwroot` (including `_content/*` from MudBlazor and `AssetManager.Shared`, and `_framework/blazor.webview.js`) is embedded as resources at publish time. A `BlazorWebView` subclass overrides `CreateFileProvider` to return an embedded-resource file provider, with the physical provider as fallback for Debug builds. Pre-compressed `.br`/`.gz` copies are not embedded: the WebView serves from memory, so compression gains nothing.

*Alternative:* keep `wwwroot/` as a folder. That is simpler but leaves 74 files and a folder the layout requirement does not allow; only used if D6 forces it.

### D3. Drop files that are never used unpackaged
- `SatelliteResourceLanguages=en` removes the locale folders.
- The tile/logo PNGs generated by `MauiIcon` are for MSIX; remove them from publish output with a target after `ComputeResolvedFilesToPublishList`, keeping `appicon.ico`.
- `DebugType=none` for Release portable publish of all projects (the 5 PDBs come from referenced projects).
- `onnxruntime.dll`/`DirectML.dll` (40 MB) come from Windows App SDK's Windows ML. Exclude them via the SDK's opt-out property or by removing them from the publish list. Verify that the app starts and renders without them.

### D4. One llama.cpp build
llama.cpp b12141 Windows builds load backends dynamically (`ggml-*.dll`), and the Vulkan build includes all `ggml-cpu-*` variants. On a machine without a usable Vulkan device, the Vulkan backend fails to initialise and llama-server should fall back to CPU. Ship only the Vulkan build in `llama/` (flattened, no `vulkan/` subfolder). Run CPU mode as the same exe with `-ngl 0` and `--device none` (or the build's equivalent to skip Vulkan init). `LlamaServerHost` keeps its "GPU, then CPU" retry, but on one exe. **Gate:** a spike must show the Vulkan build starts with `-ngl 0` when Vulkan is unavailable (simulated with `GGML_VK_VISIBLE_DEVICES=` / disabled ICD). If it does not, keep both builds but share the identical CPU DLLs. That is still about 25 fewer files.

### D5. Startup I/O ordering
- `AiRuntime.StartAsync` loads **embedding first** (300 MB, makes semantic search available), then chat. The embedding server is independent of chat, so search should not wait 3 minutes behind the big model.
- `AiRuntimeStartupTask` waits until the first catalog page has rendered (a signal from the layout, or at most 3 s after the startup tasks run). The WebView's own cold start then does not compete with a 5 GB read.
- `CacheCleanupStartupTask` waits until the AI runtime has left the `Starting` state (ready or failed) and runs at below-normal I/O priority. Eviction only needs to happen eventually.

### D6. Spike before committing to the layout
Single-file with WinUI/WinAppSDK self-contained plus MAUI plus BlazorWebView is the main technical risk: `.pri` resources, `Microsoft.ui.xaml.dll` activation and WebView2 loader may expect files beside the exe. The first task publishes with D1–D3 and runs the scenario "Compact package still runs" on a clean VM or Windows Sandbox. Files the platform still needs on disk (expected: some `.pri` files) are allowed by the spec ("at most 5 other files"). If more than 5 remain, fall back to D1-alternative (a) plus D2 and report the resulting count before going further.

### D7. Model loading with sequential reads
Add `--no-mmap` to llama-server by default, so llama.cpp reads the GGUF with large buffered sequential reads instead of page faults. With full GPU offload, weights are copied to VRAM anyway, so mmap gains nothing. The cost is a temporary RAM buffer during load on CPU mode, since the weights then live in RAM instead of the page cache. If b12141 offers direct I/O (`--direct-io`), measure it as a third option. **Measurement task:** cold-cache load time from E: (clear standby list with RAMMap or reboot) for mmap vs `--no-mmap`. Pick the faster for GPU, and keep mmap for CPU mode if `--no-mmap` doubles RAM there.

### D8. Prompt layout for prefix reuse
Each prompt builder (item sheet, model, audio, pack pass, texture/orientation judges, collection describer, consensus) is reordered as:
1. system prompt (unchanged, fixed)
2. **fixed rules** for that request kind: the "Describe what each item…", tags, animation-type and contact-sheet explanation lines, worded so they do not depend on item count
3. **per-source context**: pack name, pack description, user instruction, suggested tags (same for every request in one import of one source)
4. per-request variable text: "Enrich these N items", the item list
5. the image or audio

Items 1–3 are byte-identical for every request of one kind in one source, so llama-server's `cache_prompt` reuses their KV. The suggested-tags line can be hundreds of tokens, so this saves the most there. Unit tests lock this order: two requests built for the same source share a prefix that ends after the per-source context.

### D9. Server flags so the prefix is actually reused
Gemma uses sliding-window attention. llama.cpp can reuse a prefix on SWA layers only with `--swa-full` (full-size SWA cache, more VRAM) or through context checkpoints (`--ctx-checkpoints`, on by default in recent builds), which restore state at saved points. With `-np 3 --no-kv-unified`, each slot keeps its own last prompt, and slot choice by prompt similarity sends a request to the slot with the best match. Plan: measure `cache_n` with D8 under defaults first. If `cache_n` stays ~0, try `--swa-full` and checkpoints, and keep the cheapest that gives reuse within the VRAM budget. Startup's existing "fewer slots on failure" fallback covers VRAM shortage. The image itself cannot be cached across requests, since each contact sheet is new.

### D10. Timings from the server
`StructuredChat` reads llama.cpp's `timings` from `ChatResponse.RawRepresentation` (OpenAI `ChatCompletion` → raw JSON) and adds them to a per-run accumulator held by the enrichment job. If the field is missing (another server), it records nothing. The job logs one info line at the end ("AI: 52 requests, prompt 41,200 tok (cache 28,900), 96 s; generated 38,000 tok, 610 s"). Each request is logged at debug level. These numbers also guide follow-ups: if generation dominates, output length is the next lever. That is outside this change, because it changes the enrichment output (Non-Goals).

## Risks / Trade-offs

- [WinUI/MAUI in single-file breaks at runtime: missing `.pri`, XAML resources, WebView2 loader] → D6 spike first on a clean machine; allow a few loose files; fallback layout.
- [Self-extracted natives land in `%TEMP%` and may be cleaned by disk cleanup] → .NET re-extracts automatically on the next start; first start after cleanup is slower once.
- [Antivirus scans a 250 MB exe on each start] → measure cold start before and after on the user's drive. If worse, compare with the folder fallback before shipping.
- [Embedded `wwwroot` breaks Debug hot reload] → physical provider in Debug, embedded only in portable publish.
- [`--swa-full` raises VRAM use and fails on small GPUs] → only enabled if measurements need it; the existing fallback to one slot or CPU applies.
- [Reordered prompts change answer quality slightly] → same validation; run the real-model tests (see memory note: `E4B` env vars, non-parallel classes) before and after and compare on a sample pack.
- [One llama build: CPU-only machines fail if Vulkan init crashes instead of failing cleanly] → D4 gate; keep two builds if the spike fails.
- [Deferring AI start delays enrichment by a few seconds] → negligible next to model load time.

## Migration Plan

- Users replace the old folder's program files with the new zip's contents and keep `data/`. Old loose DLLs left behind are harmless but should be deleted. The README "Download/Update" section says: extract to a new folder and move `data/` over, or delete everything except `data/` first.
- Rollback: the previous release zip still works with the same `data/` (no format changes).

## Open Questions

- Which of `--no-mmap` / mmap / direct I/O is fastest on the user's drive (D7). Measured during implementation; the choice does not change specs or tasks.
- Whether `--swa-full` is needed for reuse with Gemma 4 E4B in b12141 (D9). Measured; does not change specs or tasks.
