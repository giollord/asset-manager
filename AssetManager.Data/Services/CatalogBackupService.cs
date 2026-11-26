using System.Globalization;
using System.Text.RegularExpressions;
using AssetManager.Core;
using AssetManager.Core.Jobs;
using AssetManager.Core.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetManager.Data.Services;

/// <summary>A backup file of the open catalog; <see cref="CreatedAt"/> is local time, from the file name.</summary>
public sealed record CatalogBackup(string Path, DateTime CreatedAt, long Size, bool Manual)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Consistent backups of the open catalog (design D12, spec "Catalog backups"): SQLite's online backup copies the
/// catalog as of one point in time while jobs keep writing, into <c>&lt;name&gt;-yyyyMMdd-HHmmss-auto|manual.amdb.tmp</c>,
/// which is renamed once complete, so a failure never leaves a partial backup. Only automatic backups are pruned.
/// </summary>
public sealed partial class CatalogBackupService(
    CatalogStore catalog, IAppSettingsStore settings, IJobQueue? jobs = null, IProgressHub? hub = null, ILogger<CatalogBackupService>? logger = null)
{
    private readonly ILogger _log = (ILogger?)logger ?? NullLogger.Instance;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The current time (local); replaceable in tests.</summary>
    public Func<DateTime> Clock { get; init; } = () => DateTime.Now;

    /// <summary>Free bytes on the drive of a folder, or null when unknown; replaceable in tests.</summary>
    public Func<string, long?> FreeSpace { get; init; } = folder =>
    {
        try { return new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(folder))!).AvailableFreeSpace; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    };

    public string Folder => string.IsNullOrWhiteSpace(settings.Current.BackupFolder) ? AppPaths.Backups : settings.Current.BackupFolder;

    private string CatalogPath => catalog.Path ?? throw new InvalidOperationException("No catalog is open.");

    private string CatalogName => System.IO.Path.GetFileNameWithoutExtension(CatalogPath);

    [GeneratedRegex(@"^(?<name>.+)-(?<stamp>\d{8}-\d{6})(-\d+)?-(?<kind>auto|manual)\.amdb$", RegexOptions.IgnoreCase)]
    private static partial Regex BackupName();

    /// <summary>The open catalog's backups, newest first.</summary>
    public IReadOnlyList<CatalogBackup> List()
    {
        if (catalog.Path is null || !Directory.Exists(Folder)) return [];
        var result = new List<CatalogBackup>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*" + CatalogLocationService.Extension))
        {
            var m = BackupName().Match(System.IO.Path.GetFileName(file));
            if (!m.Success || !string.Equals(m.Groups["name"].Value, CatalogName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!DateTime.TryParseExact(m.Groups["stamp"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) continue;
            result.Add(new CatalogBackup(file, at, new FileInfo(file).Length, m.Groups["kind"].Value.Equals("manual", StringComparison.OrdinalIgnoreCase)));
        }
        return result.OrderByDescending(b => b.CreatedAt).ThenByDescending(b => b.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Whether the schedule calls for an automatic backup now (the last backup of any kind is too old).</summary>
    public bool IsDue()
    {
        var age = settings.Current.BackupSchedule switch
        {
            BackupSchedule.Daily => TimeSpan.FromDays(1),
            BackupSchedule.Weekly => TimeSpan.FromDays(7),
            _ => (TimeSpan?)null,
        };
        if (age is null || catalog.Path is null) return false;
        return List().FirstOrDefault() is not { } last || Clock() - last.CreatedAt >= age;
    }

    /// <summary>Makes a backup of the open catalog and, for automatic ones, prunes the oldest beyond the limit.</summary>
    /// <exception cref="IOException">Not enough free space, or the copy failed; no backup file is left behind.</exception>
    public async Task<CatalogBackup> BackupAsync(bool manual, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var source = CatalogPath;
            var folder = Folder;
            Directory.CreateDirectory(folder);
            var needed = Size(source) + Size(source + "-wal");
            if (FreeSpace(folder) is { } free && free < needed)
                throw new IOException($"Not enough space for a catalog backup in {folder}: it needs {Bytes(needed)}, and {Bytes(free)} is free.");

            var target = NewPath(folder, manual);
            var tmp = target + ".tmp";
            try
            {
                await Task.Run(() =>
                {
                    using var from = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                    using var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp, Pooling = false }.ToString());
                    from.Open();
                    to.Open();
                    from.BackupDatabase(to); // one step under a read transaction: a snapshot as of one point in time
                    using var cmd = to.CreateCommand();
                    cmd.CommandText = "PRAGMA journal_mode=DELETE;"; // one self-contained file; opening it as a catalog turns WAL on again
                    cmd.ExecuteNonQuery();
                }, ct);
                File.Move(tmp, target);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
            _log.LogInformation("Catalog backed up to {Path}", target);
            if (!manual) Prune();
            return List().First(b => string.Equals(b.Path, target, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes the oldest automatic backups beyond <see cref="AppSettings.BackupKeep"/>; manual ones stay.</summary>
    public void Prune()
    {
        foreach (var old in List().Where(b => !b.Manual).Skip(Math.Max(1, settings.Current.BackupKeep)))
        {
            TryDelete(old.Path);
            _log.LogInformation("Deleted old catalog backup {Path}", old.Path);
        }
    }

    /// <summary>
    /// Restores a backup (spec "Restore"): cancels running jobs, saves the current catalog as a manual backup, then
    /// replaces the catalog file with the backup, removes the old WAL and shared-memory files and reopens it (migrating
    /// a backup from an older version).
    /// </summary>
    /// <returns>The manual backup of the catalog as it was before the restore.</returns>
    public async Task<CatalogBackup> RestoreAsync(string backupPath, CancellationToken ct = default)
    {
        if (!File.Exists(backupPath)) throw new FileNotFoundException("The backup file no longer exists.", backupPath);
        await CancelJobsAsync(ct);
        var before = await BackupAsync(manual: true, ct);
        var path = CatalogPath;
        await _gate.WaitAsync(ct);
        try
        {
            SqliteConnection.ClearAllPools();
            var incoming = path + ".restore.tmp";
            File.Copy(backupPath, incoming, overwrite: true);
            await RetryAsync(() => File.Move(incoming, path, overwrite: true), ct);
            foreach (var extra in new[] { path + "-wal", path + "-shm" })
                await RetryAsync(() => TryDelete(extra, rethrow: true), ct);
        }
        finally
        {
            _gate.Release();
        }
        await catalog.OpenAsync(path, ct);
        catalog.NotifyContentChanged();
        _log.LogInformation("Catalog {Path} restored from {Backup}", path, backupPath);
        return before;
    }

    private async Task CancelJobsAsync(CancellationToken ct)
    {
        if (jobs is null || hub is null) return;
        var active = hub.Active.Select(j => j.Id).ToList();
        foreach (var id in active) await jobs.CancelAsync(id, ct);
        foreach (var id in active)
        {
            try { await jobs.WaitAsync(id, ct).WaitAsync(TimeSpan.FromSeconds(30), ct); }
            catch (TimeoutException) { _log.LogWarning("Job {Job} did not stop before the restore", id); }
        }
    }

    /// <summary>A file still held by a closing connection is retried for a few seconds.</summary>
    private static async Task RetryAsync(Action action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                SqliteConnection.ClearAllPools();
                await Task.Delay(250, ct);
            }
        }
    }

    private string NewPath(string folder, bool manual)
    {
        var stem = $"{CatalogName}-{Clock():yyyyMMdd-HHmmss}";
        var kind = manual ? "manual" : "auto";
        var path = System.IO.Path.Combine(folder, $"{stem}-{kind}{CatalogLocationService.Extension}");
        for (var i = 2; File.Exists(path) || File.Exists(path + ".tmp"); i++)
            path = System.IO.Path.Combine(folder, $"{stem}-{i}-{kind}{CatalogLocationService.Extension}");
        return path;
    }

    private static long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static void TryDelete(string path, bool rethrow = false)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) when (!rethrow) { }
    }

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} bytes",
    };
}

/// <summary>Payload of "catalog.backup".</summary>
public sealed record BackupPayload(bool Manual);

/// <summary>Backs up the catalog in the background (spec "Backups SHALL run as background jobs").</summary>
public sealed class CatalogBackupJob(CatalogBackupService backups) : IJobHandler
{
    public const string JobType = "catalog.backup";

    public string Type => JobType;

    public JobPool Pool => JobPool.Io;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        context.Progress.SetStage("Backing up the catalog", 0, 1, 1);
        var backup = await backups.BackupAsync(context.Payload<BackupPayload>().Manual, ct);
        context.Progress.Advance(1, backup.FileName);
        context.Progress.SetSummary(new { size = backup.Size });
    }
}

/// <summary>The scheduled backup: after the catalog opened and the job queue started, when the last one is too old.</summary>
public sealed class BackupStartupTask(CatalogBackupService backups, IJobQueue jobs) : IStartupTask
{
    public int Order => 50;

    public async Task RunAsync(CancellationToken ct)
    {
        if (backups.IsDue()) await jobs.EnqueueAsync(CatalogBackupJob.JobType, "Back up the catalog", new BackupPayload(false), ct: ct);
    }
}
