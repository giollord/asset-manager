using AssetManager.Core.Domain;
using AssetManager.Data;
using ImageMagick;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Processing.Handlers;

/// <summary>One companion texture of a model, as the details drawer lists it.</summary>
public sealed record ModelTextureRow(long FileId, string? Material, string? Slot, string FileName, string RelativePath, CompanionOrigin Origin, string? Thumbnail)
{
    public string OriginLabel => Origin switch
    {
        CompanionOrigin.NameMatch => "name match",
        CompanionOrigin.Ai => "AI",
        CompanionOrigin.User => "user",
        _ => "file reference",
    };
}

/// <summary>A model's textures and the AI's suggestions.</summary>
public sealed record ModelTexturesView(IReadOnlyList<ModelTextureRow> Textures, IReadOnlyList<TextureSuggestion> Suggestions, bool UserEdited);

/// <summary>
/// The user's side of texture matching (spec "Reviewing a model's textures", "User texture edits are kept", design D20):
/// list a model's textures, detach one, apply or dismiss a suggestion, or choose a texture of the source for a material.
/// Every change except dismissing marks the textures as user-edited and re-renders the previews.
/// </summary>
public sealed class ModelTextureService(CatalogStore catalog, ILocationResolver resolver, PreviewService previews, TextureMatcher matcher)
{
    private const int ThumbnailSize = 48;

    public async Task<ModelTexturesView> LoadAsync(long assetId, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var asset = await db.AllAssets.AsNoTracking().Include(a => a.Files).ThenInclude(f => f.Stored).FirstAsync(a => a.Id == assetId, ct);
        var rows = new List<ModelTextureRow>();
        foreach (var f in asset.Files.Where(f => f.Role == FileRole.Companion && ModelHandler.IsTextureExtension(f.Extension))
                     .OrderBy(f => f.Material).ThenBy(f => f.Slot == "map" ? 0 : 1).ThenBy(f => f.FileName))
            rows.Add(new ModelTextureRow(f.Id, f.Material, f.Slot, f.FileName, f.RelativePath.Replace('\\', '/'), f.Origin, await ThumbnailAsync(f, ct)));
        return new ModelTexturesView(rows, asset.Model?.TextureSuggestions ?? [], asset.UserEdited.HasFlag(EditedFields.Textures));
    }

    public Task<IReadOnlyList<TextureCandidate>> ChoicesAsync(long assetId, CancellationToken ct = default) => matcher.SourceTexturesAsync(assetId, ct);

    /// <exception cref="OriginalUnavailableException">The model file is not reachable.</exception>
    public Task<IReadOnlyList<string>> MaterialsAsync(long assetId, CancellationToken ct = default) => matcher.MaterialsAsync(assetId, ct);

    public async Task DetachAsync(long assetId, long fileId, CancellationToken ct = default)
    {
        await using (var db = catalog.CreateDbContext())
        {
            await db.AssetFiles.Where(f => f.Id == fileId && f.AssetId == assetId && f.Role == FileRole.Companion).ExecuteDeleteAsync(ct);
            var asset = await db.AllAssets.FirstAsync(a => a.Id == assetId, ct);
            asset.UserEdited |= EditedFields.Textures;
            await db.SaveChangesAsync(ct);
        }
        await RerenderAsync(assetId, ct);
    }

    public async Task ApplySuggestionAsync(long assetId, string material, CancellationToken ct = default)
    {
        await using (var db = catalog.CreateDbContext())
        {
            var asset = await db.AllAssets.FirstAsync(a => a.Id == assetId, ct);
            var suggestion = asset.Model?.TextureSuggestions.FirstOrDefault(s => s.Material == material)
                             ?? throw new InvalidOperationException("The suggestion is no longer there.");
            await matcher.AttachAsync(asset, material, suggestion.FileId, CompanionOrigin.User, null, ct);
            asset.UserEdited |= EditedFields.Textures;
            await db.SaveChangesAsync(ct);
        }
        await RerenderAsync(assetId, ct);
    }

    /// <summary>
    /// Attaches every texture suggestion of the given assets (bulk "Apply texture suggestions"; the job re-renders them
    /// afterwards). Assets without suggestions are left alone. Returns the models changed and the number of textures attached.
    /// </summary>
    public async Task<(IReadOnlyList<long> Models, int Textures)> AttachSuggestionsAsync(IReadOnlyList<long> assetIds, CancellationToken ct = default)
    {
        var changed = new List<long>();
        var attached = 0;
        foreach (var chunk in assetIds.Chunk(200))
        {
            await using var db = catalog.CreateDbContext();
            foreach (var asset in await db.AllAssets.Where(a => chunk.Contains(a.Id) && a.Type == AssetType.Model3D).ToListAsync(ct))
            {
                var suggestions = asset.Model?.TextureSuggestions.ToList() ?? [];
                if (suggestions.Count == 0) continue;
                foreach (var s in suggestions)
                {
                    await matcher.AttachAsync(asset, s.Material, s.FileId, CompanionOrigin.User, null, ct);
                    attached++;
                }
                asset.UserEdited |= EditedFields.Textures;
                changed.Add(asset.Id);
            }
            await db.SaveChangesAsync(ct);
        }
        return (changed, attached);
    }

    public async Task DismissSuggestionAsync(long assetId, string material, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var asset = await db.AllAssets.FirstAsync(a => a.Id == assetId, ct);
        if (asset.Model is { } m) asset.Model = m with { TextureSuggestions = m.TextureSuggestions.Where(s => s.Material != material).ToList() };
        await db.SaveChangesAsync(ct);
    }

    public async Task ChooseAsync(long assetId, string material, long fileId, CancellationToken ct = default)
    {
        await using (var db = catalog.CreateDbContext())
        {
            var asset = await db.AllAssets.FirstAsync(a => a.Id == assetId, ct);
            await matcher.AttachAsync(asset, material, fileId, CompanionOrigin.User, null, ct);
            asset.UserEdited |= EditedFields.Textures;
            await db.SaveChangesAsync(ct);
        }
        await RerenderAsync(assetId, ct);
    }

    private async Task RerenderAsync(long assetId, CancellationToken ct)
    {
        var result = await previews.RegenerateAsync([assetId], ct: ct);
        if (result.Skipped.Count > 0)
            throw new InvalidOperationException($"The textures were saved, but the previews could not be re-rendered: {result.Skipped[0].Reason}");
    }

    private async Task<string?> ThumbnailAsync(AssetFile file, CancellationToken ct)
    {
        try
        {
            using var local = await resolver.ResolveAsync(file, ct);
            using var img = new MagickImage(local.Path);
            img.Thumbnail(ThumbnailSize, ThumbnailSize);
            return "data:image/png;base64," + Convert.ToBase64String(img.ToByteArray(MagickFormat.Png));
        }
        catch (Exception e) when (e is OriginalUnavailableException or IOException or MagickException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
