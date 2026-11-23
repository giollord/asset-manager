using System.Text.Json;
using AssetManager.AI.Enrichment;
using AssetManager.Core.Domain;
using AssetManager.Processing.Containers;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Settings;
using AssetManager.Tests.Fixtures;
using AssetManager.Tests.Processing;
using ImageMagick;
using Microsoft.Extensions.AI;

namespace AssetManager.Tests.Ai;

[Collection(AiModelCollection.Name)]
public class PackPassTests
{
    private static readonly FileClassifier Classifier = new(new ClassificationOptions());

    /// <summary>Classifies walked files the way the import scan does: assets and noted files, documents read separately.</summary>
    internal static (List<PackFile> Files, List<SourceDocument> Docs, List<AiImage> Samples) Scan(IEnumerable<VfsNode> nodes)
    {
        var files = new List<PackFile>();
        var docs = new List<SourceDocument>();
        var samples = new List<AiImage>();
        foreach (var n in nodes.Where(n => n.Kind == VfsNodeKind.File))
        {
            var c = Classifier.Classify(n.RelativePath);
            switch (c.Kind)
            {
                case FileKind.SourceDocument:
                    docs.Add(DocumentExtractor.Extract(n.LocalPath, n.RelativePath));
                    break;
                case FileKind.Asset or FileKind.Noted:
                    files.Add(new PackFile(n.RelativePath, c.Kind, c.Type));
                    if (c.Kind == FileKind.Asset && n.PreviewPath is not null && samples.Count < PackPass.MaxSamples
                        && samples.All(s => Path.GetDirectoryName(s.Label) != Path.GetDirectoryName(n.RelativePath)))
                        samples.Add(new AiImage("image/png", File.ReadAllBytes(n.PreviewPath), n.RelativePath));
                    break;
            }
        }
        return (files, docs, samples);
    }

    private static byte[] Png(MagickColor color)
    {
        using var img = new MagickImage(color, 32, 32);
        return img.ToByteArray(MagickFormat.Png);
    }

    [Fact]
    public async Task Package_with_scripts_is_noted_not_imported()
    {
        using var kit = new ProcessingKit();
        var entries = new List<UnityPackageEntry>();
        for (var i = 0; i < 40; i++)
            entries.Add(new(FixtureBuilder.Guid("cs" + i), $"Assets/Kit/Scripts/Controller{i}.cs", "public class C {}"u8.ToArray()));
        entries.Add(new(FixtureBuilder.Guid("asmdef"), "Assets/Kit/Scripts/Kit.asmdef", "{}"u8.ToArray()));
        entries.Add(new(FixtureBuilder.Guid("crate"), "Assets/Kit/Models/Crate.fbx", [1, 2, 3], Preview: Png(MagickColors.Brown)));
        entries.Add(new(FixtureBuilder.Guid("tex"), "Assets/Kit/Textures/Crate_Albedo.png", Png(MagickColors.Brown)));
        entries.Add(new(FixtureBuilder.Guid("lic"), "Assets/Kit/License.txt", "Free for commercial use. No credit required."u8.ToArray()));
        var package = kit.Dir.Write("Kit.unitypackage", FixtureBuilder.UnityPackage(entries, new AssetStoreHeader("123", "Crate Kit", "Acme", "1.2")));

        var nodes = await kit.WalkAsync(package);
        var (files, docs, samples) = Scan(nodes);

        Assert.DoesNotContain(files, f => f.Kind == FileKind.Asset && f.RelativePath.EndsWith(".cs"));
        Assert.Equal(2, files.Count(f => f.Kind == FileKind.Asset));
        Assert.Equal(41, files.Count(f => f.Kind == FileKind.Noted));

        var store = nodes.First().Store ?? new StoreInfo { StoreId = "123", Title = "Crate Kit", Publisher = "Acme", Version = "1.2" };
        var input = new PackInput("Crate Kit", SourceKind.UnityPackage, files, docs, samples, store, "Tag everything as 'prototype'.");
        var client = new FakeChatClient("""
            {"description":"Wooden crate props with a character controller.","license":{"kind":"Custom (free)","commercial":"yes","attribution":"no","attribution_text":null,"evidence":"License.txt"},
             "links":[],"script_notes":"The scripts implement a character controller.","style":["Stylized"],"tags":["Prototype","props"," props"]}
            """);

        var result = await PackPass.RunAsync(new StructuredChat(client), input, vision: true, TestContext.Current.CancellationToken);
        var source = new Source();
        var defaults = PackPass.Apply(source, input, result);

        var prompt = client.Requests[0].Messages[^1];
        var text = string.Join("\n", prompt.Contents.OfType<TextContent>().Select(t => t.Text));
        Assert.Contains("41 code/project file", PackPass.Notes(files, null));
        Assert.Contains("40 .cs, 1 .asmdef in Assets/", text);
        Assert.Contains("Controller0.cs", text);
        Assert.Contains("Instruction from the user (follow it): Tag everything as 'prototype'.", text);
        Assert.Contains("Asset Store: \"Crate Kit\" by Acme, version 1.2", text);
        Assert.Contains("=== Assets/Kit/License.txt ===", text);
        Assert.Contains("1 Model3D", text);
        var image = Assert.Single(prompt.Contents.OfType<DataContent>());
        Assert.Equal("image/png", image.MediaType);

        Assert.Equal("Includes 41 code/project files (40 .cs, 1 .asmdef in Assets/). The scripts implement a character controller.", source.Notes);
        Assert.Equal("Wooden crate props with a character controller.", source.Description);
        Assert.Equal(("Assets/Kit/License.txt", TriState.Yes), (source.License.Evidence, source.License.Commercial));
        Assert.Equal(["prototype", "props"], defaults.Tags);
        Assert.Equal(["stylized"], defaults.Style);
    }

