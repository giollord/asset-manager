using AssetManager.Core.Domain;
using AssetManager.Data;
using AssetManager.Processing.Models;
using AssetManager.Processing.Settings;
using ImageMagick;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Processing.Handlers;

/// <summary>A texture of the model's source that could be a material's base colour.</summary>
/// <param name="FileId">The catalog file: a texture asset's primary file or another model's companion.</param>
/// <param name="Evidence">Its name shares a word with the material, its missing reference or the model.</param>
public sealed record TextureCandidate(long FileId, string RelativePath, double Score, bool Evidence)
{
    public string FileName => RelativePath[(RelativePath.LastIndexOf('/') + 1)..];
}

/// <summary>A material without a base colour, with its shortlisted candidates (best first).</summary>
/// <param name="MissingReference">The file its material references but the pack does not contain, if any.</param>
public sealed record MaterialToMatch(string Material, string? MissingReference, IReadOnlyList<TextureCandidate> Candidates);

/// <summary>The texture matching steps the AI judgment uses (implemented by <see cref="TextureMatcher"/>; tests fake it).</summary>
public interface ITextureMatcher
{
    bool CanRender { get; }

    Task<IReadOnlyList<MaterialToMatch>> PlanAsync(long assetId, CancellationToken ct = default);

    Task<IReadOnlyList<byte[]>> RenderCandidatesAsync(long assetId, MaterialToMatch match, CancellationToken ct = default);

    Task AttachAsync(Asset asset, string material, long fileId, CompanionOrigin origin, string? missingReference, CancellationToken ct = default);

    /// <summary>Suggests a source's pack textures for its still untextured models; returns the number of suggestions.</summary>
    Task<int> SuggestFromPackAsync(long sourceId, CancellationToken ct = default);
}

/// <summary>
/// Texture matching after import (design D20, spec "Materials that need texture matching", "Texture candidates"): finds
/// the materials of a model that still have no base colour, shortlists textures of the same source for each, renders
/// the model with each candidate for the AI to judge, and attaches the chosen texture or stores it as a suggestion.
/// </summary>
public sealed class TextureMatcher(CatalogStore catalog, ILocationResolver resolver, PreviewSettings settings, IModelRenderer? renderer = null) : ITextureMatcher
{
    /// <summary>How many candidates per material go to the AI.</summary>
    public const int MaxCandidates = 4;

    /// <summary>Edge of the candidate renders, in pixels.</summary>
    public const int RenderSize = 256;

    /// <summary>Words that say nothing about which texture belongs where.</summary>
    private static readonly HashSet<string> Generic =
        ["material", "mat", "default", "defaultmaterial", "texture", "tex", "textures", "standard", "lambert", "lambert1", "phong", "blinn", "mesh", "model", "polygon", "low", "poly", "lowpoly", "fbx", "obj"];

    /// <summary>Words of images that show a pack rather than texture a model.</summary>
    private static readonly HashSet<string> NotTextures = ["preview", "sample", "screenshot", "thumbnail", "icon", "logo", "banner", "cover"];

    public bool CanRender => renderer is { IsAvailable: true };

    /// <summary>The model's materials that need matching, with their candidates; empty when none do (spec "Materials that need texture matching").</summary>
    public async Task<IReadOnlyList<MaterialToMatch>> PlanAsync(long assetId, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var asset = await db.AllAssets.AsNoTracking().Include(a => a.Files).ThenInclude(f => f.Stored).FirstAsync(a => a.Id == assetId, ct);
        if (asset.Type != AssetType.Model3D || asset.UserEdited.HasFlag(EditedFields.Textures) || asset.PrimaryFile is null) return [];

        var pool = await PoolAsync(db, asset, ct);
        if (pool.Count == 0) return [];
        var materials = await WithItemAsync(asset, ct, item => Task.Run(() => UnmatchedMaterials(item), ct));
        var result = new List<MaterialToMatch>();
        foreach (var (material, missing) in materials)
        {
            var ranked = Rank(pool, material, missing, asset.Name);
            if (missing is null) ranked = ranked.Where(c => c.Evidence).ToList();
            var top = new List<TextureCandidate>();
            foreach (var c in ranked)
            {
                if (top.Count == MaxCandidates) break;
                if (await LooksLikeNormalMapAsync(db, c.FileId, ct)) continue;
                top.Add(c);
            }
            if (top.Count > 0) result.Add(new MaterialToMatch(material, missing, top));
        }
        return result;
    }

