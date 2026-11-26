using AssetManager.Core.Jobs;
using Microsoft.Extensions.Logging;

namespace AssetManager.Core.Platform;

/// <summary>
/// The single-file portable exe unpacks itself once per version into <c>%TEMP%\.net\AssetManager\&lt;hash&gt;</c>, and
/// .NET never removes older versions' folders (~240 MB each). After the window is up, this deletes the folders of
/// other versions (fast-portable-startup design D1). Folders in use by another running copy cannot be deleted and are
/// skipped.
/// </summary>
public sealed class SelfExtractCleanupStartupTask(StartupSignals? signals = null, ILogger<SelfExtractCleanupStartupTask>? logger = null) : IStartupTask
{
    public int Order => 40;

    public Task RunAsync(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            if (signals is not null) await signals.WaitForWindowAsync(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), ct: ct);
            using var background = BackgroundIo.Enter();
            try
            {
                var removed = DeleteOtherVersions(AppContext.BaseDirectory);
                if (removed > 0) logger?.LogInformation("Removed {Count} unpacked copies of older app versions", removed);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(e, "Could not remove unpacked copies of older app versions");
            }
        }, ct);
        return Task.CompletedTask;
    }

    /// <summary>
    /// When <paramref name="baseDirectory"/> is a self-extraction folder (<c>…\.net\&lt;app&gt;\&lt;hash&gt;</c>), deletes its
    /// sibling folders and returns how many; otherwise (a normal build) does nothing.
    /// </summary>
    public static int DeleteOtherVersions(string baseDirectory)
    {
        var current = new DirectoryInfo(Path.TrimEndingDirectorySeparator(baseDirectory));
        if (current.Parent is not { } app || app.Parent is not { Name: ".net" }) return 0;
        var removed = 0;
        foreach (var other in app.EnumerateDirectories())
        {
            if (string.Equals(other.FullName, current.FullName, StringComparison.OrdinalIgnoreCase)) continue;
            // Rename first: Windows refuses while any file inside is open, so a copy that is still running keeps all
            // its files instead of losing the ones it has not loaded yet. Leftovers of an earlier attempt end in ".old".
            var doomed = other.Name.EndsWith(".old", StringComparison.Ordinal) ? other.FullName : other.FullName + ".old";
            try
            {
                if (doomed != other.FullName) other.MoveTo(doomed);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue; // in use by another running copy
            }
            try
            {
                Directory.Delete(doomed, recursive: true);
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // retried at the next start
        }
        return removed;
    }
}
