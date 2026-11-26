namespace AssetManager.Processing.Export;

/// <summary>Unity project detection for exports (design D8).</summary>
public static class UnityProject
{
    /// <summary>
    /// The root of the Unity project that contains <paramref name="folder"/> (a folder with both <c>Assets</c> and
    /// <c>ProjectSettings</c>), or null. The folder itself does not need to exist yet.
    /// </summary>
    public static string? FindRoot(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) return null;
        try
        {
            for (var dir = new DirectoryInfo(Path.GetFullPath(folder)); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "Assets")) && Directory.Exists(Path.Combine(dir.FullName, "ProjectSettings")))
                    return dir.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
        return null;
    }

    /// <summary>
    /// Entries whose GUID is already used by a <c>.meta</c> under the project's <c>Assets</c> folder (each read once). The
    /// <c>.meta</c> at an entry's own target path is not a GUID conflict: that is the same file again (a name conflict).
    /// </summary>
    public static IReadOnlyList<GuidConflict> GuidConflicts(string projectRoot, string targetFolder, IEnumerable<ExportEntry> entries)
    {
        var wanted = entries.Where(e => e.Guid is not null).ToList();
        if (wanted.Count == 0) return [];
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var assets = Path.Combine(projectRoot, "Assets");
        try
        {
            foreach (var meta in Directory.EnumerateFiles(assets, "*.meta", SearchOption.AllDirectories))
                if (Handlers.UnityMaterial.GuidFromMeta(meta) is { } g) existing.TryAdd(g, meta);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        var result = new List<GuidConflict>();
        foreach (var e in wanted)
        {
            if (!existing.TryGetValue(e.Guid!, out var meta)) continue;
            var own = Path.GetFullPath(Path.Combine(targetFolder, e.RelativeTarget) + ".meta");
            if (string.Equals(Path.GetFullPath(meta), own, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(new GuidConflict(e.RelativeTarget, e.Guid!, meta));
        }
        return result;
    }

    /// <summary>The <c>.meta</c> next to a resolved file (package extraction or Unity project folder), or null.</summary>
    public static string? MetaFor(string localPath)
    {
        var meta = localPath + ".meta";
        return File.Exists(meta) ? meta : null;
    }
}
