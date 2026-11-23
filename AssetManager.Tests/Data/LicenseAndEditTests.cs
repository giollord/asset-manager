using AssetManager.Core.Domain;
using AssetManager.Data.Search;
using AssetManager.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Data;

public class LicenseAndEditTests
{
    [Fact]
    public async Task Inherited_license_unless_overridden()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 3, s =>
        {
            s.License = new LicenseInfo { Kind = "Custom", Commercial = TriState.Yes, Attribution = TriState.Yes };
            s.Links = [new("https://example.itch.io/pack")];
        });
        var overridden = src.Assets[2].Id;
        await new MetadataEditService(c.Store).EditAssetAsync(overridden,
            new AssetUpdate { License = new LicenseInfo { Kind = "CC-BY-NC", Commercial = TriState.No, Attribution = TriState.Yes } });

        await using var db = c.Db();
        var assets = await db.Assets.Include(a => a.Source).OrderBy(a => a.Id).ToListAsync();
        Assert.All(assets.Take(2), a =>
        {
            Assert.Equal(TriState.Yes, a.EffectiveLicense.Commercial);
            Assert.Equal(TriState.Yes, a.EffectiveLicense.Attribution);
            Assert.Equal("https://example.itch.io/pack", Assert.Single(a.EffectiveLinks).Url);
        });
        Assert.Equal(TriState.No, assets[2].EffectiveLicense.Commercial);

        // The SQL filter agrees with the in-memory rule.
        Assert.Equal(2, await db.Assets.WhereCommercial(TriState.Yes).CountAsync());
        Assert.Equal(overridden, (await db.Assets.WhereCommercial(TriState.No).SingleAsync()).Id);

        await new MetadataEditService(c.Store).ResetLicenseToSourceAsync(overridden);
        Assert.Equal(3, await db.Assets.WhereCommercial(TriState.Yes).CountAsync());
    }

    [Fact]
    public async Task Unknown_license_by_default()
    {
        await using var c = await TestCatalog.CreateAsync();
        await TestData.SourceAsync(c, 1);
        await using var db = c.Db();
        var a = await db.Assets.Include(x => x.Source).SingleAsync();
        Assert.Equal(TriState.Unknown, a.EffectiveLicense.Commercial);
        Assert.Equal(TriState.Unknown, a.EffectiveLicense.Attribution);
        Assert.Equal(1, await db.Assets.WhereCommercial(TriState.Unknown).CountAsync());
    }

    [Fact]
    public async Task User_edit_preserved_on_ai_rerun()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 1);
        var id = src.Assets[0].Id;
        await new MetadataEditService(c.Store).EditAssetAsync(id, new AssetUpdate { Description = "My barrel" });

        await using (var db = c.Db())
        {
            var a = await db.Assets.SingleAsync();
            new AssetUpdate { Description = "AI barrel", Style = ["low-poly"] }.ApplyAsAi(a);
            await db.SaveChangesAsync();
        }

        await using (var db = c.Db())
        {
            var a = await db.Assets.SingleAsync();
            Assert.Equal("My barrel", a.Description);
            Assert.Equal(["low-poly"], a.Style);
            Assert.True(a.UserEdited.HasFlag(EditedFields.Description));

            // Explicit regeneration overwrites and clears the user mark.
            new AssetUpdate { Description = "AI barrel" }.ApplyAsAi(a, overwriteUserEdits: true);
            Assert.Equal("AI barrel", a.Description);
            Assert.False(a.UserEdited.HasFlag(EditedFields.Description));
        }
    }

    [Fact]
    public void Source_user_edit_preserved()
    {
        var s = new Source();
        new SourceUpdate { Notes = "mine" }.ApplyAsUser(s);
        new SourceUpdate { Notes = "ai", Description = "pack" }.ApplyAsAi(s);
        Assert.Equal("mine", s.Notes);
        Assert.Equal("pack", s.Description);
    }
}
