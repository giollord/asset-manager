using Microsoft.Extensions.Logging;

namespace AssetManager.Core;

/// <summary>Work run once when the app starts (open catalog, resume jobs, start AI host …). Lower order runs first.</summary>
public interface IStartupTask
{
    int Order { get; }
    Task RunAsync(CancellationToken ct);
}

/// <summary>
/// Runs all <see cref="IStartupTask"/>s exactly once. Both hosts trigger it from the layout, because MAUI has no
/// generic-host IHostedService pipeline.
/// </summary>
public sealed class AppStartup(IEnumerable<IStartupTask> tasks, ILogger<AppStartup> logger)
{
    private readonly Lock _lock = new();
    private Task? _run;

    public Exception? Error { get; private set; }

    /// <summary>
    /// Starts the tasks once, on the thread pool: they open the catalog and run SQLite queries, which execute
    /// synchronously even when awaited, so on the UI thread a slow drive would keep the window frozen (design D13).
    /// </summary>
    public Task EnsureStartedAsync()
    {
        lock (_lock) return _run ??= Task.Run(RunAllAsync);
    }

    private async Task RunAllAsync()
    {
        foreach (var task in tasks.OrderBy(t => t.Order))
        {
            try
            {
                await task.RunAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Startup task {Task} failed", task.GetType().Name);
                Error ??= ex;
            }
        }
    }
}
