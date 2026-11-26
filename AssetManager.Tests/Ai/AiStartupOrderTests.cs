using AssetManager.AI.Runtime;
using AssetManager.Core;
using AssetManager.Core.Settings;
using AssetManager.Processing;
using AssetManager.Processing.Cache;
using AssetManager.Tests.Fixtures;

namespace AssetManager.Tests.Ai;

/// <summary>Design D5: the order in which the AI servers and the cache cleanup start.</summary>
[Collection(AiModelCollection.Name)]
public class AiStartupOrderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonAppSettingsStore Settings(TempDir dir, string? chat, string? embed)
    {
        var store = new JsonAppSettingsStore(Path.Combine(dir.Path, "settings.json"));
        store.Update(s =>
        {
            s.ChatModelPath = chat;
            s.MmprojPath = null;
            s.EmbeddingModelPath = embed;
        });
        return store;
    }

    [Fact]
    public async Task Embedding_server_starts_before_the_chat_server()
    {
        using var dir = FixtureBuilder.TempDir();
        await using var runtime = new AiRuntime(Settings(dir, @"C:\nope\chat.gguf", @"C:\nope\embed.gguf"));
        var order = new List<string>();
        runtime.Embedding.StateChanged += () => { lock (order) order.Add("embedding"); };
        runtime.Chat.StateChanged += () => { lock (order) order.Add("chat"); };

        await runtime.StartAsync(Ct);

        Assert.Equal("embedding", order.First());
        Assert.Contains("chat", order);
        Assert.Equal(AiServerState.Failed, runtime.Chat.State);
    }

    /// <summary>Loading the models shows in the jobs panel, model by model, and ends there with the outcome.</summary>
    [Fact]
    public async Task Model_loading_shows_as_an_entry_in_the_jobs_panel()
    {
        using var dir = FixtureBuilder.TempDir();
        var hub = new AssetManager.Data.Jobs.ProgressHub();
        var seen = new List<AssetManager.Core.Jobs.JobSnapshot>();
        hub.Changed += s => { lock (seen) seen.Add(s); };
        await using var runtime = new AiRuntime(Settings(dir, @"C:
ope\chat.gguf", @"C:
ope\embed.gguf"), hub: hub);

        await runtime.StartAsync(Ct);

        Assert.All(seen, s => Assert.Equal((AiRuntime.LoadActivityId, "Loading AI models"), (s.Id, s.Title)));
        Assert.Equal([("Embedding model", "embed.gguf"), ("Chat model", "chat.gguf")],
            seen.Where(s => s.IsActive).Select(s => (s.Stage, s.CurrentItem)));
        var last = seen.Last();
        Assert.Equal(AssetManager.Core.Domain.JobState.Failed, last.State);
        Assert.Contains("not found", last.Error);
        Assert.Empty(hub.Active); // gone from the panel once finished
    }

    [Fact]
    public async Task Missing_chat_model_still_leaves_embeddings_ready()
    {
        Assert.SkipWhen(AiModelFactAttribute.Embed is null, "Set ASSETMANAGER_AI_EMBED to an embedding GGUF to run.");
        using var dir = FixtureBuilder.TempDir();
        await using var runtime = new AiRuntime(Settings(dir, @"C:\nope\chat.gguf", AiModelFactAttribute.Embed));

        await runtime.StartAsync(Ct);

        Assert.Equal(AiServerState.Ready, runtime.Embedding.State);
        Assert.Equal(AiServerState.Failed, runtime.Chat.State);
        Assert.Contains("not found", runtime.Chat.Error);
    }

    [Fact]
    public async Task Ai_start_waits_for_the_window()
    {
        using var dir = FixtureBuilder.TempDir();
        var store = Settings(dir, null, null);
        store.Update(s => s.AiLoadAtStartup = true);
        await using var runtime = new AiRuntime(store);
        var signals = new StartupSignals();

        await new AiRuntimeStartupTask(runtime, signals).RunAsync(Ct);
        await Task.Delay(300, Ct);
        Assert.False(signals.AiSettled.IsCompleted); // still waiting for the window

        signals.SignalWindowReady();
        await signals.AiSettled.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    /// <summary>Design D16: by default nothing loads at startup, and the AI counts as settled at once.</summary>
    [Fact]
    public async Task By_default_models_are_not_loaded_at_startup()
    {
        using var dir = FixtureBuilder.TempDir();
        await using var runtime = new AiRuntime(Settings(dir, @"C:\nope\chat.gguf", @"C:\nope\embed.gguf"));
        var signals = new StartupSignals();

        await new AiRuntimeStartupTask(runtime, signals).RunAsync(Ct);
        await signals.AiSettled.WaitAsync(TimeSpan.FromSeconds(10), Ct); // no window needed: nothing to load

        Assert.Equal((AiServerState.NotConfigured, AiServerState.NotConfigured), (runtime.Chat.State, runtime.Embedding.State));
        Assert.Equal("Not loaded: models load when needed", runtime.StatusText);
    }

    /// <summary>Design D16: each model loads when first needed, alone, and a failed one is not retried with the same settings.</summary>
    [Fact]
    public async Task Each_model_loads_on_demand_and_a_failure_is_not_retried()
    {
        using var dir = FixtureBuilder.TempDir();
        var hub = new AssetManager.Data.Jobs.ProgressHub();
        var loads = 0;
        hub.Finished += _ => Interlocked.Increment(ref loads);
        var store = Settings(dir, @"C:\nope\chat.gguf", @"C:\nope\embed.gguf");
        await using var runtime = new AiRuntime(store, hub: hub);

        await runtime.EnsureChatAsync(Ct);
        Assert.Equal((AiServerState.Failed, AiServerState.NotConfigured), (runtime.Chat.State, runtime.Embedding.State));
        Assert.Equal(1, loads);

        await runtime.EnsureChatAsync(Ct); // same settings: not tried again
        Assert.Equal(1, loads);

        store.Update(s => s.ChatModelPath = @"C:\nope\other.gguf");
        await runtime.EnsureChatAsync(Ct); // new settings: tried again
        Assert.Equal(2, loads);

        await runtime.EnsureEmbeddingsAsync(Ct);
        Assert.Equal(AiServerState.Failed, runtime.Embedding.State);
        Assert.Equal(3, loads);
    }

    [Fact]
    public async Task Cache_cleanup_waits_until_the_ai_has_settled()
    {
        using var dir = FixtureBuilder.TempDir();
        var cache = new ExtractionCache(dir.Path, () => new CacheLimits(long.MaxValue, TimeSpan.Zero)); // everything is expired
        (await cache.GetOrCreateAsync("old", (d, _) => File.WriteAllTextAsync(Path.Combine(d, "f.txt"), "x"), Ct)).Dispose();
        var signals = new StartupSignals();

        await new CacheCleanupStartupTask(cache, signals: signals).RunAsync(Ct);
        await Task.Delay(300, Ct);
        var lease = cache.TryOpen("old");
        Assert.NotNull(lease); // not evicted while the AI is loading
        lease.Dispose();

        signals.SignalAiSettled();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Directory.EnumerateDirectories(dir.Path).Any() && DateTime.UtcNow < deadline) await Task.Delay(50, Ct);
        Assert.Empty(Directory.EnumerateDirectories(dir.Path));
    }
}
