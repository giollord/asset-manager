using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AssetManager.AI.Runtime;
using AssetManager.Core.Settings;

namespace AssetManager.Tests.Ai;

public class ModelDownloaderTests
{
    /// <summary>Serves one payload; honours Range requests unless told not to.</summary>
    private sealed class FakeServer(byte[] payload, bool supportsRange = true) : HttpMessageHandler
    {
        public List<RangeHeaderValue?> Ranges { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Ranges.Add(request.Headers.Range);
            var from = supportsRange ? request.Headers.Range?.Ranges.First().From ?? 0 : 0;
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload, (int)from, payload.Length - (int)from),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class SyncProgress : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Reports { get; } = [];
        public void Report(DownloadProgress value) { lock (Reports) Reports.Add(value); }
    }

    private static byte[] Payload(int size)
    {
        var b = new byte[size];
        new Random(42).NextBytes(b);
        return b;
    }

    private static ModelFile FileFor(byte[] payload, ModelRole role = ModelRole.Embedding, string name = "m.gguf", string? sha = null) =>
        new(role, name, "https://example.invalid/" + name, name, payload.Length, sha ?? Convert.ToHexStringLower(SHA256.HashData(payload)));

    [Fact]
    public async Task Downloaded_models_are_found_after_the_data_folder_moves()
    {
        // Spec "Relocatable settings": a portable copy moved to another folder or drive letter keeps its models.
        var ct = TestContext.Current.CancellationToken;
        using var dir = Fixtures.FixtureBuilder.TempDir();
        var root = Path.Combine(dir.Path, "E", "AssetManager", "data");
        var chat = Payload(200_000);
        var settings = new JsonAppSettingsStore(Path.Combine(root, "settings.json"));
        var downloader = new ModelDownloader(settings, new HttpClient(new MultiServer(new() { ["chat.gguf"] = chat }))) { Folder = Path.Combine(root, "models") };
        await downloader.InstallAsync(new ModelPackage("p", "P", "", [FileFor(chat, ModelRole.Chat, "chat.gguf")]), null, ct);

        var moved = Path.Combine(dir.Path, "F", "AssetManager", "data");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        Directory.Move(root, moved);
        var reloaded = new JsonAppSettingsStore(Path.Combine(moved, "settings.json")).Current;

        Assert.Equal(Path.Combine(moved, "models", "chat.gguf"), reloaded.ChatModelPath);
        Assert.Equal(chat, await File.ReadAllBytesAsync(reloaded.ChatModelPath!, ct));
    }

    [Fact]
    public async Task Downloads_verifies_reports_progress_and_updates_settings()
    {
        using var dir = Fixtures.FixtureBuilder.TempDir();
        var settings = new JsonAppSettingsStore(Path.Combine(dir.Path, "s.json"));
        var chat = Payload(3_000_000);
        var mmproj = Payload(500_000);
        var server = new MultiServer(new() { ["chat.gguf"] = chat, ["mm.gguf"] = mmproj });
        var downloader = new ModelDownloader(settings, new HttpClient(server)) { Folder = Path.Combine(dir.Path, "models") };
        var package = new ModelPackage("p", "P", "", [FileFor(chat, ModelRole.Chat, "chat.gguf"), FileFor(mmproj, ModelRole.Mmproj, "mm.gguf")]);
        var progress = new SyncProgress();

        Assert.False(downloader.IsInstalled(package));
        await downloader.InstallAsync(package, progress, TestContext.Current.CancellationToken);

        Assert.True(downloader.IsInstalled(package));
        Assert.Equal(chat, await File.ReadAllBytesAsync(downloader.PathOf(package.Files[0]), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(downloader.PathOf(package.Files[0]) + ".part"));
        Assert.Equal(downloader.PathOf(package.Files[0]), settings.Current.ChatModelPath);
        Assert.Equal(downloader.PathOf(package.Files[1]), settings.Current.MmprojPath);

        // Reports are passed on synchronously: the final one has arrived when InstallAsync returns.
        Assert.Contains(progress.Reports, r => r.Done == package.TotalSize && r.Total == package.TotalSize);
        Assert.All(progress.Reports, r => Assert.InRange(r.Done, 0, package.TotalSize));
    }

    [Fact]
    public async Task Resumes_a_partial_download_with_a_range_request()
    {
        using var dir = Fixtures.FixtureBuilder.TempDir();
        var payload = Payload(2_000_000);
        var file = FileFor(payload);
        var server = new FakeServer(payload);
        var downloader = new ModelDownloader(new JsonAppSettingsStore(Path.Combine(dir.Path, "s.json")), new HttpClient(server))
            { Folder = dir.Path };
        await File.WriteAllBytesAsync(downloader.PathOf(file) + ".part", payload[..700_000], TestContext.Current.CancellationToken);

        await downloader.DownloadAsync(file, ct: TestContext.Current.CancellationToken);

        Assert.Equal(700_000, Assert.Single(server.Ranges)!.Ranges.Single().From);
        Assert.Equal(payload, await File.ReadAllBytesAsync(downloader.PathOf(file), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Starts_over_when_the_server_ignores_the_range()
    {
        using var dir = Fixtures.FixtureBuilder.TempDir();
        var payload = Payload(1_000_000);
        var file = FileFor(payload);
        var downloader = new ModelDownloader(new JsonAppSettingsStore(Path.Combine(dir.Path, "s.json")), new HttpClient(new FakeServer(payload, supportsRange: false)))
            { Folder = dir.Path };
        await File.WriteAllBytesAsync(downloader.PathOf(file) + ".part", payload[..300_000], TestContext.Current.CancellationToken);

        await downloader.DownloadAsync(file, ct: TestContext.Current.CancellationToken);

        Assert.Equal(payload, await File.ReadAllBytesAsync(downloader.PathOf(file), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Checksum_mismatch_deletes_the_file_and_leaves_settings_alone()
    {
        using var dir = Fixtures.FixtureBuilder.TempDir();
        var settings = new JsonAppSettingsStore(Path.Combine(dir.Path, "s.json"));
        var payload = Payload(100_000);
        var file = FileFor(payload, sha: new string('0', 64));
        var downloader = new ModelDownloader(settings, new HttpClient(new FakeServer(payload))) { Folder = dir.Path };

        var e = await Assert.ThrowsAsync<InvalidDataException>(() =>
            downloader.InstallAsync(new ModelPackage("p", "P", "", [file]), ct: TestContext.Current.CancellationToken));

        Assert.Contains("checksum mismatch", e.Message);
        Assert.False(File.Exists(downloader.PathOf(file)));
        Assert.False(File.Exists(downloader.PathOf(file) + ".part"));
        Assert.Null(settings.Current.EmbeddingModelPath);
    }

    [Fact]
    public async Task Installs_tracks_a_failed_download_and_reports_the_error()
    {
        using var dir = Fixtures.FixtureBuilder.TempDir();
        var payload = Payload(10_000);
        var file = FileFor(payload, sha: new string('0', 64));
        var installs = new ModelInstalls(new ModelDownloader(new JsonAppSettingsStore(Path.Combine(dir.Path, "s.json")),
            new HttpClient(new FakeServer(payload))) { Folder = dir.Path });
        var package = new ModelPackage("p", "P", "", [file]);

        installs.Start(package);
        for (var i = 0; i < 100 && installs.IsRunning(package); i++) await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.False(installs.IsRunning(package));
        Assert.Contains("checksum mismatch", installs.Error(package));
    }

    private sealed class MultiServer(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(files[request.RequestUri!.Segments[^1]]) });
    }
}
