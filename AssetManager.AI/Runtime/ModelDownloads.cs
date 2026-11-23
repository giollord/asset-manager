using System.Net.Http.Headers;
using System.Security.Cryptography;
using AssetManager.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetManager.AI.Runtime;

public enum ModelRole { Chat, Mmproj, Embedding }

/// <summary>A downloadable model file (pinned by SHA-256).</summary>
public sealed record ModelFile(ModelRole Role, string Title, string Url, string FileName, long Size, string Sha256);

/// <summary>A named set of files installed together (e.g. the chat model plus its vision projector).</summary>
public sealed record ModelPackage(string Id, string Title, string Description, IReadOnlyList<ModelFile> Files)
{
    public long TotalSize => Files.Sum(f => f.Size);
}

/// <summary>The recommended models (design D7). Downloads happen only when the user starts them.</summary>
public static class ModelCatalog
{
    private const string Hf = "https://huggingface.co/";

    public static readonly ModelPackage Gemma4E4B = new("gemma-4-e4b", "Gemma 4 E4B (Q4_K_M)",
        "Chat and vision model used for descriptions, tags, licenses and orientation.",
        [
            new(ModelRole.Chat, "Gemma 4 E4B-it Q4_K_M", Hf + "unsloth/gemma-4-E4B-it-GGUF/resolve/main/gemma-4-E4B-it-Q4_K_M.gguf",
                "gemma-4-E4B-it-Q4_K_M.gguf", 4_977_171_584, "85a896a047553e842f25297ee5b031d64ff30147d9c4af17b1e4b394cd1fab87"),
            new(ModelRole.Mmproj, "Vision projector (F16)", Hf + "unsloth/gemma-4-E4B-it-GGUF/resolve/main/mmproj-F16.gguf",
                "gemma-4-E4B-it-mmproj-F16.gguf", 990_372_672, "ddf46c21d7078e95338cfc22306b19b276a29a5ad089023449dd54d4b6170a51"),
        ]);

    public static readonly ModelPackage EmbeddingGemma = new("embeddinggemma-300m", "EmbeddingGemma 300M (Q8_0)",
        "Text embeddings for semantic search and \"find similar\".",
        [
            new(ModelRole.Embedding, "EmbeddingGemma 300M Q8_0", Hf + "ggml-org/embeddinggemma-300M-GGUF/resolve/main/embeddinggemma-300M-Q8_0.gguf",
                "embeddinggemma-300M-Q8_0.gguf", 333_590_944, "b5ce9d77a3fc4b3b39ccb5643c36777911cc4eb46a66962eadfa3f5f60490d63"),
        ]);

    public static IReadOnlyList<ModelPackage> All { get; } = [Gemma4E4B, EmbeddingGemma];
}

public sealed record DownloadProgress(string File, long Done, long Total)
{
    public double Fraction => Total > 0 ? (double)Done / Total : 0;
}

