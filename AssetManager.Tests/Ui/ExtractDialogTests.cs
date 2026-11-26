using AssetManager.Core.Domain;
using AssetManager.Core.Platform;
using AssetManager.Processing.Export;
using AssetManager.Processing.Import;
using AssetManager.Shared.Components;
using AssetManager.Shared.Services;
using AssetManager.Tests.Fixtures;
using AssetManager.Tests.Import;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace AssetManager.Tests.Ui;

/// <summary>Unity project export in the extract dialog (spec "Unity project export", "GUID already in project").</summary>
public class ExtractDialogTests : BunitContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private sealed class NoPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task Unity_export_reviews_dependencies_and_guid_conflicts_before_extracting()
    {
        await using var kit = await ImportKit.CreateAsync();
        var g = FixtureBuilder.Guid;
        var material = "%YAML 1.1\n--- !u!21 &2100000\nMaterial:\n  m_Texture: {fileID: 2800000, guid: " + g("albedo") + ", type: 3}\n";
        var package = kit.Catalog.Dir.Write("Kit.unitypackage", FixtureBuilder.UnityPackage(
        [
            new(g("albedo"), "Assets/Kit/albedo.png", ImportStagingTests.Png(ImageMagick.MagickColors.Orange)),
            new(g("crate_mat"), "Assets/Kit/crate.mat", System.Text.Encoding.UTF8.GetBytes(material)),
            new(g("crate"), "Assets/Kit/Crate.prefab", System.Text.Encoding.UTF8.GetBytes("%YAML 1.1\n  m_Materials:\n  - {fileID: 2100000, guid: " + g("crate_mat") + ", type: 2}\n")),
        ]));
        await kit.CatalogAsync(new ImportRequest([package], Depth: AiDepth.None));
        long prefab;
        await using (var db = kit.Catalog.Db())
            prefab = await db.AssetFiles.Where(f => f.Extension == ".prefab").Select(f => f.AssetId).SingleAsync(Ct);

        var project = Path.Combine(kit.Catalog.Dir.Path, "MyGame");
        Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
        Directory.CreateDirectory(Path.Combine(project, "Assets", "Old"));
        File.WriteAllText(Path.Combine(project, "Assets", "Old", "albedo_old.png.meta"), $"guid: {g("albedo")}\n");
        var target = Path.Combine(project, "Assets", "Imported");

        Services.AddMudServices();
        Services.AddSingleton(new AssetExporter(kit.Store, kit.Processing.Resolver, references: new UnityReferences(kit.Store, kit.Processing.Resolver, kit.Processing.Readers)));
        Services.AddSingleton<IFolderPicker>(new NoPicker());
        Services.AddScoped<UiActions>();
        Services.AddLogging();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var dialogs = Render<MudDialogProvider>();
        var service = Services.GetRequiredService<IDialogService>();
        var reference = await dialogs.InvokeAsync(() => service.ShowAsync<ExtractDialog>("Extract",
            new DialogParameters<ExtractDialog> { { x => x.AssetIds, (IReadOnlyCollection<long>)[prefab] } }));

        // A target inside a Unity project turns Unity project export on.
        dialogs.Find(".extract-folder input").Input(target);
        dialogs.WaitForAssertion(() => Assert.NotNull(dialogs.Find(".extract-unity-detected")));
        Assert.True(dialogs.Find(".extract-unity input").HasAttribute("checked"));
        dialogs.Find(".extract-references input").Change(true);
        dialogs.Find("button.extract-ok").Click();

        // The review lists the referenced files as dependencies and the GUID the project already has; nothing is written yet.
        dialogs.WaitForAssertion(() => Assert.NotNull(dialogs.Find("#extract-review")));
        Assert.Equal(["Assets\\Kit\\albedo.png", "Assets\\Kit\\crate.mat"],
            dialogs.FindAll(".extract-dependency").Select(d => d.TextContent.Replace("dependency", "").Trim().Replace('/', '\\')).Order());
        Assert.Contains("albedo.png", dialogs.Find(".guid-conflict").TextContent);
        Assert.False(Directory.Exists(target));

        dialogs.Find("button.guid-skip").Click();
        var result = await reference.Result;
        var payload = Assert.IsType<ExtractPayload>(result!.Data);
        Assert.True(payload.UnityMeta && payload.IncludeReferences);
        Assert.Equal([g("albedo")], payload.SkipGuids);
    }
}
