using AssetManager.Core.Domain;
using AssetManager.Processing.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetManager.Processing.Handlers;

/// <summary>A file of a source being imported, with its classification.</summary>
public sealed record SourceFile(string RelativePath, string LocalPath, FileClass Class)
{
    public string? UnityGuid { get; init; }

    public string FileName => Path.GetFileName(RelativePath.Replace('\\', '/'));

    public string Stem => Path.GetFileNameWithoutExtension(FileName);

    public string Folder => Path.GetDirectoryName(RelativePath.Replace('\\', '/'))?.Replace('\\', '/') ?? "";

    public string Extension => Path.GetExtension(FileName).ToLowerInvariant();
}

/// <summary>A file attached to a model, with the material slot it fills when known.</summary>
public sealed record CompanionLink(SourceFile File, string? Material, string? Slot, string Reason);

/// <summary>One future asset: a primary file plus its companions.</summary>
public sealed record AssetGroup(SourceFile Primary, IReadOnlyList<CompanionLink> Companions);

/// <summary>
/// Groups a source's files into assets before the handlers run (design D5). Textures are attached to models by,
/// in order: Unity <c>.mat</c> GUID references (matched to the model's material names), the texture paths in the
/// model's materials (Assimp), and file-name heuristics (same base name or a known suffix). A claimed texture
/// becomes a companion and not its own asset; anything unclaimed stays a standalone asset.
/// </summary>
public sealed class CompanionResolver(ILogger<CompanionResolver>? logger = null)
{
    private static readonly (string Suffix, string? Slot)[] Suffixes =
    [
        ("albedo", "map"), ("diffuse", "map"), ("basecolor", "map"), ("base_color", "map"), ("basemap", "map"), ("color", "map"),
        ("col", "map"), ("diff", "map"), ("d", "map"), ("maintex", "map"), ("tex", "map"), ("texture", "map"),
        ("normal", "normalMap"), ("normals", "normalMap"), ("nrm", "normalMap"), ("norm", "normalMap"), ("n", "normalMap"), ("bump", "normalMap"),
        ("emissive", "emissiveMap"), ("emission", "emissiveMap"), ("emit", "emissiveMap"),
        ("roughness", null), ("rough", null), ("metallic", null), ("metalness", null), ("metal", null), ("ao", null), ("occlusion", null),
        ("height", null), ("disp", null), ("displacement", null), ("specular", null), ("spec", null), ("gloss", null), ("mask", null),
        ("orm", null), ("mra", null), ("mets", null), ("ms", null),
    ];

    private readonly ILogger _log = logger ?? (ILogger)NullLogger.Instance;

    public IReadOnlyList<AssetGroup> Group(IReadOnlyList<SourceFile> files, CancellationToken ct = default)
    {
        var assets = files.Where(f => f.Class.Kind == FileKind.Asset).ToList();
        var textures = assets.Where(f => f.Class.Type is AssetType.Image or AssetType.Texture).ToList();
        var models = assets.Where(f => f.Class.Type == AssetType.Model3D).ToList();
        var unityMats = assets.Where(f => f.Extension == ".mat")
            .Select(f => (File: f, Info: UnityMaterial.Read(f.LocalPath)))
            .Where(x => x.Info is not null)
            .Select(x => (x.File, Info: x.Info!))
            .ToList();
        var mtls = assets.Where(f => f.Extension == ".mtl").ToList();

        var byGuid = new Dictionary<string, SourceFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in textures)
            if ((t.UnityGuid ?? MetaGuid(t)) is { } g) byGuid.TryAdd(g, t);
        var byName = textures.ToLookup(t => t.FileName, StringComparer.OrdinalIgnoreCase);

        var claimed = new HashSet<SourceFile>(ReferenceEqualityComparer.Instance);
        var groups = new List<AssetGroup>();
        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();
            var links = new List<CompanionLink>();
            void Claim(SourceFile f, string? material, string? slot, string reason)
            {
                if (links.Any(l => ReferenceEquals(l.File, f) && l.Material == material && l.Slot == slot)) return;
                if (slot is not null && links.Any(l => l.Material == material && l.Slot == slot)) slot = null; // slot already filled
                links.Add(new CompanionLink(f, material, slot, reason));
                claimed.Add(f);
            }

            var materials = ReadMaterials(model);

