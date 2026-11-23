using AssetManager.AI.Runtime;
using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace AssetManager.AI.Enrichment;

/// <summary>Numbers for the AI queue panel.</summary>
public sealed record AiQueueStatus(int Pending, bool Paused, double ItemsPerMinute, TimeSpan? TimeToDrain, int Completed, int Failed);

/// <summary>
/// AI queue control (design D8): a pause gate every AI request waits on (<see cref="GatedChatClient"/>), the rate of
/// enriched items over the last 5 minutes, the pending count (assets waiting for enrichment), time to drain, the
/// completed/failed counts of the current run, and cancel.
/// </summary>
public sealed class AiQueue(CatalogStore catalog, AiRuntime runtime, IJobQueue jobs, IProgressHub hub, TimeProvider? time = null)
{
    public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(5);
    public const string JobTypePrefix = "ai.";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly Queue<DateTimeOffset> _recent = new();
    private TaskCompletionSource _resumed = Completed();
    private int _completed;
    private int _failed;
    private DateTimeOffset? _runStarted;

    public event Action? Changed;

    public bool IsPaused
    {
        get { lock (_lock) return !_resumed.Task.IsCompleted; }
    }

    /// <summary>The chat client every AI caller uses: waits while paused before each request.</summary>
    public IChatClient? Chat => runtime.ChatClient is not null ? new GatedChatClient(new RuntimeProbe(runtime), this) : null;

    private sealed class RuntimeProbe(AiRuntime runtime) : IChatServerProbe
    {
        public IChatClient? Client => runtime.ChatClient;
        public Uri? Endpoint => runtime.Chat.Endpoint;
        public AiServerState State => runtime.Chat.State;
    }

    public void Pause()
    {
        lock (_lock)
            if (_resumed.Task.IsCompleted) _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Changed?.Invoke();
    }

    public void Resume()
    {
        TaskCompletionSource gate;
        lock (_lock) gate = _resumed;
        gate.TrySetResult();
        Changed?.Invoke();
    }

    /// <summary>Completes immediately unless paused; otherwise when resumed.</summary>
    public Task WaitWhilePausedAsync(CancellationToken ct)
    {
        Task gate;
        lock (_lock) gate = _resumed.Task;
        return gate.IsCompleted ? Task.CompletedTask : gate.WaitAsync(ct);
    }

    /// <summary>Called by enrichment once per asset (not per request).</summary>
    public void RecordItem(bool success)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            _runStarted ??= now;
            if (success) _completed++; else _failed++;
            _recent.Enqueue(now);
            Trim(now);
        }
        Changed?.Invoke();
    }

    /// <summary>Starts a new run: resets the completed/failed counts and the rate.</summary>
    public void ResetRun()
    {
        lock (_lock)
        {
            _completed = _failed = 0;
            _recent.Clear();
            _runStarted = null;
        }
        Changed?.Invoke();
    }

    /// <summary>Items per minute over the last 5 minutes (or since the run started, if that is shorter).</summary>
    public double ItemsPerMinute
    {
        get
        {
            lock (_lock)
            {
                var now = _time.GetUtcNow();
                Trim(now);
                if (_recent.Count == 0 || _runStarted is not { } start) return 0;
                var window = now - start < RateWindow ? now - start : RateWindow;
                return window <= TimeSpan.Zero ? 0 : _recent.Count / window.TotalMinutes;
            }
        }
    }

    public async Task<int> PendingCountAsync(CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        return await db.AllAssets.CountAsync(a => a.AiStatus == AiStatus.Pending, ct);
    }

    public async Task<AiQueueStatus> StatusAsync(CancellationToken ct = default)
    {
        var pending = await PendingCountAsync(ct);
        var rate = ItemsPerMinute;
        int completed, failed;
        lock (_lock) (completed, failed) = (_completed, _failed);
        TimeSpan? drain = pending == 0 ? TimeSpan.Zero : rate > 0 ? TimeSpan.FromMinutes(pending / rate) : null;
        return new AiQueueStatus(pending, IsPaused, rate, drain, completed, failed);
    }

    /// <summary>Cancels running and queued AI jobs and takes the waiting assets out of the queue ("not enriched").</summary>
    public async Task CancelAsync(CancellationToken ct = default)
    {
        foreach (var job in hub.Active.Where(j => j.Type.StartsWith(JobTypePrefix)))
            await jobs.CancelAsync(job.Id, ct);
        await using (var db = catalog.CreateDbContext())
            await db.AllAssets.Where(a => a.AiStatus == AiStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.AiStatus, AiStatus.NotEnriched), ct);
        Resume();
        catalog.NotifyContentChanged();
    }

    private void Trim(DateTimeOffset now)
    {
        while (_recent.Count > 0 && now - _recent.Peek() >= RateWindow) _recent.Dequeue();
    }

    private static TaskCompletionSource Completed()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }
}

