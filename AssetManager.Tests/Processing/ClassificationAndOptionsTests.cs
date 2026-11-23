using System.ComponentModel.DataAnnotations;
using AssetManager.Core.Domain;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Settings;
using AssetManager.Tests.Data;

namespace AssetManager.Tests.Processing;

public class ClassificationAndOptionsTests
{
    private static readonly FileClassifier Classifier = new(new ClassificationOptions());

    [Theory]
    [InlineData("Sprites/hero.png", AssetType.Image)]
    [InlineData("Textures/wood.png", AssetType.Texture)]
    [InlineData("Props/barrel_normal.tga", AssetType.Texture)]
    [InlineData("src/Painting.PSD", AssetType.Image)]
    [InlineData("Models/barrel.fbx", AssetType.Model3D)]
    [InlineData("tree.OBJ", AssetType.Model3D)]
    [InlineData("scene.blend", AssetType.Model3D)]
    [InlineData("sfx/jump.wav", AssetType.Audio)]
    [InlineData("music/theme.ogg", AssetType.Audio)]
    [InlineData("Fonts/pixel.ttf", AssetType.Font)]
    [InlineData("intro.mp4", AssetType.Video)]
    [InlineData("Materials/Wood.mat", AssetType.Material)]
    [InlineData("docs/manual.pdf", AssetType.Document)]
    [InlineData("data.bin", AssetType.Other)]
    public void Sample_extensions_map_to_types(string path, AssetType expected)
    {
        var c = Classifier.Classify(path);
        Assert.Equal(FileKind.Asset, c.Kind);
        Assert.Equal(expected, c.Type);
    }

    [Theory]
    [InlineData("Scripts/Player.cs")]
    [InlineData("Shaders/Toon.shader")]
    [InlineData("Plugins/x.dll")]
    [InlineData("Scenes/Demo.unity")]
    [InlineData("Prefabs/Barrel.prefab")]
    [InlineData("Game.asmdef")]
    public void Code_and_project_files_are_noted(string path)
    {
        var c = Classifier.Classify(path);
        Assert.Equal(FileKind.Noted, c.Kind);
        Assert.Equal("script (noted)", c.Reason);
    }

    [Theory]
    [InlineData("License.txt", FileKind.SourceDocument)]
    [InlineData("pack/README.md", FileKind.SourceDocument)]
    [InlineData("Store page.url", FileKind.SourceDocument)]
    [InlineData("Assets/x.png.meta", FileKind.Ignored)]
    [InlineData("Thumbs.db", FileKind.Ignored)]
    [InlineData("__MACOSX/._wood.png", FileKind.Ignored)]
    public void Documents_and_ignored_files(string path, FileKind kind) => Assert.Equal(kind, Classifier.Classify(path).Kind);

    [Fact]
    public void Script_list_is_configurable()
    {
        var custom = new FileClassifier(new ClassificationOptions { ScriptExtensions = [".lua"], IgnoredExtensions = [".png"] });
        Assert.Equal(FileKind.Asset, custom.Classify("Player.cs").Kind);
        Assert.Equal(FileKind.Noted, custom.Classify("ai.lua").Kind);
        Assert.Equal(FileKind.Ignored, custom.Classify("a.png").Kind);
    }

    [Fact]
    public void Signature_sniffing_for_unknown_extensions()
    {
        Assert.Equal(AssetType.Image, Classifier.Classify("image.dat", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A]).Type);
        Assert.Equal(AssetType.Audio, Classifier.Classify("sound.bin", "RIFF\0\0\0\0WAVEfmt "u8).Type);
        Assert.Equal(AssetType.Model3D, Classifier.Classify("x", "Kaydara FBX Binary  \0"u8).Type);
    }

    [Fact]
    public void Preview_option_defaults()
    {
        var o = new PreviewOptions();
        Assert.Equal(512, o.ImageMaxEdge);
        Assert.Equal(80, o.ImageQuality);
        Assert.Equal(512, o.KeepAsIsMaxDimension);
        Assert.Equal(64, o.KeepAsIsMaxKb);
        Assert.Equal(128, o.PixelArtMaxEdge);
        Assert.Equal(64, o.PixelArtMaxColors);
        Assert.True(o.PixelArtDetection);
        Assert.Equal(384, o.RenderSize);
        Assert.Equal([-15, 20, 55], o.Elevations);
        Assert.Equal(8, o.AzimuthsPerRing);
        Assert.Equal(80, o.RenderQuality);
        Assert.Equal(10, o.AudioClipSeconds);
        Assert.Equal(48, o.AudioBitrateKbps);
        Assert.Equal(1, o.AudioChannels);
        Assert.Equal(5, o.PaletteColors);
        Assert.Equal(24, o.ViewCount);
        Assert.Empty(o.Errors());
    }

    [Fact]
    public async Task Preview_options_round_trip_reset_and_reject_invalid()
    {
        await using var c = await TestCatalog.CreateAsync();
        var settings = new PreviewSettings(c.Store);
        Assert.Equal(new PreviewOptions().Version(), (await settings.GetAsync()).Version());

        var custom = new PreviewOptions { AudioClipSeconds = 5, Elevations = [0, 30], AzimuthsPerRing = 6, KeepAsIsMaxDimension = 256 };
        await settings.SaveAsync(custom);
        var reloaded = await new PreviewSettings(c.Store).GetAsync();
        Assert.Equal(5, reloaded.AudioClipSeconds);
        Assert.Equal([0, 30], reloaded.Elevations);
        Assert.Equal(custom.Version(), reloaded.Version());
        Assert.NotEqual(new PreviewOptions().Version(), reloaded.Version());

        await Assert.ThrowsAsync<ValidationException>(() => settings.SaveAsync(reloaded with { AzimuthsPerRing = 0 }));
        await Assert.ThrowsAsync<ValidationException>(() => settings.SaveAsync(reloaded with { AudioClipSeconds = 61 }));
        await Assert.ThrowsAsync<ValidationException>(() => settings.SaveAsync(reloaded with { Elevations = [95] }));
        Assert.Equal(6, (await new PreviewSettings(c.Store).GetAsync()).AzimuthsPerRing); // previous value kept

        await settings.ResetAsync();
        Assert.Equal(new PreviewOptions().Version(), (await new PreviewSettings(c.Store).GetAsync()).Version());
    }
}