/// <summary>
/// Downloads model files to <c>%LOCALAPPDATA%\AssetManager\models</c>: resumable (<c>.part</c> + HTTP Range),
/// verified against the pinned SHA-256, and moved into place only when complete. On success the app settings point
/// at the new files, which restarts the AI servers.
/// </summary>
public sealed class ModelDownloader(IAppSettingsStore settings, HttpClient? http = null, ILogger<ModelDownloader>? logger = null)
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ILogger _log = logger ?? (ILogger)NullLogger.Instance;

    public string Folder { get; init; } = AppPaths.Models;

    public string PathOf(ModelFile f) => Path.Combine(Folder, f.FileName);

    public bool IsInstalled(ModelPackage p) => p.Files.All(f => File.Exists(PathOf(f)) && new FileInfo(PathOf(f)).Length == f.Size);

    public async Task InstallAsync(ModelPackage package, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Folder);
        long before = 0;
        var total = package.TotalSize;
        foreach (var file in package.Files)
        {
            var offset = before;
            await DownloadAsync(file, new Progress<DownloadProgress>(p => progress?.Report(new DownloadProgress(file.Title, offset + p.Done, total))), ct);
            before += file.Size;
        }
        settings.Update(s =>
        {
            foreach (var f in package.Files)
            {
                switch (f.Role)
                {
                    case ModelRole.Chat: s.ChatModelPath = PathOf(f); break;
                    case ModelRole.Mmproj: s.MmprojPath = PathOf(f); break;
                    case ModelRole.Embedding: s.EmbeddingModelPath = PathOf(f); break;
                }
            }
        });
        _log.LogInformation("Installed model package {Package}", package.Id);
    }

    public async Task DownloadAsync(ModelFile file, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var target = PathOf(file);
        if (File.Exists(target) && new FileInfo(target).Length == file.Size) return;
        var part = target + ".part";

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long done = 0;
        if (File.Exists(part))
        {
            // Resume: hash what is already there.
            await using var existing = File.OpenRead(part);
            var buf = new byte[1 << 20];
            int n;
            while ((n = await existing.ReadAsync(buf, ct)) > 0) sha.AppendData(buf, 0, n);
            done = existing.Length;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
        if (done > 0) request.Headers.Range = new RangeHeaderValue(done, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (done > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            // The server ignored the range: start over.
            sha.GetHashAndReset();
            done = 0;
            File.Delete(part);
        }
        response.EnsureSuccessStatusCode();

        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(part, done > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            var buffer = new byte[1 << 20];
            int read;
            var lastReport = DateTime.MinValue;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                sha.AppendData(buffer, 0, read);
                done += read;
                if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(200))
                {
                    progress?.Report(new DownloadProgress(file.Title, done, file.Size));
                    lastReport = DateTime.UtcNow;
                }
            }
        }
        progress?.Report(new DownloadProgress(file.Title, done, file.Size));

        var hash = Convert.ToHexStringLower(sha.GetHashAndReset());
        if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(part);
            throw new InvalidDataException($"{file.FileName}: checksum mismatch (expected {file.Sha256}, got {hash}).");
        }
        File.Move(part, target, overwrite: true);
    }
}

/// <summary>Tracks user-started model downloads so they survive page navigation; one download per package.</summary>
public sealed class ModelInstalls(ModelDownloader downloader, ILogger<ModelInstalls>? logger = null)
{
    private readonly Dictionary<string, (CancellationTokenSource Cts, Task Task)> _running = new();
    private readonly Dictionary<string, DownloadProgress> _progress = new();
    private readonly Dictionary<string, string> _errors = new();
    private readonly Lock _sync = new();

    public event Action? Changed;

    public ModelDownloader Downloader => downloader;

    public bool IsRunning(ModelPackage p) { lock (_sync) return _running.ContainsKey(p.Id); }

    public DownloadProgress? Progress(ModelPackage p) { lock (_sync) return _progress.GetValueOrDefault(p.Id); }

    public string? Error(ModelPackage p) { lock (_sync) return _errors.GetValueOrDefault(p.Id); }

    public void Start(ModelPackage package)
    {
        lock (_sync)
        {
            if (_running.ContainsKey(package.Id)) return;
            _errors.Remove(package.Id);
            var cts = new CancellationTokenSource();
            var progress = new Progress<DownloadProgress>(p =>
            {
                lock (_sync) _progress[package.Id] = p;
                Changed?.Invoke();
            });
            _running[package.Id] = (cts, Task.Run(async () =>
            {
                try
                {
                    await downloader.InstallAsync(package, progress, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    lock (_sync) _errors[package.Id] = "Cancelled (the partial download is kept and resumes next time).";
                }
                catch (Exception e)
                {
                    logger?.LogError(e, "Model download {Package} failed", package.Id);
                    lock (_sync) _errors[package.Id] = e.Message;
                }
                finally
                {
                    lock (_sync)
                    {
                        _running.Remove(package.Id);
                        _progress.Remove(package.Id);
                    }
                    Changed?.Invoke();
                }
            }));
        }
        Changed?.Invoke();
    }

    public void Cancel(ModelPackage package)
    {
        lock (_sync)
            if (_running.TryGetValue(package.Id, out var r)) r.Cts.Cancel();
    }
}