    /// <summary>Materials without a base colour, used by a mesh with texture coordinates, and the file each references (when missing).</summary>
    internal static List<(string Material, string? Missing)> UnmatchedMaterials(HandlerItem item)
    {
        using var model = ModelAnalyzer.Load(item.PrimaryPath);
        var scene = model.Scene;
        var (textures, _, _) = ModelHandler.ResolveTextures(scene, item, includeBytes: false);
        var list = new List<(string, string?)>();
        for (var i = 0; i < scene.MaterialCount; i++)
        {
            var material = scene.Materials[i];
            var name = material.Name ?? "";
            if (textures.Any(t => string.Equals(t.Material, name, StringComparison.OrdinalIgnoreCase) && t.Slot == "map")) continue;
            if (!scene.Meshes.Any(m => m.MaterialIndex == i && m.HasTextureCoords(0))) continue;
            string? missing = null;
            foreach (var type in new[] { Assimp.TextureType.BaseColor, Assimp.TextureType.Diffuse })
                if (material.GetMaterialTexture(type, 0, out var slot) && !string.IsNullOrWhiteSpace(slot.FilePath) && !slot.FilePath.StartsWith('*'))
                {
                    missing = Path.GetFileName(slot.FilePath.Replace('\\', '/'));
                    break;
                }
            if (list.All(x => !string.Equals(x.Item1, name, StringComparison.OrdinalIgnoreCase))) list.Add((name, missing));
        }
        return list;
    }

