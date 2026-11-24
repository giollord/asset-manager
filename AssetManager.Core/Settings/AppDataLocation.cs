namespace AssetManager.Core.Settings;

/// <summary>How the app's data folder was chosen (spec "app-data-location").</summary>
public enum DataMode
{
    /// <summary>Not a portable copy: the user profile (%LOCALAPPDATA%\AssetManager).</summary>
    Development,
    /// <summary>A portable copy: the <c>data</c> folder next to the exe.</summary>
    Portable,
    /// <summary>Set by the <see cref="AppDataLocation.OverrideVariable"/> environment variable.</summary>
    Override,
    /// <summary>A portable copy whose folder is not writable: the user profile, with a reason.</summary>
    Fallback,
}

/// <summary>The data folder in use, decided once at startup before logging, settings and services.</summary>
/// <param name="Root">The folder that holds the catalog, settings, models, cache, temp files, logs and browser profile.</param>
/// <param name="Reason">Why portable storage was not possible (fallback only).</param>
public sealed record AppDataLocation(string Root, DataMode Mode, string? Reason = null)
{
    /// <summary>Environment variable that sets the data folder for any build.</summary>
    public const string OverrideVariable = "ASSETMANAGER_DATA_DIR";

    /// <summary>File next to the exe that marks a portable copy; the portable publish adds it.</summary>
    public const string PortableMarker = "portable";

    /// <summary>Folder name of the portable data folder, next to the exe.</summary>
    public const string PortableFolder = "data";

    public static string DevelopmentRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetManager");

    public static AppDataLocation Default { get; } = new(DevelopmentRoot, DataMode.Development);

    /// <summary>
    /// Picks the data folder: the override variable; else, for a portable copy (marker file next to the exe), its
    /// <c>data</c> folder when it can be written; else the user profile.
    /// </summary>
    /// <param name="exeDir">Folder of the running exe.</param>
    /// <param name="environment">Reads an environment variable (tests replace it).</param>
    /// <param name="developmentRoot">The user-profile folder (tests replace it).</param>
    public static AppDataLocation Resolve(string exeDir, Func<string, string?>? environment = null, string? developmentRoot = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        developmentRoot ??= DevelopmentRoot;

        if (environment(OverrideVariable) is { Length: > 0 } custom)
        {
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(custom.Trim().Trim('"')));
            return TryWritable(full) is { } error
                ? new AppDataLocation(developmentRoot, DataMode.Fallback, $"The folder set in {OverrideVariable} ({full}) cannot be written: {error}")
                : new AppDataLocation(full, DataMode.Override);
        }

        if (!File.Exists(Path.Combine(exeDir, PortableMarker))) return new AppDataLocation(developmentRoot, DataMode.Development);

        var data = Path.Combine(exeDir, PortableFolder);
        return TryWritable(data) is { } reason
            ? new AppDataLocation(developmentRoot, DataMode.Fallback, $"The app folder cannot be written ({data}): {reason}")
            : new AppDataLocation(data, DataMode.Portable);
    }

    /// <summary>Creates the folder and writes and deletes a probe file; null when that works, else the error.</summary>
    private static string? TryWritable(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            return e.Message;
        }
    }
}
