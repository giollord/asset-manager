using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetManager.Core.Settings;

public enum ThemeMode { System, Light, Dark }

public enum AiAcceleration { Auto, Gpu, Cpu }

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

    /// <summary>Extraction cache size limit in MB; least-recently-used entries are evicted above it.</summary>
    public long CacheMaxSizeMb { get; set; } = 20 * 1024;

    /// <summary>Cache entries not used for this many days are evicted.</summary>
    public int CacheMaxAgeDays { get; set; } = 14;

    /// <summary>How many container levels (archive → package → …) are opened when importing.</summary>
    public int MaxNestingDepth { get; set; } = 5;
}

/// <summary>
/// Well-known app data folders, all under <see cref="Root"/>. The host decides the root once at startup with
/// <see cref="Use"/>, before logging, settings and services; the default is the user profile.
/// </summary>
public static class AppPaths
{
    public static AppDataLocation Location { get; private set; } = AppDataLocation.Default;

    public static string Root { get; set; } = AppDataLocation.Default.Root;

    public static string Cache => Path.Combine(Root, "cache");
    public static string Models => Path.Combine(Root, "models");
    public static string Temp => Path.Combine(Root, "temp");
    /// <summary>Files staged for the clipboard or other short-lived exports.</summary>
    public static string Export => Path.Combine(Temp, "export");
    public static string Logs => Path.Combine(Root, "logs");
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
    }

    private static void TrySave(string path, AppSettings settings)
    {
        try { Save(path, settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // read-only: defaults still apply
    }
}
