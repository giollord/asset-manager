using System.Numerics;
using AssetManager.Processing.Models;
using AssetManager.Tests.Fixtures;
using static AssetManager.Tests.Fixtures.ModelFixtures;

namespace AssetManager.Tests.Processing;

/// <summary>The managed GLB the renderer gets (Assimp's glTF2 exporter crashed the process on some FBX files).</summary>
public class GlbWriterTests
{
    [Fact]
    public void Round_trip_keeps_geometry_transforms_uvs_and_material_names()
    {
        using var dir = FixtureBuilder.TempDir();
        var source = Write(dir, "box.fbx", Box(new BoxOptions
        {
            Size = new(2, 3, 4), Material = "Bark", Skeleton = true, Animation = "Walk",
            RootTransform = Matrix4x4.CreateTranslation(0, 5, 0) * Matrix4x4.CreateScale(2),
        }), "fbx");
        using var original = ModelAnalyzer.Load(source);

        var glb = ModelAnalyzer.ToGlb(original.Scene);
        var path = Path.Combine(dir.Path, "out.glb");
        File.WriteAllBytes(path, glb);
        using var copy = ModelAnalyzer.Load(path);

        Assert.Equal("glTF"u8.ToArray(), glb[..4]);
        Assert.Equal(original.Scene.Meshes.Sum(m => m.FaceCount), copy.Scene.Meshes.Sum(m => m.FaceCount));
        var (min, max) = ModelAnalyzer.WorldBounds(original.Scene);
        var (cmin, cmax) = ModelAnalyzer.WorldBounds(copy.Scene);
        Assert.True(Vector3.Distance(min, cmin) < 1e-3 && Vector3.Distance(max, cmax) < 1e-3, $"{min} {max} vs {cmin} {cmax}");
        Assert.Contains(copy.Scene.Materials, m => m.Name == "Bark");
        Assert.Equal(new Vector4(0.8f, 0.6f, 0.4f, 1), copy.Scene.Materials.First(m => m.Name == "Bark").ColorDiffuse);
        // UVs are written in glTF's top-left convention; Assimp's importer flips them back.
        var uvs = original.Scene.Meshes[0].TextureCoordinateChannels[0].Select(t => (t.X, t.Y)).Order().ToList();
        var back = copy.Scene.Meshes[0].TextureCoordinateChannels[0].Select(t => (t.X, t.Y)).Order().ToList();
        Assert.Equal(uvs.Distinct().Count(), back.Distinct().Count());
        Assert.All(back, uv => Assert.Contains(uvs, o => Math.Abs(o.X - uv.X) < 1e-5 && Math.Abs(o.Y - uv.Y) < 1e-5));
    }
}

/// <summary>Node transforms against a file written by hand (AssimpNetter keeps Assimp's matrix layout).</summary>
public class AssimpMatrixLayoutTests
{
    [Fact]
    public void Translated_node_moves_bounds_and_glb_matrix()
    {
        using var dir = FixtureBuilder.TempDir();
        // One triangle at y in [0, 1], on a node translated +5 in Y and rotated 90° about Z.
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        var bytes = new byte[positions.Length * 4];
        Buffer.BlockCopy(positions, 0, bytes, 0, bytes.Length);
        var gltf = $$$"""
        {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],
         "nodes":[{"mesh":0,"translation":[0,5,0],"rotation":[0,0,0.70710678,0.70710678]}],
         "meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],
         "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[1,1,0]}],
         "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":{{{bytes.Length}}}}],
         "buffers":[{"byteLength":{{{bytes.Length}}},"uri":"data:application/octet-stream;base64,{{{Convert.ToBase64String(bytes)}}}"}]}
        """;
        var path = Path.Combine(dir.Path, "moved.gltf");
        File.WriteAllText(path, gltf);
        using var model = ModelAnalyzer.Load(path);

        var (min, max) = ModelAnalyzer.WorldBounds(model.Scene);
        // Rotating 90° about Z maps x → y and y → -x: the triangle spans x in [-1, 0], y in [5, 6].
        Assert.True(Vector3.Distance(min, new Vector3(-1, 5, 0)) < 1e-3 && Vector3.Distance(max, new Vector3(0, 6, 0)) < 1e-3, $"{min} {max}");

        var glb = ModelAnalyzer.ToGlb(model.Scene);
        var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Encoding.UTF8.GetString(glb, 20, BitConverter.ToInt32(glb, 12)))!;
        var matrix = json["nodes"]!.AsArray().Select(n => n!["matrix"]).First(m => m is not null)!.AsArray().Select(v => v!.GetValue<float>()).ToArray();
        Assert.Equal([0f, 5f, 0f], matrix[12..15].Select(v => MathF.Round(v, 4)).ToArray()); // column-major: translation last
    }
}
