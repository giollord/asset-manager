using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetManager.Core.Settings;

public enum ThemeMode { System, Light, Dark }

public enum AiAcceleration { Auto, Gpu, Cpu }

/// <summary>When an automatic catalog backup is made at startup (spec "Backup schedule and retention").</summary>
public enum BackupSchedule { Off, Daily, Weekly }

/// <summary>
/// App-level settings that cannot live inside the catalog (the catalog path itself, theme, model paths).
/// Persisted as JSON in the app data folder (<see cref="AppPaths.SettingsFile"/>).
/// </summary>
public sealed class AppSettings
{
    public string CatalogPath { get; set; } = Path.Combine(AppPaths.Root, "catalog.amdb");
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public string? ChatModelPath { get; set; }
    public string? MmprojPath { get; set; }
    public string? EmbeddingModelPath { get; set; }
    public AiAcceleration Acceleration { get; set; } = AiAcceleration.Auto;
    /// <summary>
    /// How many AI requests run at once (llama-server slots, 1-4). Several requests share each pass over the model, so a
    /// GPU produces more answers per second; each slot needs its own context memory.
    /// </summary>
    public int AiParallelRequests { get; set; } = 3;

    /// <summary>The AI depth a new import starts with (the import wizard can change it per import).</summary>
    public Domain.AiDepth DefaultAiDepth { get; set; } = Domain.AiDepth.Full;

    /// <summary>
    /// Load the AI models right after the window is ready. Off by default: they load when first needed (an AI import
    /// step, semantic or image search), so a session without AI work never spends the time, memory and disk on them.
    /// </summary>
    public bool AiLoadAtStartup { get; set; }

    /// <summary>Extraction cache size limit in MB; least-recently-used entries are evicted above it.</summary>
    public long CacheMaxSizeMb { get; set; } = 4 * 1024;

    /// <summary>
    /// Where files extracted from archives and packages go; null means <see cref="AppPaths.DefaultCache"/> on the system
    /// drive. Takes effect at the next start.
    /// </summary>
    public string? CacheFolder { get; set; }

    /// <summary>Cache entries not used for this many days are evicted.</summary>
    public int CacheMaxAgeDays { get; set; } = 14;

    /// <summary>How many container levels (archive → package → …) are opened when importing.</summary>
    public int MaxNestingDepth { get; set; } = 5;

    /// <summary>Run the AI tag consensus step (source tags from tags most assets share) after the item pass (design D2).</summary>
    public bool ConsensusEnabled { get; set; } = true;

    /// <summary>Share of a source's game assets (0-1) a tag or style needs to be a consensus candidate.</summary>
    public double ConsensusShare { get; set; } = 0.6;

    /// <summary>Sources with fewer game assets get no consensus step.</summary>
    public int ConsensusMinAssets { get; set; } = 5;

    /// <summary>Let the AI choose textures for model materials that have none (design D20).</summary>
    public bool TextureMatchEnabled { get; set; } = true;

    /// <summary>AI texture choices at or above this confidence are attached and the previews re-rendered.</summary>
    public double TextureMatchThreshold { get; set; } = 0.7;

    /// <summary>AI texture choices at or above this confidence (and below the match threshold) are offered as suggestions.</summary>
    public double TextureSuggestThreshold { get; set; } = 0.4;

    /// <summary>
    /// Folders where new packs appear (design D11), as catalog locations (<c>Location.ToString()</c>: a volume id and a
    /// path on it), so a drive that gets another letter still matches.
    /// </summary>
    public List<string> LibraryFolders { get; set; } = [];

    /// <summary>Rescan the sources and scan the library folders 30 s after startup; findings are only reported.</summary>
    public bool CheckForChangesAtStartup { get; set; }

    /// <summary>Where catalog backups go; null means <c>backups</c> in the data folder (design D12).</summary>
    public string? BackupFolder { get; set; }

    /// <summary>At startup, back up the catalog when the last backup is older than a day or a week.</summary>
    public BackupSchedule BackupSchedule { get; set; } = BackupSchedule.Daily;

    /// <summary>How many automatic backups are kept; manual ones are never deleted automatically.</summary>
    public int BackupKeep { get; set; } = 5;

    /// <summary>Ask GitHub Releases for a newer version at most once a day (design D13). Off by default.</summary>
    public bool UpdateCheckEnabled { get; set; }

    /// <summary>When the update check last ran (UTC), successful or not.</summary>
    public DateTime? LastUpdateCheck { get; set; }

    /// <summary>Whether the last update check failed (shown as a note in About, never as an error).</summary>
    public bool LastCheckFailed { get; set; }

    /// <summary>The latest release the last successful check found, and its page.</summary>
    public string? LatestVersion { get; set; }

    public string? LatestReleaseUrl { get; set; }

    /// <summary>The newest version a notification was shown for, so each new version is announced once.</summary>
    public string? LastSeenVersion { get; set; }
}