    /// <summary>
    /// Ranks base-colour candidates for one material (spec "Texture candidates"): shared words with the material, its
    /// missing reference and the model, then textures other models already use (atlases), then folder nearness.
    /// </summary>
    internal static List<TextureCandidate> Rank(IReadOnlyList<PoolFile> pool, string material, string? missing, string modelName)
    {
        var keys = Words(material).Concat(missing is null ? [] : Words(missing)).Concat(Words(modelName)).ToHashSet();
        return pool
            .Where(f => TextureNames.KindOf(f.RelativePath) == TextureMapKind.BaseColor && !TextureNames.Tokens(f.RelativePath).Any(NotTextures.Contains))
            .Select(f =>
            {
                var words = Words(f.RelativePath).ToHashSet();
                var shared = words.Count(keys.Contains);
                var score = shared + (f.UsedByModels > 0 ? 0.5 : 0) + 0.05 * f.FolderNearness;
                return new TextureCandidate(f.FileId, f.RelativePath, score, shared > 0);
            })
            .OrderByDescending(c => c.Score).ThenBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The meaningful words of a name (core tokens without generic words and plain numbers).</summary>
    internal static IEnumerable<string> Words(string name) =>
        TextureNames.CoreTokens(name).Where(t => t.Length > 1 && !Generic.Contains(t) && !t.All(char.IsDigit));

    /// <summary>A texture file of the source, once per path.</summary>
    internal sealed record PoolFile(long FileId, string RelativePath, int UsedByModels, int FolderNearness);

    /// <summary>The textures of the model's source the user can choose from (base colours first, then by path).</summary>
    public async Task<IReadOnlyList<TextureCandidate>> SourceTexturesAsync(long assetId, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var asset = await db.AllAssets.AsNoTracking().Include(a => a.Files).FirstAsync(a => a.Id == assetId, ct);
        if (asset.PrimaryFile is null) return [];
        return (await PoolAsync(db, asset, ct))
            .OrderBy(f => TextureNames.KindOf(f.RelativePath) == TextureMapKind.BaseColor ? 0 : 1)
            .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(f => new TextureCandidate(f.FileId, f.RelativePath, 0, false)).ToList();
    }

    /// <summary>The model's material names (needs the original).</summary>
    /// <exception cref="OriginalUnavailableException">The model file is not reachable.</exception>
    public async Task<IReadOnlyList<string>> MaterialsAsync(long assetId, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var asset = await db.AllAssets.AsNoTracking().Include(a => a.Files).ThenInclude(f => f.Stored).FirstAsync(a => a.Id == assetId, ct);
        using var local = await resolver.ResolveAsync(asset.PrimaryFile!, ct);
        return await Task.Run(() =>
        {
            using var model = ModelAnalyzer.Load(local.Path);
            return (IReadOnlyList<string>)model.Scene.Materials.Select(m => m.Name ?? "").Distinct().ToList();
        }, ct);
    }

    /// <summary>The source's image files: texture and image assets, and other models' base-colour companions.</summary>
    private static async Task<List<PoolFile>> PoolAsync(CatalogDbContext db, Asset model, CancellationToken ct)
    {
        // During an import the source's assets are still staged: include them (no query filters).
        var rows = await db.AssetFiles.IgnoreQueryFilters().AsNoTracking().Where(f => f.Asset.SourceId == model.SourceId && f.AssetId != model.Id)
            .Where(f => (f.Role == FileRole.Primary && (f.Asset.Type == AssetType.Texture || f.Asset.Type == AssetType.Image))
                        || (f.Role == FileRole.Companion && (f.Slot == null || f.Slot == "map")))
            .Select(f => new { f.Id, f.RelativePath, f.Extension, f.Role })
            .ToListAsync(ct);
        var own = model.Files.Select(f => f.RelativePath.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var modelFolder = Folder(model.PrimaryFile!.RelativePath);
        return rows.Where(r => ModelHandler.IsTextureExtension(r.Extension))
            .GroupBy(r => r.RelativePath.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
            .Where(g => !own.Contains(g.Key))
            .Select(g => new PoolFile(g.OrderBy(r => r.Role).First().Id, g.Key, g.Count(r => r.Role == FileRole.Companion), Nearness(Folder(g.Key), modelFolder)))
            .ToList();
    }

    private static string Folder(string path)
    {
        path = path.Replace('\\', '/');
        var i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }

    /// <summary>Shared leading folders.</summary>
    private static int Nearness(string a, string b)
    {
        var x = a.Split('/');
        var y = b.Split('/');
        var n = 0;
        while (n < x.Length && n < y.Length && string.Equals(x[n], y[n], StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    /// <summary>Normal maps are blue-violet on average (about 128, 128, 255); judged from the texture's stored thumbnail.</summary>
    private static async Task<bool> LooksLikeNormalMapAsync(CatalogDbContext db, long fileId, CancellationToken ct)
    {
        var assetId = await db.AssetFiles.Where(f => f.Id == fileId && f.Role == FileRole.Primary).Select(f => (long?)f.AssetId).FirstOrDefaultAsync(ct);
        if (assetId is null) return false; // a companion already used as a base colour elsewhere
        var thumb = await db.Previews.AsNoTracking().Where(p => p.AssetId == assetId && p.Kind == PreviewKind.Thumbnail).Select(p => p.Bytes).FirstOrDefaultAsync(ct);
        return thumb is not null && IsNormalMapColor(thumb);
    }

    /// <summary>Mean colour near (0.5, 0.5, 1): a tangent-space normal map.</summary>
    public static bool IsNormalMapColor(byte[] image)
    {
        try
        {
            using var img = new MagickImage(image);
            img.Alpha(AlphaAction.Off);
            img.Resize(32, 32);
            var px = img.GetPixels().ToByteArray(PixelMapping.RGB) ?? [];
            if (px.Length < 3) return false;
            double r = 0, g = 0, b = 0;
            for (var i = 0; i < px.Length; i += 3) { r += px[i]; g += px[i + 1]; b += px[i + 2]; }
            var n = px.Length / 3 * 255.0;
            (r, g, b) = (r / n, g / n, b / n);
            return b > 0.75 && Math.Abs(r - 0.5) < 0.15 && Math.Abs(g - 0.5) < 0.15;
        }
        catch (MagickException)
        {
            return false;
        }
    }

    /// <summary>
    /// Renders the model from the front, as it is (first) and with each candidate as the material's base colour, as
    /// JPEGs on a neutral background for the AI.
    /// </summary>
    /// <exception cref="RendererUnavailableException">No renderer is attached.</exception>
    public async Task<IReadOnlyList<byte[]>> RenderCandidatesAsync(long assetId, MaterialToMatch match, CancellationToken ct = default)
    {
        if (renderer is not { IsAvailable: true }) throw new RendererUnavailableException();
        var options = await settings.GetAsync(ct);
        await using var db = catalog.CreateDbContext();
        var asset = await db.AllAssets.AsNoTracking().Include(a => a.Files).ThenInclude(f => f.Stored).FirstAsync(a => a.Id == assetId, ct);
        var request = await WithItemAsync(asset, ct, item => Task.Run(() => ModelHandler.BuildRenderRequest(item, options), ct));
        var elevation = options.Elevations[Math.Clamp(options.PrimaryRing, 0, options.Elevations.Count - 1)];
        ModelRenderRequest Single(IReadOnlyList<RenderTexture> textures, IReadOnlyList<string> neutral) =>
            request with { Textures = textures, Size = RenderSize, Elevations = [elevation], Azimuths = 1, NeutralMaterials = neutral };

        var renders = new List<byte[]> { await RenderOneAsync(Single(request.Textures, request.NeutralMaterials), ct) };
        foreach (var candidate in match.Candidates)
        {
            var file = await db.AssetFiles.AsNoTracking().Include(f => f.Stored).FirstAsync(f => f.Id == candidate.FileId, ct);
            using var local = await resolver.ResolveAsync(file, ct);
            var (mime, bytes) = ModelHandler.ForBrowser(await File.ReadAllBytesAsync(local.Path, ct));
            var textures = request.Textures.Where(t => !(string.Equals(t.Material, match.Material, StringComparison.OrdinalIgnoreCase) && t.Slot == "map"))
                .Append(new RenderTexture(match.Material, "map", mime, bytes)).ToList();
            var neutral = request.NeutralMaterials.Where(m => !string.Equals(m, match.Material, StringComparison.OrdinalIgnoreCase)).ToList();
            renders.Add(await RenderOneAsync(Single(textures, neutral), ct));
        }
        return renders;
    }

    private async Task<byte[]> RenderOneAsync(ModelRenderRequest request, CancellationToken ct)
    {
        var view = (await renderer!.RenderAsync(request, ct))[0];
        using var img = new MagickImage(view.Webp);
        img.BackgroundColor = new MagickColor("#5a5a5a");
        img.Alpha(AlphaAction.Remove);
        img.Quality = 85;
        return img.ToByteArray(MagickFormat.Jpeg);
    }

    /// <summary>
    /// Attaches <paramref name="fileId"/> as the base colour of <paramref name="material"/>, with the normal and emissive
    /// maps of the same name, and updates the asset's missing-texture state and suggestions. <paramref name="asset"/> may
    /// be an entity tracked by the caller: the files are saved here, the asset's fields are only changed in memory.
    /// </summary>
    public async Task AttachAsync(Asset asset, string material, long fileId, CompanionOrigin origin, string? missingReference, CancellationToken ct = default)
    {
        await using (var db = catalog.CreateDbContext())
        {
            var file = await db.AssetFiles.AsNoTracking().FirstAsync(f => f.Id == fileId, ct);
            var existing = await db.AssetFiles.Where(f => f.AssetId == asset.Id && f.Role == FileRole.Companion && f.Material == material
                                                          && (f.Slot == "map" || f.Slot == "normalMap" || f.Slot == "emissiveMap")).ToListAsync(ct);
            db.AssetFiles.RemoveRange(existing);
            db.AssetFiles.Add(CompanionOf(asset.Id, file, material, "map", origin));
            var key = TextureNames.Key(file.FileName);
            var maps = await db.AssetFiles.IgnoreQueryFilters().AsNoTracking()
                .Where(f => f.Asset.SourceId == asset.SourceId && f.Id != file.Id).ToListAsync(ct);
            foreach (var (kind, slot) in new[] { (TextureMapKind.Normal, "normalMap"), (TextureMapKind.Emissive, "emissiveMap") })
                if (maps.Where(f => ModelHandler.IsTextureExtension(f.Extension) && TextureNames.KindOf(f.FileName) == kind && TextureNames.Key(f.FileName) == key)
                        .OrderByDescending(f => string.Equals(Folder(f.RelativePath), Folder(file.RelativePath), StringComparison.OrdinalIgnoreCase))
                        .FirstOrDefault() is { } map)
                    db.AssetFiles.Add(CompanionOf(asset.Id, map, material, slot, origin));
            await db.SaveChangesAsync(ct);
        }
        if (asset.Model is { } m)
        {
            var missing = missingReference is null ? m.MissingTextures : m.MissingTextures.Where(x => !string.Equals(x, missingReference, StringComparison.OrdinalIgnoreCase)).ToList();
            asset.Model = m with { MissingTextures = missing, TextureSuggestions = m.TextureSuggestions.Where(s => s.Material != material).ToList() };
            asset.TexturesMissing = missing.Count > 0;
        }
    }

    /// <summary>A pack texture needs at least this many models using it for the material…</summary>
    public const int PackMinModels = 3;

    /// <summary>…and this share of the models that have the material textured.</summary>
    public const double PackMinShare = 0.5;

    /// <summary>
    /// Pack texture suggestions (spec "Pack texture suggestions", design D20): when most models of a source that have a
    /// material textured use the same texture for it, that texture is suggested for the material on the source's
    /// models that still have it untextured. Returns the number of suggestions made.
    /// </summary>
    public async Task<int> SuggestFromPackAsync(long sourceId, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var models = await db.AllAssets.Include(a => a.Files).ThenInclude(f => f.Stored)
            .Where(a => a.SourceId == sourceId && a.Type == AssetType.Model3D).ToListAsync(ct);
        var packs = PackTextures(models);
        if (packs.Count == 0) return 0;

        // Suggest the texture's own file (a texture asset) rather than another model's companion row, when there is one.
        var paths = packs.Values.Select(p => p.RelativePath).ToList();
        var primaries = (await db.AssetFiles.IgnoreQueryFilters().AsNoTracking()
                .Where(f => f.Asset.SourceId == sourceId && f.Role == FileRole.Primary).Select(f => new { f.Id, f.RelativePath }).ToListAsync(ct))
            .GroupBy(f => f.RelativePath.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var made = 0;
        foreach (var model in models)
        {
            if (model.UserEdited.HasFlag(EditedFields.Textures) || model.PrimaryFile is null) continue;
            if (!model.Files.Any(f => f.Role == FileRole.Primary)) continue;
            List<(string Material, string? Missing)> materials;
            try
            {
                materials = await WithItemAsync(model, ct, item => Task.Run(() => UnmatchedMaterials(item), ct));
            }
            catch (Exception e) when (e is OriginalUnavailableException or IOException or Assimp.AssimpException)
            {
                continue;
            }
            foreach (var (material, _) in materials)
            {
                if (!packs.TryGetValue(TextureNames.Key(material), out var pack)) continue;
                if (model.Model?.TextureSuggestions.Any(s => s.Material == material && s.Confidence >= pack.Share) == true) continue;
                var fileId = primaries.GetValueOrDefault(pack.RelativePath, pack.FileId);
                Suggest(model, new TextureSuggestion(material, fileId, pack.RelativePath, pack.Share,
                    $"used for \"{material}\" by {pack.Models} of {pack.Textured} models in this pack"));
                made++;
            }
        }
        await db.SaveChangesAsync(ct);
        return made;
    }

    /// <summary>The texture most models use for a material name, when it clears <see cref="PackMinModels"/> and <see cref="PackMinShare"/>.</summary>
    internal sealed record PackTexture(long FileId, string RelativePath, int Models, int Textured, double Share);

    /// <summary>Pack textures by material name key (<see cref="TextureNames.Key"/>).</summary>
    internal static Dictionary<string, PackTexture> PackTextures(IEnumerable<Asset> models)
    {
        var uses = models.SelectMany(m => m.Files
                .Where(f => f.Role == FileRole.Companion && f.Slot == "map" && f.Material is { Length: > 0 } && ModelHandler.IsTextureExtension(f.Extension))
                .Select(f => (Model: m.Id, Key: TextureNames.Key(f.Material!), Path: f.RelativePath.Replace('\\', '/'), f.Id))
                .DistinctBy(u => (u.Key, u.Path.ToLowerInvariant())))
            .Where(u => u.Key.Length > 0)
            .ToList();
        var result = new Dictionary<string, PackTexture>();
        foreach (var byKey in uses.GroupBy(u => u.Key))
        {
            var textured = byKey.Select(u => u.Model).Distinct().Count();
            var top = byKey.GroupBy(u => u.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Uses: g, Models: g.Select(u => u.Model).Distinct().Count()))
                .OrderByDescending(x => x.Models).ThenBy(x => x.Uses.Key, StringComparer.OrdinalIgnoreCase).First();
            var share = (double)top.Models / textured;
            if (top.Models >= PackMinModels && share >= PackMinShare)
                result[byKey.Key] = new PackTexture(top.Uses.First().Id, top.Uses.Key, top.Models, textured, share);
        }
        return result;
    }

    /// <summary>Stores (or replaces) the suggestion for a material on <paramref name="asset"/>, in memory.</summary>
    public static void Suggest(Asset asset, TextureSuggestion suggestion)
    {
        var m = asset.Model ?? new ModelMetadata();
        asset.Model = m with { TextureSuggestions = m.TextureSuggestions.Where(s => s.Material != suggestion.Material).Append(suggestion).ToList() };
    }

    private static AssetFile CompanionOf(long assetId, AssetFile source, string material, string slot, CompanionOrigin origin) => new()
    {
        AssetId = assetId,
        Role = FileRole.Companion,
        Location = source.Location,
        RelativePath = source.RelativePath,
        FileName = source.FileName,
        Extension = source.Extension,
        Size = source.Size,
        FastHash = source.FastHash,
        ContentHash = source.ContentHash,
        UnityGuid = source.UnityGuid,
        Material = material,
        Slot = slot,
        Origin = origin,
    };

    /// <summary>Resolves the model and its companions to local files for <paramref name="work"/>.</summary>
    private async Task<T> WithItemAsync<T>(Asset asset, CancellationToken ct, Func<HandlerItem, Task<T>> work)
    {
        var leases = new List<ResolvedFile>();
        try
        {
            var primary = await resolver.ResolveAsync(asset.PrimaryFile!, ct);
            leases.Add(primary);
            var companions = new List<CompanionInput>();
            foreach (var f in asset.Files.Where(f => f.Role == FileRole.Companion))
            {
                try
                {
                    var local = await resolver.ResolveAsync(f, ct);
                    leases.Add(local);
                    companions.Add(new CompanionInput(f, local.Path, f.Material, f.Slot));
                }
                catch (OriginalUnavailableException) { }
            }
            return await work(new HandlerItem { Asset = asset, PrimaryPath = primary.Path, Companions = companions });
        }
        finally
        {
            foreach (var l in leases) l.Dispose();
        }
    }
}