            // 1. Unity .mat files named like the model's materials.
            foreach (var (matName, _) in materials)
            {
                var mat = unityMats
                    .Where(m => string.Equals(m.Info.Name, matName, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(m.File.Stem, matName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(m => CommonPrefix(m.File.Folder, model.Folder))
                    .FirstOrDefault();
                if (mat.File is null) continue;
                Claim(mat.File, matName, null, "unity material");
                foreach (var (_, guid, slot) in mat.Info.Textures)
                    if (byGuid.TryGetValue(guid, out var tex)) Claim(tex, matName, slot, "unity material");
            }

            // 2. Texture paths stored in the model's materials.
            foreach (var (matName, refs) in materials)
                foreach (var (slot, file) in refs)
                {
                    if (slot is not null && links.Any(l => l.Material == matName && l.Slot == slot)) continue;
                    var tex = byName[file].OrderByDescending(t => CommonPrefix(t.Folder, model.Folder)).FirstOrDefault();
                    if (tex is not null) Claim(tex, matName, slot, "model material");
                }

            // 3. File-name heuristics near the model.
            foreach (var t in textures)
            {
                if (!NearModel(t, model)) continue;
                if (HeuristicSlot(t.Stem, model.Stem) is { } match) Claim(t, null, match.Slot, "name match");
            }
            foreach (var mtl in mtls)
                if (model.Extension == ".obj" && string.Equals(mtl.Stem, model.Stem, StringComparison.OrdinalIgnoreCase) && mtl.Folder == model.Folder)
                    Claim(mtl, null, null, "obj material library");

            groups.Add(new AssetGroup(model, links));
        }

        foreach (var f in assets)
            if (f.Class.Type != AssetType.Model3D && !claimed.Contains(f)) groups.Add(new AssetGroup(f, []));
        _log.LogDebug("Grouped {Files} files into {Groups} assets ({Claimed} companions)", assets.Count, groups.Count, claimed.Count);
        return groups;
    }

    /// <summary>Material names and their texture file references (by renderer slot).</summary>
    private List<(string Name, List<(string Slot, string File)> Refs)> ReadMaterials(SourceFile model)
    {
        try
        {
            using var loaded = ModelAnalyzer.Load(model.LocalPath);
            return loaded.Scene.Materials.Select(m =>
            {
                var refs = new List<(string, string)>();
                foreach (var (type, slot) in new[] { (Assimp.TextureType.BaseColor, "map"), (Assimp.TextureType.Diffuse, "map"),
                             (Assimp.TextureType.NormalCamera, "normalMap"), (Assimp.TextureType.Normals, "normalMap"), (Assimp.TextureType.Height, "normalMap"),
                             (Assimp.TextureType.Emissive, "emissiveMap"), (Assimp.TextureType.Specular, "other"), (Assimp.TextureType.Metalness, "other"),
                             (Assimp.TextureType.Roughness, "other"), (Assimp.TextureType.AmbientOcclusion, "other"), (Assimp.TextureType.Lightmap, "other") })
                    foreach (var slotInfo in m.GetMaterialTextures(type))
                        if (!string.IsNullOrWhiteSpace(slotInfo.FilePath) && !slotInfo.FilePath.StartsWith('*'))
                            refs.Add((slot == "other" ? null! : slot, Path.GetFileName(slotInfo.FilePath.Replace('\\', '/'))));
                return (m.Name ?? "", refs);
            }).ToList();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or Assimp.AssimpException)
        {
            _log.LogDebug("Cannot read materials of {Model}: {Error}", model.RelativePath, e.Message);
            return [];
        }
    }

    /// <summary>The texture's stem is the model's stem, optionally followed by a known map suffix.</summary>
    internal static (string? Slot, bool Ok)? HeuristicSlot(string textureStem, string modelStem)
    {
        var t = textureStem.ToLowerInvariant();
        var m = modelStem.ToLowerInvariant();
        if (t == m) return ("map", true);
        if (!t.StartsWith(m, StringComparison.Ordinal) || t.Length <= m.Length + 1 || t[m.Length] is not ('_' or '-' or ' ' or '.')) return null;
        var suffix = t[(m.Length + 1)..].Replace("-", "_").Replace(" ", "_");
        foreach (var (s, slot) in Suffixes)
            if (suffix == s || suffix.StartsWith(s + "_", StringComparison.Ordinal) || suffix.EndsWith("_" + s, StringComparison.Ordinal))
                return (slot, true);
        return null;
    }

    /// <summary>Same folder, or a texture folder next to / below the model's folder.</summary>
    private static bool NearModel(SourceFile texture, SourceFile model)
    {
        if (texture.Folder == model.Folder) return true;
        var parent = Path.GetDirectoryName(model.Folder)?.Replace('\\', '/') ?? "";
        return texture.Folder.StartsWith(model.Folder + "/", StringComparison.OrdinalIgnoreCase) ||
               (Path.GetDirectoryName(texture.Folder)?.Replace('\\', '/') ?? "") == parent &&
               Path.GetFileName(texture.Folder).Contains("tex", StringComparison.OrdinalIgnoreCase);
    }

    private static int CommonPrefix(string a, string b)
    {
        var x = a.Split('/');
        var y = b.Split('/');
        var n = 0;
        while (n < x.Length && n < y.Length && string.Equals(x[n], y[n], StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    private static string? MetaGuid(SourceFile f) => File.Exists(f.LocalPath + ".meta") ? UnityMaterial.GuidFromMeta(f.LocalPath + ".meta") : null;
}