/// <summary>
/// Well-known app data folders, all under <see cref="Root"/>. The host decides the root once at startup with
/// <see cref="Use"/>, before logging, settings and services; the default is the user profile.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// The folder that holds the app's exe and what ships beside it (the <c>portable</c> marker, <c>llama/</c>,
    /// <c>ffmpeg/</c>). In a single-file build <see cref="AppContext.BaseDirectory"/> is the folder the bundle was
    /// extracted to, not this one (design D1).
    /// </summary>
    public static string InstallFolder { get; } = ResolveInstallFolder();

    private static string ResolveInstallFolder()
    {
        var exe = Environment.ProcessPath;
        // Run as "dotnet app.dll" the process is the dotnet host; its folder is not the app's.
        if (exe is null || Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return AppContext.BaseDirectory;
        return Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
    }

    public static AppDataLocation Location { get; private set; } = AppDataLocation.Default;

    public static string Root { get; set; } = AppDataLocation.Default.Root;

    /// <summary>The extraction cache inside the data folder, where older versions kept it.</summary>
    public static string Cache => Path.Combine(Root, "cache");

    /// <summary>
    /// The default extraction cache: on the system drive (usually an internal SSD), even for a portable copy. Unpacking
    /// an archive writes thousands of small files; on a USB hard drive that is ~10 files/s with the drive 100% busy, on
    /// an SSD it is instant. The cache is disposable, so keeping it off the portable folder loses nothing
    /// (fast-portable-startup design D14).
    /// </summary>
    public static string DefaultCache =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetManager", "cache");

    /// <summary>The cache folder in use for these settings.</summary>
    public static string CacheFor(AppSettings settings) => string.IsNullOrWhiteSpace(settings.CacheFolder) ? DefaultCache : settings.CacheFolder;
    public static string Models => Path.Combine(Root, "models");
    /// <summary>Short-lived work files (ffmpeg outputs, clipboard exports): see <see cref="TempFor"/>.</summary>
    public static string Temp => TempFor(Location with { Root = Root });

    /// <summary>
    /// The temp folder for a data location. A portable copy keeps it on the system drive, like the extraction cache
    /// (<see cref="DefaultCache"/>): every audio and video preview writes and reads files there, which is slow on a USB
    /// hard drive, and nothing in it outlives the app. Other locations keep it in their data folder, so an
    /// overridden data folder (tests, a second instance) stays self-contained.
    /// </summary>
    public static string TempFor(AppDataLocation location) =>
        Path.Combine(location.Mode == DataMode.Portable ? AppDataLocation.DevelopmentRoot : location.Root, "temp");
    /// <summary>Files staged for the clipboard or other short-lived exports.</summary>
    public static string Export => Path.Combine(Temp, "export");
    public static string Logs => Path.Combine(Root, "logs");
    /// <summary>The default catalog backup folder.</summary>
    public static string Backups => Path.Combine(Root, "backups");
    /// <summary>The embedded browser's (WebView2) profile.</summary>
    public static string WebView => Path.Combine(Root, "webview");
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static void Use(AppDataLocation location)
    {
        Location = location;
        Root = location.Root;
    }
}

public interface IAppSettingsStore
{
    AppSettings Current { get; }
    event Action<AppSettings>? Changed;
    void Update(Action<AppSettings> change);
}

public sealed class JsonAppSettingsStore : IAppSettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Lock _lock = new();

    public JsonAppSettingsStore(string? path = null)
    {
        _path = path ?? AppPaths.SettingsFile;
        Current = Load(_path);
        if (!File.Exists(_path)) TrySave(_path, Current); // the data folder shows its settings from the first start
    }

    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? Changed;

    public void Update(Action<AppSettings> change)
    {
        lock (_lock)
        {
            change(Current);
            Save(_path, Current);
        }
        Changed?.Invoke(Current);
    }

    /// <summary>
    /// Reads the settings; paths stored relative to the settings file's folder (the data folder) are resolved
    /// against where that folder is now (spec "Relocatable settings").
    /// </summary>
    public static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
            MapPaths(settings, p => Resolve(p, RootOf(path)));
            return settings;
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Writes the settings; paths inside the settings file's folder are stored relative to it.</summary>
    public static void Save(string path, AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, Json), Json)!;
        MapPaths(stored, p => Relativize(p, RootOf(path)));
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(stored, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary><paramref name="path"/> relative to <paramref name="root"/> when strictly inside it, else unchanged.</summary>
    public static string Relativize(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return path;
        var full = Path.GetFullPath(path);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && full.Length > prefix.Length
            ? Path.GetRelativePath(prefix, full)
            : path;
    }

    /// <summary>A relative <paramref name="path"/> resolved against <paramref name="root"/>; absolute paths unchanged.</summary>
    public static string Resolve(string path, string root) =>
        string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(Path.Combine(root, path));

    private static string RootOf(string settingsPath) => Path.GetDirectoryName(Path.GetFullPath(settingsPath))!;

    private static void MapPaths(AppSettings s, Func<string, string> map)
    {
        s.CatalogPath = map(s.CatalogPath);
        if (s.ChatModelPath is { } chat) s.ChatModelPath = map(chat);
        if (s.MmprojPath is { } mmproj) s.MmprojPath = map(mmproj);
        if (s.EmbeddingModelPath is { } embedding) s.EmbeddingModelPath = map(embedding);
        if (s.BackupFolder is { } backups) s.BackupFolder = map(backups);
        if (s.CacheFolder is { } cache) s.CacheFolder = map(cache);
    }

    private static void TrySave(string path, AppSettings settings)
    {
        try { Save(path, settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // read-only: defaults still apply
    }
}