/// <summary>The chat server as a <see cref="GatedChatClient"/> sees it: its current client, endpoint and state.</summary>
public interface IChatServerProbe
{
    IChatClient? Client { get; }
    Uri? Endpoint { get; }
    AiServerState State { get; }
}

/// <summary>
/// Waits on the AI queue's pause gate before every request, and survives a restart of the chat server (a settings
/// change, a crash): each request goes to the server running now, and a request that cannot reach it waits for the
/// restarted server (up to <see cref="RestartTimeout"/>) and is sent again.
/// </summary>
public sealed class GatedChatClient : IChatClient
{
    private readonly AiQueue _queue;
    private readonly IChatServerProbe? _server;
    private readonly IChatClient? _fixed;

    /// <summary>A fixed client (tests).</summary>
    public GatedChatClient(IChatClient inner, AiQueue queue) => (_fixed, _queue) = (inner, queue);

    public GatedChatClient(IChatServerProbe server, AiQueue queue) => (_server, _queue) = (server, queue);

    public TimeSpan RestartTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a server that still looks ready on the old endpoint may take to show it is restarting.</summary>
    public TimeSpan RestartNoticeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        for (var attempt = 1; ; attempt++)
        {
            await _queue.WaitWhilePausedAsync(ct);
            var (client, endpoint) = await CurrentAsync(ct);
            try
            {
                return await client.GetResponseAsync(list, options, ct);
            }
            catch (Exception e) when (_server is not null && attempt < 4 && IsUnreachable(e))
            {
                if (!await RestartedAsync(endpoint, ct)) throw;
            }
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        await _queue.WaitWhilePausedAsync(ct);
        var (client, _) = await CurrentAsync(ct);
        await foreach (var u in client.GetStreamingResponseAsync(messages, options, ct)) yield return u;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : (_fixed ?? _server?.Client)?.GetService(serviceType, serviceKey);

    public void Dispose() => _fixed?.Dispose();

    /// <summary>The client for the server running now; waits while it (re)starts.</summary>
    private async Task<(IChatClient Client, Uri? Endpoint)> CurrentAsync(CancellationToken ct)
    {
        if (_fixed is not null) return (_fixed, null);
        var deadline = DateTime.UtcNow + RestartTimeout;
        while (_server!.State == AiServerState.Starting && DateTime.UtcNow < deadline) await Task.Delay(250, ct);
        var endpoint = _server.Endpoint;
        return (_server.Client ?? throw new HttpRequestException($"The AI server is not running ({_server.State})."), endpoint);
    }

    /// <summary>After a failed request: true once a server is ready on a new endpoint, false when it stays down or unchanged.</summary>
    private async Task<bool> RestartedAsync(Uri? failedEndpoint, CancellationToken ct)
    {
        var notice = DateTime.UtcNow + RestartNoticeTimeout;
        var deadline = DateTime.UtcNow + RestartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var state = _server!.State;
            if (state == AiServerState.Ready && _server.Endpoint != failedEndpoint) return true;
            if (state is AiServerState.Failed or AiServerState.NotConfigured or AiServerState.Stopped) return false;
            if (state == AiServerState.Ready && DateTime.UtcNow > notice) return false; // same server, really unreachable
            await Task.Delay(250, ct);
        }
        return false;
    }

    private static bool IsUnreachable(Exception e) => e switch
    {
        HttpRequestException => true,
        AggregateException a => a.InnerExceptions.All(IsUnreachable),
        System.ClientModel.ClientResultException { Status: 0 } => true,
        _ => e.InnerException is { } inner && IsUnreachable(inner),
    };
}
