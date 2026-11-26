using AssetManager.Core.Domain;
using AssetManager.Processing.Export;
using AssetManager.Processing.Import;
using AssetManager.Tests.Fixtures;
using AssetManager.Tests.Import;
using ImageMagick;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Processing;

/// <summary>Spec "Referenced files in Unity export" (design D8).</summary>
public class UnityReferencesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Yaml = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";
    private const string BuiltIn = "0000000000000000f000000000000000";

    private static string G(string name) => FixtureBuilder.Guid(name);

    private static string Material(string name, string main, string normal) => Yaml + $"""
        --- !u!21 &2100000
        Material:
          m_Name: {name}
          m_Shader: {"{"}fileID: 46, guid: {BuiltIn}, type: 0{"}"}
          m_SavedProperties:
            m_TexEnvs:
            - _MainTex:
                m_Texture: {"{"}fileID: 2800000, guid: {G(main)}, type: 3{"}"}
            - _BumpMap:
                m_Texture: {"{"}fileID: 2800000, guid: {G(normal)}, type: 3{"}"}
        """;

    private const string Cube = "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nf 1 2 3 4\n";

    /// <summary>A package: Hero.prefab → crate mesh + 2 materials → 4 textures, an animator controller → a clip, and an unrelated texture.</summary>
    private static byte[] Package()
    {
        var textures = new[] { "wood_albedo", "wood_normal", "metal_albedo", "metal_normal", "unrelated" }
            .Select((n, i) => new UnityPackageEntry(G(n), $"Assets/Kit/Textures/{n}.png", ImportStagingTests.Png(new MagickColor((byte)(30 + i * 40), 90, 140))));
        var prefab = Yaml + $"""
            --- !u!1 &100
            GameObject:
              m_Name: Hero
            --- !u!33 &200
            MeshFilter:
              m_Mesh: {"{"}fileID: 4300000, guid: {G("crate")}, type: 3{"}"}
            --- !u!23 &300
            MeshRenderer:
              m_Materials:
              - {"{"}fileID: 2100000, guid: {G("wood")}, type: 2{"}"}
              - {"{"}fileID: 2100000, guid: {G("metal")}, type: 2{"}"}
              - {"{"}fileID: 10303, guid: {BuiltIn}, type: 0{"}"}
            --- !u!95 &400
            Animator:
              m_Controller: {"{"}fileID: 9100000, guid: {G("hero_ctrl")}, type: 2{"}"}
            """;
        var controller = Yaml + $"""
            --- !u!1102 &500
            AnimatorState:
              m_Motion: {"{"}fileID: 7400000, guid: {G("idle")}, type: 2{"}"}
            """;
        var idle = Yaml + "--- !u!74 &7400000\nAnimationClip:\n  m_Name: idle\n";
        return FixtureBuilder.UnityPackage(
        [
            .. textures,
            new(G("crate"), "Assets/Kit/Models/crate.obj", System.Text.Encoding.UTF8.GetBytes(Cube)),
            new(G("wood"), "Assets/Kit/Materials/wood.mat", System.Text.Encoding.UTF8.GetBytes(Material("wood", "wood_albedo", "wood_normal"))),
            new(G("metal"), "Assets/Kit/Materials/metal.mat", System.Text.Encoding.UTF8.GetBytes(Material("metal", "metal_albedo", "metal_normal"))),
            new(G("hero_ctrl"), "Assets/Kit/Animation/Hero.controller", System.Text.Encoding.UTF8.GetBytes(controller)),
            new(G("idle"), "Assets/Kit/Animation/idle.anim", System.Text.Encoding.UTF8.GetBytes(idle)),
            new(G("hero"), "Assets/Kit/Prefabs/Hero.prefab", System.Text.Encoding.UTF8.GetBytes(prefab)),
        ]);
    }

    [Fact]
    public async Task Prefab_with_dependencies()
    {
        await using var kit = await ImportKit.CreateAsync();
        var package = kit.Catalog.Dir.Write("Kit.unitypackage", Package());
        await kit.CatalogAsync(new ImportRequest([package], Depth: AiDepth.None));
        long hero;
        await using (var db = kit.Catalog.Db())
            hero = await db.AssetFiles.Where(f => f.Extension == ".prefab").Select(f => f.AssetId).SingleAsync(Ct); // prefabs are cataloged
        var exporter = new AssetExporter(kit.Store, kit.Processing.Resolver, references: new UnityReferences(kit.Store, kit.Processing.Resolver, kit.Processing.Readers));
        var target = Path.Combine(kit.Catalog.Dir.Path, "MyGame", "Assets", "Imported");

        // The plan lists the referenced files as dependencies.
        var plan = await exporter.PlanAsync([hero], new ExportOptions(target, PreserveStructure: true, IncludeReferences: true), Ct);
        string[] expected =
        [
            "Assets/Kit/Animation/Hero.controller", "Assets/Kit/Animation/idle.anim", "Assets/Kit/Materials/metal.mat", "Assets/Kit/Materials/wood.mat",
            "Assets/Kit/Models/crate.obj", "Assets/Kit/Textures/metal_albedo.png", "Assets/Kit/Textures/metal_normal.png",
            "Assets/Kit/Textures/wood_albedo.png", "Assets/Kit/Textures/wood_normal.png",
        ];
        Assert.Equal(expected, plan.Entries.Where(e => e.Dependency).Select(e => e.RelativeTarget.Replace('\\', '/')).Order(StringComparer.Ordinal));
        Assert.Equal("Assets/Kit/Prefabs/Hero.prefab", Assert.Single(plan.Entries, e => !e.Dependency).RelativeTarget.Replace('\\', '/'));
        Assert.Empty(plan.Notes);

        // The prefab, its mesh, its 2 materials, their 4 textures (and the animator's files) are written with their .meta files.
        var result = await exporter.ExtractAsync([hero], new ExportOptions(target, PreserveStructure: true, UnityMeta: true, IncludeReferences: true), ct: Ct);
        Assert.Equal(9, result.Dependencies);
        Assert.Equal(10, result.Written.Count);
        Assert.Equal(10, result.MetaWritten.Count);
        foreach (var path in expected.Append("Assets/Kit/Prefabs/Hero.prefab"))
        {
            var file = Path.Combine(target, path);
            Assert.True(File.Exists(file), file);
            var guid = File.ReadAllText(file + ".meta");
            Assert.Contains("guid: " + G(Path.GetFileNameWithoutExtension(path) switch { "Hero" when path.EndsWith(".controller") => "hero_ctrl", var s => s.ToLowerInvariant() }), guid);
        }
        Assert.False(File.Exists(Path.Combine(target, "Assets/Kit/Textures/unrelated.png"))); // not referenced

        // Without the option only the prefab is written.
        var alone = await exporter.ExtractAsync([hero], new ExportOptions(Path.Combine(kit.Catalog.Dir.Path, "Alone"), true, UnityMeta: true), ct: Ct);
        Assert.Equal((1, 0), (alone.Written.Count, alone.Dependencies));
    }

    [Fact]
    public async Task Folder_source_uses_meta_files_and_an_unavailable_source_is_a_note()
    {
        await using var kit = await ImportKit.CreateAsync();
        var root = Path.Combine(kit.Catalog.Dir.Path, "Proj");
        void Put(string rel, byte[] bytes, string guid)
        {
            var path = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            File.WriteAllText(path + ".meta", $"fileFormatVersion: 2\nguid: {guid}\n");
        }
        Put("Assets/Tex/stone.png", ImportStagingTests.Png(MagickColors.Gray), G("stone"));
        Put("Assets/Tex/stone_n.png", ImportStagingTests.Png(MagickColors.SlateBlue), G("stone_n"));
        Put("Assets/Mat/stone.mat", System.Text.Encoding.UTF8.GetBytes(Material("stone", "stone", "stone_n")), G("stone_mat"));
        await kit.CatalogAsync(new ImportRequest([root], Depth: AiDepth.None));
        long mat;
        await using (var db = kit.Catalog.Db())
            mat = await db.AssetFiles.Where(f => f.Extension == ".mat").Select(f => f.AssetId).SingleAsync(Ct);
        var exporter = new AssetExporter(kit.Store, kit.Processing.Resolver, references: new UnityReferences(kit.Store, kit.Processing.Resolver, kit.Processing.Readers));

        var plan = await exporter.PlanAsync([mat], new ExportOptions(Path.Combine(kit.Catalog.Dir.Path, "Out"), false, IncludeReferences: true), Ct);
        var own = plan.Entries.Where(e => !e.Dependency).Select(e => e.RelativeTarget).ToHashSet();
        // Textures that are already the material's own (companion) files are not listed twice.
        Assert.Equal(["stone.png", "stone_n.png"], plan.Entries.Select(e => e.RelativeTarget).Distinct().Where(t => t != "stone.mat").Order(StringComparer.Ordinal));
        Assert.All(plan.Entries.Where(e => e.Dependency), e => Assert.DoesNotContain(e.RelativeTarget, own));

        // The source folder is gone: the material's own files still plan, and the references are reported.
        Directory.Move(root, root + "-moved");
        var missing = await exporter.PlanAsync([mat], new ExportOptions(Path.Combine(kit.Catalog.Dir.Path, "Out"), false, IncludeReferences: true), Ct);
        Assert.DoesNotContain(missing.Entries, e => e.Dependency);
        Assert.Contains("could not be read", Assert.Single(missing.Notes));
    }

    /// <summary>Spec "GUID already in project": the conflict is listed and nothing is written before the choice.</summary>
    [Fact]
    public async Task Guid_already_in_project()
    {
        await using var kit = await ImportKit.CreateAsync();
        var package = kit.Catalog.Dir.Write("Kit.unitypackage", Package());
        await kit.CatalogAsync(new ImportRequest([package], Depth: AiDepth.None));
        long hero;
        await using (var db = kit.Catalog.Db())
            hero = await db.AssetFiles.Where(f => f.Extension == ".prefab").Select(f => f.AssetId).SingleAsync(Ct);
        var exporter = new AssetExporter(kit.Store, kit.Processing.Resolver, references: new UnityReferences(kit.Store, kit.Processing.Resolver, kit.Processing.Readers));

        // The project already has the wood material (same GUID) at another path.
        var project = Path.Combine(kit.Catalog.Dir.Path, "MyGame");
        Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
        var existing = Path.Combine(project, "Assets", "Old", "wood_v1.mat");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllText(existing, "%YAML 1.1\n");
        File.WriteAllText(existing + ".meta", $"fileFormatVersion: 2\nguid: {G("wood")}\n");
        var target = Path.Combine(project, "Assets", "Imported");
        var options = new ExportOptions(target, PreserveStructure: false, UnityMeta: true, IncludeReferences: true);

        var plan = await exporter.PlanAsync([hero], options, Ct);

        var conflict = Assert.Single(plan.GuidConflicts);
        Assert.Equal(("wood.mat", G("wood"), existing + ".meta"), (conflict.RelativeTarget, conflict.Guid, conflict.ExistingMeta));
        Assert.False(Directory.Exists(target)); // planning writes nothing

        // Skip conflicting: the material is left out, everything else is written.
        var skipped = await exporter.ExtractAsync([hero], options with { SkipGuids = new HashSet<string> { G("wood") } }, ct: Ct);
        Assert.False(File.Exists(Path.Combine(target, "wood.mat")));
        Assert.True(File.Exists(Path.Combine(target, "metal.mat")));
        Assert.Equal(9, skipped.Written.Count);

        // Continue: written anyway (Unity reassigns one side).
        var all = await exporter.ExtractAsync([hero], options with { TargetFolder = Path.Combine(project, "Assets", "Again") }, ct: Ct);
        Assert.True(File.Exists(Path.Combine(project, "Assets", "Again", "wood.mat")));
        Assert.Equal(10, all.Written.Count);

        // Re-exporting the same files to the same place is a name conflict, not a GUID conflict.
        var again = await exporter.PlanAsync([hero], options with { TargetFolder = Path.Combine(project, "Assets", "Again") }, Ct);
        Assert.DoesNotContain(again.GuidConflicts, c => c.RelativeTarget == "metal.mat");
        Assert.Contains("metal.mat", again.Conflicts);
    }

    /// <summary>
    /// A real Synty package (ASSETMANAGER_SAMPLES): the prefab with the most references is exported with its mesh,
    /// materials and textures. Set ASSETMANAGER_UNITY_EXPORT to a Unity project's Assets subfolder to keep the output
    /// for checking in the editor (task 14.4).
    /// </summary>
    [SamplesFact]
    public async Task Real_package_prefab_exports_with_its_references()
    {
        var package = Directory.EnumerateFiles(SamplesFactAttribute.SamplesRoot!, "Simple_Roadwork_Unity_Project*.unitypackage", SearchOption.AllDirectories).FirstOrDefault();
        Assert.SkipWhen(package is null, "Simple_Roadwork package not in the samples.");
        await using var kit = await ImportKit.CreateAsync();
        await kit.CatalogAsync(new ImportRequest([package!], Depth: AiDepth.None));
        List<long> prefabs;
        await using (var db = kit.Catalog.Db())
            prefabs = await db.AssetFiles.Where(f => f.Extension == ".prefab").Select(f => f.AssetId).ToListAsync(Ct);
        Assert.NotEmpty(prefabs);
        var references = new UnityReferences(kit.Store, kit.Processing.Resolver, kit.Processing.Readers);
        var exporter = new AssetExporter(kit.Store, kit.Processing.Resolver, references: references);

        (long Id, int Count) best = (0, -1);
        foreach (var id in prefabs)
        {
            using var deps = await references.FindAsync([id], Ct);
            if (deps.Files.Count > best.Count) best = (id, deps.Files.Count);
        }
        var target = Environment.GetEnvironmentVariable("ASSETMANAGER_UNITY_EXPORT") is { Length: > 0 } keep ? keep : Path.Combine(kit.Catalog.Dir.Path, "Out");
        var result = await exporter.ExtractAsync([best.Id], new ExportOptions(target, PreserveStructure: true, UnityMeta: true, IncludeReferences: true), ct: Ct);

        TestContext.Current.SendDiagnosticMessage($"Exported {string.Join(", ", result.Written.Select(Path.GetFileName))} to {target}");
        Assert.Equal(best.Count, result.Dependencies);
        Assert.Contains(result.Written, w => w.EndsWith(".mat", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Written, w => Path.GetExtension(w).ToLowerInvariant() is ".png" or ".tga" or ".psd" or ".jpg");
        Assert.Contains(result.Written, w => w.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase));
        Assert.All(result.Written, w => Assert.True(File.Exists(w + ".meta"), w));
        Assert.Empty(result.Notes);
    }
}
