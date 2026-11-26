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
    /// <summary>A box whose second half (a copy of the mesh) uses another material; neither references a texture.</summary>
    private static Assimp.Scene TwoMaterials(string first, string second)
    {
        var scene = Box(new BoxOptions { Material = first });
        scene.Materials.Add(new Assimp.Material { Name = second, ColorDiffuse = new System.Numerics.Vector4(0.8f, 0.8f, 0.8f, 1) });
        var copy = new Assimp.Mesh("Leaves", Assimp.PrimitiveType.Triangle) { MaterialIndex = 1 };
        copy.Vertices.AddRange(scene.Meshes[0].Vertices.Select(v => v + new System.Numerics.Vector3(0, 1, 0)));
        copy.Faces.AddRange(scene.Meshes[0].Faces.Select(f => new Assimp.Face([.. f.Indices])));
        copy.TextureCoordinateChannels[0].AddRange(scene.Meshes[0].TextureCoordinateChannels[0]);
        copy.UVComponentCount[0] = 2;
        scene.Meshes.Add(copy);
        scene.RootNode.Children[0].MeshIndices.Add(1);
        return scene;
    }

    /// <summary>Scenario "Prefixed names" (design D20 step 4).</summary>
    [Fact]
    public void Prefixed_names_attach_albedo_and_normal_map()
    {
        using var dir = FixtureBuilder.TempDir();
        Write(dir, "Orc/Meshes/SK_Orc.fbx", Box(new BoxOptions { Material = "Orc" }), "fbx");
        Png(dir, "Orc/Textures/T_Orc_Albedo.png");
        Png(dir, "Orc/Textures/T_Orc_Normal.png");
        Png(dir, "Orc/Textures/T_Goblin_Albedo.png");

        var groups = new CompanionResolver().Group(
        [
            File(dir, "Orc/Meshes/SK_Orc.fbx"),
            File(dir, "Orc/Textures/T_Orc_Albedo.png"),
            File(dir, "Orc/Textures/T_Orc_Normal.png"),
            File(dir, "Orc/Textures/T_Goblin_Albedo.png"),
        ]);

        var model = Assert.Single(groups, g => g.Primary.Class.Type == AssetType.Model3D);
        Assert.Collection(model.Companions.OrderBy(c => c.File.FileName),
            c => Assert.Equal(("T_Orc_Albedo.png", "Orc", "map", CompanionOrigin.NameMatch), (c.File.FileName, c.Material, c.Slot, c.Origin)),
            c => Assert.Equal(("T_Orc_Normal.png", "Orc", "normalMap", CompanionOrigin.NameMatch), (c.File.FileName, c.Material, c.Slot, c.Origin)));
        Assert.Single(groups, g => g.Primary.FileName == "T_Goblin_Albedo.png"); // unrelated texture stays its own asset
    }

    /// <summary>Scenario "Reference with another extension and folder".</summary>
    [Fact]
    public void Missing_reference_matches_the_same_name_in_another_format_and_folder()
    {
        using var dir = FixtureBuilder.TempDir();
        Write(dir, "farm/Models/Buildings/House_1.fbx", Box(new BoxOptions { Material = "Material", DiffuseTexture = @"C:\Users\artist\Desktop\Color 2.jpg" }), "fbx");
        Png(dir, "farm/Textures/Color 2.png");
        Png(dir, "farm/Textures/Emission.png");

        var groups = new CompanionResolver().Group(
        [
            File(dir, "farm/Models/Buildings/House_1.fbx"),
            File(dir, "farm/Textures/Color 2.png"),
            File(dir, "farm/Textures/Emission.png"),
        ]);

        var model = Assert.Single(groups, g => g.Primary.Class.Type == AssetType.Model3D);
        var link = Assert.Single(model.Companions);
        Assert.Equal(("Color 2.png", "Material", "map", CompanionOrigin.NameMatch), (link.File.FileName, link.Material, link.Slot, link.Origin));
    }

    /// <summary>The name-match half of scenario "Two materials": the leaves match by name, the bark is left for the AI.</summary>
    [Fact]
    public void Each_material_is_matched_on_its_own()
    {
        using var dir = FixtureBuilder.TempDir();
        Write(dir, "Trees/FBX/Tree_3.fbx", TwoMaterials("Bark", "Tree_Leaves"), "fbx");
        foreach (var t in new[] { "Textures/Tree_Leaves.png", "Textures/Tree_Bark.jpg", "Textures/Birch_Bark.png", "Textures/Color Variations/Bark_Dead.png" })
            Png(dir, "Trees/" + t);

        var groups = new CompanionResolver().Group(
        [
            File(dir, "Trees/FBX/Tree_3.fbx"),
            File(dir, "Trees/Textures/Tree_Leaves.png"),
            File(dir, "Trees/Textures/Tree_Bark.jpg"),
            File(dir, "Trees/Textures/Birch_Bark.png"),
            File(dir, "Trees/Textures/Color Variations/Bark_Dead.png"),
        ]);

        var model = Assert.Single(groups, g => g.Primary.Class.Type == AssetType.Model3D);
        var link = Assert.Single(model.Companions);
        Assert.Equal(("Tree_Leaves.png", "Tree_Leaves", "map", CompanionOrigin.NameMatch), (link.File.FileName, link.Material, link.Slot, link.Origin));
        Assert.Equal(4, groups.Count); // the three bark textures stay assets: "Bark" matches none of them exactly
    }

    [Fact]
    public void Ambiguous_names_attach_nothing()
    {
        using var dir = FixtureBuilder.TempDir();
        Write(dir, "pack/Models/Rock.fbx", Box(new BoxOptions { Material = "Rock" }), "fbx");
        Png(dir, "pack/Textures/Summer/T_Rock.png");
        Png(dir, "pack/Textures/Winter/T_Rock.png");

        var groups = new CompanionResolver().Group(
        [
            File(dir, "pack/Models/Rock.fbx"),
            File(dir, "pack/Textures/Summer/T_Rock.png"),
            File(dir, "pack/Textures/Winter/T_Rock.png"),
        ]);

        Assert.Empty(Assert.Single(groups, g => g.Primary.Class.Type == AssetType.Model3D).Companions);
    }

    [Theory]
    [InlineData("SK_Orc", "orc")]
    [InlineData("T_Orc_Albedo.png", "orc")]
    [InlineData("T_Orc_Base_Color.tga", "orc")]
    [InlineData(@"C:\Users\artist\Desktop\Color 2.jpg", "color2")]
    [InlineData("LowPolyCity_Texture_Diff.png", "lowpolycity")]
    [InlineData("Polygon_Texture_01_A.png", "polygontexture01a")]
    [InlineData("Material.001", "material001")]
    [InlineData("Normal.png", "normal")]
    public void Texture_name_keys(string name, string key) => Assert.Equal(key, TextureNames.Key(name));

    [Theory]
    [InlineData("T_Orc_Albedo.png", TextureMapKind.BaseColor)]
    [InlineData("Tree_Bark.jpg", TextureMapKind.BaseColor)]
    [InlineData("T_Orc_Normal.png", TextureMapKind.Normal)]
    [InlineData("Rock_N.png", TextureMapKind.Normal)]
    [InlineData("Polygon_Legendary_Chest_Texture_01_Emission.png", TextureMapKind.Emissive)]
    [InlineData("Rock_Roughness.png", TextureMapKind.Other)]
    [InlineData("Rock_Ambient_Occlusion.png", TextureMapKind.Other)]
    public void Texture_kinds(string name, TextureMapKind kind) => Assert.Equal(kind, TextureNames.KindOf(name));
}
