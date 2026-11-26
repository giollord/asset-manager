namespace AssetManager.Core;

/// <summary>
/// Points in the app's start that background work waits for (design D12). The window is ready once the layout has
/// rendered (<see cref="SignalWindowReady"/>) and the first page has loaded its data (<see cref="FirstLoad"/>). The AI
/// runtime starts after that, and interrupted jobs after the AI, so a cold start from a slow drive shows the catalog
/// before they compete for the disk.
/// </summary>
public sealed class StartupSignals
{
    private readonly TaskCompletionSource _windowReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _lock = new();
    private bool _layoutRendered;
    private int _firstLoads;

    /// <summary>Completes when the main window shows the first page with its data.</summary>
    public Task WindowReady => _windowReady.Task;

    /// <summary>The layout has rendered; ready as soon as no first-page load is still running.</summary>
    public void SignalWindowReady()
    {
        lock (_lock)
        {
            _layoutRendered = true;
            TryComplete();
        }
    }

    /// <summary>
    /// Called by a page when it starts loading its data, before its first await: the window is not ready until the
    /// returned scope is disposed. After the window is ready this does nothing.
    /// </summary>
    public FirstLoadScope FirstLoad()
    {
        lock (_lock)
        {
            if (_windowReady.Task.IsCompleted) return default;
            _firstLoads++;
            return new FirstLoadScope(this);
        }
    }

    private void EndFirstLoad()
    {
        lock (_lock)
        {
            _firstLoads--;
            TryComplete();
        }
    }

    private void TryComplete()
    {
        if (_layoutRendered && _firstLoads <= 0) _windowReady.TrySetResult();
    }

    public readonly struct FirstLoadScope(StartupSignals? owner) : IDisposable
    {
        public void Dispose() => owner?.EndFirstLoad();
    }

    private readonly TaskCompletionSource _aiSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the AI runtime's first start has finished (ready, failed or nothing configured).</summary>
    public Task AiSettled => _aiSettled.Task;

    public void SignalAiSettled() => _aiSettled.TrySetResult();

    /// <summary>
    /// Waits until the window is ready (or <paramref name="fallback"/> has passed, for hosts that never signal), then
    /// <paramref name="settle"/> more, so the first page's own loading finishes undisturbed.
    /// </summary>
    public async Task WaitForWindowAsync(TimeSpan settle, TimeSpan fallback, TimeProvider? time = null, CancellationToken ct = default)
    {
        time ??= TimeProvider.System;
        await Task.WhenAny(WindowReady, Task.Delay(fallback, time, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (settle > TimeSpan.Zero) await Task.Delay(settle, time, ct).ConfigureAwait(false);
    }
}
