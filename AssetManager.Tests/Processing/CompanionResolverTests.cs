using System.Text;
using AssetManager.Core.Domain;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Settings;
using AssetManager.Tests.Fixtures;
using static AssetManager.Tests.Fixtures.ModelFixtures;

namespace AssetManager.Tests.Processing;

public class CompanionResolverTests
{
    private static readonly FileClassifier Classifier = new(new ClassificationOptions());

    private static SourceFile File(TempDir dir, string rel, string? guid = null) =>
        new(rel, Path.Combine(dir.Path, rel), Classifier.Classify(rel)) { UnityGuid = guid };

    private static void Png(TempDir dir, string rel) => dir.Write(rel, [0x89, 0x50, 0x4E, 0x47]);

    private static string MatYaml(string name, string mainGuid, string bumpGuid) => $"""
        %YAML 1.1
        %TAG !u! tag:unity3d.com,2011:
        --- !u!21 &2100000
        Material:
          serializedVersion: 8
          m_ObjectHideFlags: 0
          m_Name: {name}
          m_Shader: {"{"}fileID: 46, guid: 0000000000000000f000000000000000, type: 0{"}"}
          m_SavedProperties:
            serializedVersion: 3
            m_TexEnvs:
            - _BumpMap:
                m_Texture: {"{"}fileID: 2800000, guid: {bumpGuid}, type: 3{"}"}
                m_Scale: {"{"}x: 1, y: 1{"}"}
                m_Offset: {"{"}x: 0, y: 0{"}"}
            - _DetailMask:
                m_Texture: {"{"}fileID: 0{"}"}
            - _MainTex:
                m_Texture: {"{"}fileID: 2800000, guid: {mainGuid}, type: 3{"}"}
                m_Scale: {"{"}x: 1, y: 1{"}"}
            m_Floats:
            - _Glossiness: 0.5
        """;

    [Fact]
    public void Model_with_textures_is_one_asset_via_unity_material()
    {
        using var dir = FixtureBuilder.TempDir();
        var albedoGuid = FixtureBuilder.Guid("albedo");
        var normalGuid = FixtureBuilder.Guid("normal");
        Write(dir, "Assets/Props/Models/Barrel.fbx", Box(new BoxOptions { Material = "BarrelWood" }), "fbx");
        dir.Write("Assets/Props/Materials/BarrelWood.mat", MatYaml("BarrelWood", albedoGuid, normalGuid));
        Png(dir, "Assets/Props/Textures/T_Wood_A.png");
        Png(dir, "Assets/Props/Textures/T_Wood_N.png");
        Png(dir, "Assets/UI/icon.png");

        var groups = new CompanionResolver().Group(
        [
            File(dir, "Assets/Props/Models/Barrel.fbx", FixtureBuilder.Guid("barrel")),
            File(dir, "Assets/Props/Materials/BarrelWood.mat", FixtureBuilder.Guid("mat")),
            File(dir, "Assets/Props/Textures/T_Wood_A.png", albedoGuid),
            File(dir, "Assets/Props/Textures/T_Wood_N.png", normalGuid),
            File(dir, "Assets/UI/icon.png", FixtureBuilder.Guid("icon")),
        ]);

        Assert.Equal(2, groups.Count);
        var model = Assert.Single(groups, g => g.Primary.Class.Type == AssetType.Model3D);
        Assert.Equal(3, model.Companions.Count);
        Assert.Contains(model.Companions, c => c.File.FileName == "T_Wood_A.png" && c.Slot == "map" && c.Material == "BarrelWood");
        Assert.Contains(model.Companions, c => c.File.FileName == "T_Wood_N.png" && c.Slot == "normalMap");
        Assert.Contains(model.Companions, c => c.File.FileName == "BarrelWood.mat" && c.Reason == "unity material");

        // Standalone texture remains its own asset.
        var icon = Assert.Single(groups, g => g.Primary.FileName == "icon.png");
        Assert.Empty(icon.Companions);
    }

    [Fact]
    public void Texture_paths_in_model_materials_are_followed()
    {
        using var dir = FixtureBuilder.TempDir();
        Write(dir, "pack/models/crate.obj", Box(new BoxOptions { Material = "Crate", DiffuseTexture = "../tex/planks.png" }), "obj");
        Png(dir, "pack/tex/planks.png");

        var groups = new CompanionResolver().Group(
        [
            File(dir, "pack/models/crate.obj"),
            File(dir, "pack/models/crate.mtl"),
            File(dir, "pack/tex/planks.png"),
        ]);

        var model = Assert.Single(groups);
        Assert.Contains(model.Companions, c => c.File.FileName == "planks.png" && c.Slot == "map" && c.Reason == "model material");
        Assert.Contains(model.Companions, c => c.File.FileName == "crate.mtl");
    }

    [Fact]
    public void Name_heuristics_claim_suffixed_textures()
    {
        using var dir = FixtureBuilder.TempDir();
        Write(dir, "Tree.glb", Box(new BoxOptions { Material = "Bark" }), "glb2");
        foreach (var t in new[] { "Tree_BaseColor.png", "Tree_Normal.png", "Tree_Roughness.png", "Treehouse.png", "Rock_Albedo.png" }) Png(dir, t);

        var groups = new CompanionResolver().Group(
            new[] { "Tree.glb", "Tree_BaseColor.png", "Tree_Normal.png", "Tree_Roughness.png", "Treehouse.png", "Rock_Albedo.png" }
                .Select(f => File(dir, f)).ToList());

        var model = Assert.Single(groups, g => g.Primary.FileName == "Tree.glb");
        Assert.Equal(["Tree_BaseColor.png", "Tree_Normal.png", "Tree_Roughness.png"], model.Companions.Select(c => c.File.FileName).Order());
        Assert.Equal("normalMap", model.Companions.Single(c => c.File.FileName == "Tree_Normal.png").Slot);
        Assert.Contains(groups, g => g.Primary.FileName == "Treehouse.png");
        Assert.Contains(groups, g => g.Primary.FileName == "Rock_Albedo.png");
        Assert.Equal(3, groups.Count);
    }

    [Fact]
    public void Shared_texture_is_companion_of_every_model_using_it()
    {
        using var dir = FixtureBuilder.TempDir();
        Write(dir, "a.obj", Box(new BoxOptions { Material = "M", DiffuseTexture = "atlas.png" }), "obj");
        Write(dir, "b.obj", Box(new BoxOptions { Material = "M", DiffuseTexture = "atlas.png" }), "obj");
        Png(dir, "atlas.png");

        var groups = new CompanionResolver().Group([File(dir, "a.obj"), File(dir, "b.obj"), File(dir, "atlas.png")]);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Contains(g.Companions, c => c.File.FileName == "atlas.png"));
    }

    [Fact]
    public void Unity_material_parsing()
    {
        var info = UnityMaterial.Parse(MatYaml("Stone", new string('a', 32), new string('b', 32)), "fallback");
        Assert.NotNull(info);
        Assert.Equal("Stone", info.Name);
        Assert.Equal([("_BumpMap", new string('b', 32), "normalMap"), ("_MainTex", new string('a', 32), "map")], info.Textures);
        Assert.Null(UnityMaterial.Parse("\u0001binary", "x"));
    }
}
