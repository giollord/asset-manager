using AssetManager.Core.Domain;
using AssetManager.Core.Settings;
using AssetManager.Data;
using AssetManager.Data.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Data;

/// <summary>Spec "Catalog backups", "Backup schedule and retention", "Restore" (design D12).</summary>
public class CatalogBackupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (CatalogBackupService Service, JsonAppSettingsStore Settings) Create(TestCatalog c, Func<DateTime>? clock = null, Func<string, long?>? free = null)
    {
        var settings = new JsonAppSettingsStore(Path.Combine(c.Dir.Path, "settings.json"));
        settings.Update(s => s.BackupFolder = Path.Combine(c.Dir.Path, "backups"));
        return (new CatalogBackupService(c.Store, settings)
        {
            Clock = clock ?? (() => DateTime.Now),
            FreeSpace = free ?? (_ => long.MaxValue),
        }, settings);
    }

    private static async Task<long> ScalarAsync(string file, string sql)
    {
        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct));
    }

    /// <summary>Scenario "Backup during enrichment": each write adds an "a" and a "b" tag in one transaction.</summary>
    [Fact]
    public async Task Backup_during_enrichment_is_a_consistent_snapshot()
    {
        await using var c = await TestCatalog.CreateAsync();
        var source = await TestData.SourceAsync(c, 1);
        await using (var db = c.Db())
        {
            // Enough data that the copy takes a moment while the writer runs.
            var asset = await db.Assets.FirstAsync(Ct);
            for (var i = 0; i < 40; i++)
                db.Previews.Add(new PreviewBlob { AssetId = asset.Id, Kind = PreviewKind.ModelView, Index = i, Mime = "image/png", Bytes = new byte[256 * 1024] });
            await db.SaveChangesAsync(Ct);
        }
        var (backups, _) = Create(c);

        using var stop = new CancellationTokenSource();
        var writes = 0;
        var writer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await using var db = c.Db();
                await using var tx = await db.Database.BeginTransactionAsync(Ct);
                db.Tags.AddRange(new Tag { Name = $"a{writes}" }, new Tag { Name = $"b{writes}" });
                await db.SaveChangesAsync(Ct);
                await tx.CommitAsync(Ct);
                writes++;
            }
        }, Ct);
        while (writes < 5) await Task.Delay(10, Ct);

        var backup = await backups.BackupAsync(manual: false, Ct);
        await stop.CancelAsync();
        await writer;

        Assert.False(backup.Manual);
        Assert.Matches(@"^catalog-\d{8}-\d{6}-auto\.amdb$", backup.FileName);
        Assert.Equal("ok", await Task.Run(async () =>
        {
            await using var conn = new SqliteConnection($"Data Source={backup.Path};Mode=ReadOnly;Pooling=False");
            await conn.OpenAsync(Ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            return (string)(await cmd.ExecuteScalarAsync(Ct))!;
        }, Ct));
        var a = await ScalarAsync(backup.Path, "SELECT COUNT(*) FROM Tags WHERE Name LIKE 'a%'");
        var b = await ScalarAsync(backup.Path, "SELECT COUNT(*) FROM Tags WHERE Name LIKE 'b%'");
        Assert.Equal(a, b); // never half a transaction
        Assert.True(a >= 5);
        Assert.Equal(40, await ScalarAsync(backup.Path, "SELECT COUNT(*) FROM Previews"));
        Assert.Equal(source.Id, await ScalarAsync(backup.Path, "SELECT Id FROM Sources"));

        // The backup is a catalog that opens on its own.
        var opened = new CatalogStore();
        await opened.OpenAsync(backup.Path, Ct);
        await using (var db = opened.CreateDbContext())
            Assert.Equal(1, await db.Assets.CountAsync(Ct));
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Retention_keeps_manual_backups()
    {
        await using var c = await TestCatalog.CreateAsync();
        var now = new DateTime(2026, 11, 20, 9, 0, 0);
        var (backups, _) = Create(c, () => now);
        await backups.BackupAsync(manual: true, Ct);
        now = now.AddMinutes(1);
        await backups.BackupAsync(manual: true, Ct);
        var autos = new List<CatalogBackup>();
        for (var i = 0; i < 6; i++)
        {
            now = now.AddDays(1);
            autos.Add(await backups.BackupAsync(manual: false, Ct));
        }

        var left = backups.List();
        Assert.Equal(2, left.Count(b => b.Manual));
        Assert.Equal(autos.Skip(1).Select(b => b.FileName).Reverse(), left.Where(b => !b.Manual).Select(b => b.FileName));
        Assert.False(File.Exists(autos[0].Path)); // the oldest automatic one went
        Assert.Equal(new DateTime(2026, 11, 26, 9, 1, 0), left[0].CreatedAt);
    }

    [Fact]
    public async Task Two_backups_in_the_same_second_get_distinct_names()
    {
        await using var c = await TestCatalog.CreateAsync();
        var (backups, _) = Create(c, () => new DateTime(2026, 11, 20, 9, 0, 0));
        var first = await backups.BackupAsync(manual: true, Ct);
        var second = await backups.BackupAsync(manual: true, Ct);
        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal(2, backups.List().Count);
    }

    /// <summary>Scenario "Not enough space": the message names both sizes and no partial file is left.</summary>
    [Fact]
    public async Task Not_enough_space_leaves_no_file()
    {
        await using var c = await TestCatalog.CreateAsync();
        var (backups, _) = Create(c, free: _ => 1024);

        var e = await Assert.ThrowsAsync<IOException>(() => backups.BackupAsync(manual: false, Ct));

        Assert.Contains("needs", e.Message);
        Assert.Contains("1 KB is free", e.Message);
        Assert.Empty(Directory.GetFiles(backups.Folder));
    }

    [Fact]
    public async Task A_failed_backup_leaves_no_partial_file()
    {
        await using var c = await TestCatalog.CreateAsync();
        var (backups, _) = Create(c, () => new DateTime(2026, 11, 20, 9, 0, 0));
        // A folder holds the backup's final name, so the rename after the copy fails.
        var taken = Directory.CreateDirectory(Path.Combine(backups.Folder, "catalog-20261120-090000-auto.amdb"));

        await Assert.ThrowsAnyAsync<IOException>(() => backups.BackupAsync(manual: false, Ct));

        Assert.Empty(Directory.GetFiles(backups.Folder));
        Assert.True(taken.Exists);
    }

    [Theory]
    [InlineData(BackupSchedule.Daily, 23, false)]
    [InlineData(BackupSchedule.Daily, 25, true)]
    [InlineData(BackupSchedule.Weekly, 25, false)]
    [InlineData(BackupSchedule.Weekly, 24 * 7 + 1, true)]
    [InlineData(BackupSchedule.Off, 24 * 30, false)]
    public async Task Schedule(BackupSchedule schedule, int hoursSinceLast, bool due)
    {
        await using var c = await TestCatalog.CreateAsync();
        var now = new DateTime(2026, 11, 20, 9, 0, 0);
        var (backups, settings) = Create(c, () => now);
        settings.Update(s => s.BackupSchedule = schedule);
        Assert.Equal(schedule != BackupSchedule.Off, backups.IsDue()); // no backup yet
        await backups.BackupAsync(manual: true, Ct);
        now = now.AddHours(hoursSinceLast);
        Assert.Equal(due, backups.IsDue());
    }

    private sealed class BlockingJob : AssetManager.Core.Jobs.IJobHandler
    {
        public string Type => "test.block";

        public AssetManager.Core.Jobs.JobPool Pool => AssetManager.Core.Jobs.JobPool.Io;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunAsync(AssetManager.Core.Jobs.JobContext context, CancellationToken ct)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    /// <summary>Scenario "Restore yesterday's catalog".</summary>
    [Fact]
    public async Task Restore_yesterdays_catalog()
    {
        await using var c = await TestCatalog.CreateAsync();
        var source = await TestData.SourceAsync(c, 3, s => s.DisplayName = "Yesterday");
        await TestData.TagAsync(c, source.Assets[0].Id, "barrel");
        await using (var db = c.Db())
        {
            db.Previews.Add(new PreviewBlob { AssetId = source.Assets[0].Id, Kind = PreviewKind.Thumbnail, Mime = "image/png", Bytes = [1, 2, 3] });
            await db.SaveChangesAsync(Ct);
        }
        var now = new DateTime(2026, 11, 24, 9, 0, 0);
        var settings = new JsonAppSettingsStore(Path.Combine(c.Dir.Path, "settings.json"));
        settings.Update(s => s.BackupFolder = Path.Combine(c.Dir.Path, "backups"));
        var hub = new AssetManager.Data.Jobs.ProgressHub();
        var blocking = new BlockingJob();
        await using var jobs = new AssetManager.Data.Jobs.JobQueue(c.Store, [blocking], hub);
        await jobs.StartAsync(Ct);
        var backups = new CatalogBackupService(c.Store, settings, jobs, hub) { Clock = () => now, FreeSpace = _ => long.MaxValue };
        var yesterday = await backups.BackupAsync(manual: false, Ct);

        // Today: another source, a tag removed, and a job still running.
        now = now.AddDays(1);
        await TestData.SourceAsync(c, 5, s => s.DisplayName = "Today");
        await using (var db = c.Db()) await db.AssetTags.ExecuteDeleteAsync(Ct);
        var running = await jobs.EnqueueAsync(blocking.Type, "Long job", new { }, ct: Ct);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var opened = 0;
        c.Store.Opened += () => opened++;
        AssetManager.Core.Domain.JobState? stopped = null;
        hub.Finished += j => { if (j.Id == running) stopped = j.State; };

        var before = await backups.RestoreAsync(yesterday.Path, Ct);

        Assert.Equal(AssetManager.Core.Domain.JobState.Cancelled, stopped); // stopped before the catalog was replaced
        Assert.True(before.Manual);
        Assert.Equal(1, opened);
        await using (var db = c.Db())
        {
            Assert.Equal(["Yesterday"], await db.Sources.Select(s => s.DisplayName).ToListAsync(Ct));
            Assert.Equal(3, await db.Assets.CountAsync(Ct));
            Assert.Equal(["barrel"], await db.AssetTags.Select(t => t.Tag.Name).ToListAsync(Ct));
            Assert.Equal([1, 2, 3], (await db.Previews.SingleAsync(Ct)).Bytes);
            // The catalog is writable and in WAL mode again.
            db.Tags.Add(new Tag { Name = "after restore" });
            await db.SaveChangesAsync(Ct);
        }
        Assert.Equal("wal", (await ScalarTextAsync(c.Path, "PRAGMA journal_mode;")).ToLowerInvariant());
        // The catalog as it was before the restore is kept as a manual backup.
        Assert.Equal(8, await ScalarAsync(before.Path, "SELECT COUNT(*) FROM Assets"));
        Assert.Equal([before.Path, yesterday.Path], backups.List().Select(b => b.Path));
    }

    private static async Task<string> ScalarTextAsync(string file, string sql)
    {
        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (string)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task Default_folder_is_backups_in_the_data_folder()
    {
        await using var c = await TestCatalog.CreateAsync();
        var service = new CatalogBackupService(c.Store, new JsonAppSettingsStore(Path.Combine(c.Dir.Path, "settings.json")));
        Assert.Equal(AppPaths.Backups, service.Folder);
        Assert.Equal(Path.Combine(AppPaths.Root, "backups"), service.Folder);
    }
}
