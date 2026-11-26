using System.ComponentModel.DataAnnotations;
using AssetManager.Data;
using AssetManager.Data.Services;

namespace AssetManager.Processing.Settings;

/// <summary>Which files are code/project content ("noted", summarized but not imported) or ignored entirely.</summary>
public sealed record ClassificationOptions : IValidatableObject
{
    public IReadOnlyList<string> ScriptExtensions { get; init; } =
    [
        ".cs", ".js", ".ts", ".jsx", ".tsx", ".py", ".lua", ".gd", ".rb", ".java", ".cpp", ".c", ".h", ".hpp",
        ".shader", ".hlsl", ".cginc", ".glsl", ".compute", ".shadergraph", ".shadersubgraph", ".usf", ".ush",
        ".asmdef", ".asmref", ".dll", ".so", ".dylib", ".pdb", ".lib", ".a", ".exe", ".jar",
        ".unity", ".asset", ".controller", ".overridecontroller", ".anim", ".mask", ".mixer", ".playable",
        ".preset", ".lighting", ".physicmaterial", ".physicsmaterial2d", ".rendertexture", ".spriteatlas", ".guiskin",
        ".fontsettings", ".flare", ".terrainlayer", ".signal", ".brush", ".giparams", ".inputactions", ".uss", ".uxml", ".tss",
        ".tscn", ".tres", ".godot", ".import", ".uasset", ".umap", ".uproject", ".uplugin",
        ".csproj", ".sln", ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".bat", ".cmd", ".sh", ".ps1",
    ];

    public IReadOnlyList<string> IgnoredExtensions { get; init; } =
        [".meta", ".tmp", ".temp", ".bak", ".lnk", ".log", ".db", ".ds_store", ".swp", ".crdownload", ".part"];

    /// <summary>
    /// Folders imports skip by default (spec "Ignored folders"): a name matches at any depth, a path with '/' from the
    /// root of the imported folder, archive or package; '*' wildcards. An import can add rules or turn these off.
    /// </summary>
    public IReadOnlyList<string> IgnoredFolders { get; init; } =
        ["node_modules", "bower_components", ".git", ".svn", ".hg", "__pycache__", ".venv", ".vs", ".vscode", ".idea", "__MACOSX"];

    /// <summary>File names that are ignored regardless of extension (case-insensitive).</summary>
    public IReadOnlyList<string> IgnoredNames { get; init; } = ["thumbs.db", "desktop.ini", ".ds_store", "__macosx"];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var e in ScriptExtensions.Concat(IgnoredExtensions))
            if (string.IsNullOrWhiteSpace(e) || !e.StartsWith('.') || e.Contains(' '))
                yield return new ValidationResult($"'{e}' is not an extension like '.cs'.");
        foreach (var f in IgnoredFolders)
            if (Containers.FolderIgnoreRules.Normalize(f).Length == 0 || f.Contains(".."))
                yield return new ValidationResult($"'{f}' is not a folder name or path like 'node_modules' or 'Assets/Demo'.");
    }
}

/// <summary>
/// A validated options record stored in the catalog under <paramref name="key"/>. Invalid values are rejected
/// (the previous value is kept); reopening another catalog reloads.
/// </summary>
public class CatalogOptions<T> where T : class, new()
{
    private readonly CatalogSettingsService _settings;
    private readonly string _key;
    private T? _current;

    public CatalogOptions(CatalogStore catalog, string key)
    {
        _settings = new CatalogSettingsService(catalog);
        _key = key;
        catalog.Opened += () => _current = null;
    }

    public event Action<T>? Changed;

    /// <summary>The current values (defaults until the catalog stores others).</summary>
    public async Task<T> GetAsync(CancellationToken ct = default) =>
        _current ??= await _settings.GetAsync<T>(_key, ct) ?? new T();

    /// <exception cref="ValidationException">A value is invalid; nothing is saved.</exception>
    public async Task SaveAsync(T value, CancellationToken ct = default)
    {
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(value, new ValidationContext(value), errors, validateAllProperties: true))
            throw new ValidationException(string.Join(" ", errors.Select(e => e.ErrorMessage)));
        await _settings.SetAsync(_key, value, ct);
        _current = value;
        Changed?.Invoke(value);
    }

    public Task ResetAsync(CancellationToken ct = default) => SaveAsync(new T(), ct);
}

public sealed class PreviewSettings(CatalogStore catalog) : CatalogOptions<PreviewOptions>(catalog, "preview-options");

public sealed class ClassificationSettings(CatalogStore catalog) : CatalogOptions<ClassificationOptions>(catalog, "classification-options");
