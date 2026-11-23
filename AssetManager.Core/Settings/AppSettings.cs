using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetManager.Core.Settings;

public enum ThemeMode { System, Light, Dark }

public enum AiAcceleration { Auto, Gpu, Cpu }

/// <summary>
/// App-level settings that cannot live inside the catalog (the catalog path itself, theme, model paths).
/// Persisted as JSON under %LOCALAPPDATA%\AssetManager.
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

/// <summary>Well-known app data folders.</summary>
public static class AppPaths
{
    public static string Root { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetManager");

    public static string Cache => Path.Combine(Root, "cache");
    public static string Models => Path.Combine(Root, "models");
    public static string Temp => Path.Combine(Root, "temp");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
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

    public static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public static void Save(string path, AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
