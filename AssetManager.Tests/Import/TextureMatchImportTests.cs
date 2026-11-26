using AssetManager.Core.Domain;
using AssetManager.Processing.Import;
using AssetManager.Tests.Fixtures;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using static AssetManager.Tests.Fixtures.ModelFixtures;

namespace AssetManager.Tests.Import;

/// <summary>Texture matching through a real import (design D20): the origin of each companion is stored.</summary>
public class TextureMatchImportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Png(string path, string color)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var img = new MagickImage(new MagickColor(color), 64, 64);
        img.Write(path, MagickFormat.Png);
    }

    [Fact]
    public async Task Name_matched_textures_are_stored_with_their_origin()
    {
        await using var kit = await ImportKit.CreateAsync();
        var pack = Path.Combine(kit.Catalog.Dir.Path, "Orc Pack");
        Write(kit.Catalog.Dir, "Orc Pack/Meshes/SK_Orc.fbx", Box(new BoxOptions { Material = "Orc" }), "fbx");
        Write(kit.Catalog.Dir, "Orc Pack/Meshes/Crate.obj", Box(new BoxOptions { Material = "Crate", DiffuseTexture = "../Textures/Crate.png" }), "obj");
        Png(Path.Combine(pack, "Textures", "T_Orc_Albedo.png"), "#40a040");
        Png(Path.Combine(pack, "Textures", "T_Orc_Normal.png"), "#8080ff");
        Png(Path.Combine(pack, "Textures", "Crate.png"), "#a07040");

        await kit.CatalogAsync(new ImportRequest([pack], Depth: AiDepth.None));

        await using var db = kit.Catalog.Db();
        var files = await db.AssetFiles.Where(f => f.Role == FileRole.Companion).Include(f => f.Asset).ToListAsync(Ct);
        Assert.Equal(CompanionOrigin.NameMatch, files.Single(f => f.FileName == "T_Orc_Albedo.png").Origin);
        Assert.Equal(("Orc", "normalMap"), (files.Single(f => f.FileName == "T_Orc_Normal.png").Material, files.Single(f => f.FileName == "T_Orc_Normal.png").Slot));
        Assert.All(files.Where(f => f.FileName == "Crate.png"), f => Assert.Equal(CompanionOrigin.Reference, f.Origin)); // its own reference
        Assert.Equal(2, await db.Assets.CountAsync(Ct)); // the textures are companions, not assets
    }
}
