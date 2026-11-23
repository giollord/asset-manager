using AssetManager.AI.Enrichment;
using AssetManager.AI.Runtime;
using AssetManager.Core.Settings;
using AssetManager.Data.Jobs;
using AssetManager.Tests.Data;
using Microsoft.Extensions.AI;

namespace AssetManager.Tests.Ai;

/// <summary>AI requests survive a restart of the chat server (e.g. after changing "Requests at once").</summary>
public class ServerRestartTests
{
    private sealed class Probe : IChatServerProbe
    {
        public volatile IChatClient? Current;
        public IChatClient? Client => Current;
        public Uri? Endpoint { get; set; }
        public AiServerState State { get; set; } = AiServerState.Ready;
    }

    private sealed class DeadServer : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new AggregateException(new HttpRequestException("No connection could be made because the target machine actively refused it."));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public object? GetService(Type t, object? k = null) => null;
        public void Dispose() { }
    }

    private static async Task<(AiQueue, AiRuntime, JobQueue)> QueueAsync(TestCatalog c)
    {
        var hub = new ProgressHub();
        var jobs = new JobQueue(c.Store, [], hub);
        var runtime = new AiRuntime(new JsonAppSettingsStore(Path.Combine(c.Dir.Path, "settings.json")));
        return (new AiQueue(c.Store, runtime, jobs, hub), runtime, await Task.FromResult(jobs));
    }

    [Fact]
    public async Task A_request_waits_for_the_restarted_server()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await TestCatalog.CreateAsync();
        var (queue, runtime, jobs) = await QueueAsync(c);
        await using var _ = jobs;
        await using var __ = runtime;
        var probe = new Probe { Current = new DeadServer(), Endpoint = new Uri("http://127.0.0.1:1000/") };
        var client = new GatedChatClient(probe, queue);

        var request = client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: ct);
        probe.State = AiServerState.Starting; // the settings changed: the old server is gone, a new one is loading
        await Task.Delay(400, ct);
        Assert.False(request.IsCompleted);
        (probe.Current, probe.Endpoint, probe.State) = (new FakeChatClient("""{"description":"ok"}"""), new Uri("http://127.0.0.1:2000/"), AiServerState.Ready);

        Assert.Contains("ok", (await request).Text);
    }

    [Fact]
    public async Task A_server_that_stays_unreachable_fails_the_request()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await TestCatalog.CreateAsync();
        var (queue, runtime, jobs) = await QueueAsync(c);
        await using var _ = jobs;
        await using var __ = runtime;
        var probe = new Probe { Current = new DeadServer(), Endpoint = new Uri("http://127.0.0.1:1000/") };
        var client = new GatedChatClient(probe, queue) { RestartNoticeTimeout = TimeSpan.FromMilliseconds(300) };

        await Assert.ThrowsAsync<AggregateException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: ct));
    }

    [Fact]
    public async Task Schemas_can_be_built_from_many_threads()
    {
        // Regression: parallel requests raced on the shared options ("instance is read-only").
        await Parallel.ForAsync(0, 64, TestContext.Current.CancellationToken, (_, _) =>
        {
            ChatResponseFormat.ForJsonSchema<CollectionDescriptionResult>(StructuredChat.JsonOptions);
            return ValueTask.CompletedTask;
        });
    }
}