    [Fact]
    public void Pack_without_code_gets_no_notes_and_webp_samples_become_jpeg()
    {
        Assert.Null(PackPass.Notes([new PackFile("a.png", FileKind.Asset, AssetType.Image)], "nothing"));

        using var img = new MagickImage(MagickColors.Transparent, 16, 16);
        var webp = new AiImage("image/webp", img.ToByteArray(MagickFormat.WebP), "a.png");
        var data = PackPass.ToModelImage(webp);
        Assert.Equal("image/jpeg", data.MediaType);

        var input = new PackInput("Icons", SourceKind.Folder, [new PackFile("UI/a.png", FileKind.Asset, AssetType.Image)], [], [webp]);
        var noVision = PackPass.BuildPrompt(input, vision: false).Single();
        Assert.Empty(noVision.Contents.OfType<DataContent>());
        Assert.Contains("Code/project files: none", noVision.Text);
        Assert.Contains("UI/: 1 Image; e.g. a.png", noVision.Text);
        var user = new Source { UserEdited = EditedFields.Description | EditedFields.Notes, Description = "Mine", Notes = "My notes" };
        PackPass.Apply(user, input, new PackResult { Description = "AI" });
        Assert.Equal(("Mine", "My notes"), (user.Description, user.Notes));
    }

    /// <summary>Manual check of the pack prompt on a real Asset Store package with scripts (needs the samples and a model).</summary>
    [AiModelFact]
    public async Task Real_model_summarizes_a_sample_package()
    {
        Assert.SkipWhen(SamplesFactAttribute.SamplesRoot is null, "Set ASSETMANAGER_SAMPLES to run.");
        var path = Path.Combine(SamplesFactAttribute.SamplesRoot!, "Unity Asset Store Downloaded", "Deprecated", "Sensor Toolkit.unitypackage");
        Assert.SkipWhen(!File.Exists(path), "Sample package not found.");

        using var kit = new ProcessingKit();
        var nodes = await kit.WalkAsync(path);
        var (files, docs, samples) = Scan(nodes);
        var store = nodes.Select(n => n.Store).FirstOrDefault(s => s is not null);
        var input = new PackInput(Path.GetFileNameWithoutExtension(path), SourceKind.UnityPackage, files, docs, samples, store);

        await using var server = await AiTestServer.StartAsync(vision: AiModelFactAttribute.Mmproj is not null);
        var started = DateTime.UtcNow;
        var result = await PackPass.RunAsync(new StructuredChat(server.Client), input, server.Vision, TestContext.Current.CancellationToken);
        var source = new Source();
        var defaults = PackPass.Apply(source, input, result);

        var report = JsonSerializer.Serialize(new { seconds = (DateTime.UtcNow - started).TotalSeconds, source.Description, source.Notes, source.License, source.Links, defaults },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "am-pack-pass.json"), report);
        Assert.False(string.IsNullOrWhiteSpace(source.Description), report);
        Assert.StartsWith("Includes ", source.Notes);
    }
}
