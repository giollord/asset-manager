using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assimp;

namespace AssetManager.Processing.Models;

/// <summary>
/// Writes a loaded Assimp scene as binary glTF for the preview renderer. Replaces Assimp's own glTF2 exporter, which
/// crashes the process (native access violation) on some FBX files. Only what previews need is written: the node
/// hierarchy with local transforms, triangle geometry (positions, normals, first UV set, vertex colors) and materials
/// by name with their base color. Textures are not embedded: the renderer applies them by material name.
/// With <c>animated</c>, the skin (joints, weights, inverse bind matrices) and the animation clips are written too, and
/// nodes use translation/rotation/scale so clips can drive them.
/// </summary>
public static class GlbWriter
{
    private const int ArrayBuffer = 34962, ElementArrayBuffer = 34963, Float = 5126, UnsignedInt = 5125, UnsignedShort = 5123;

    public static byte[] Write(Scene scene, bool animated = false)
    {
        var bin = new MemoryStream();
        var bufferViews = new JsonArray();
        var accessors = new JsonArray();

        int View(ReadOnlySpan<byte> data, int target)
        {
            while (bin.Length % 4 != 0) bin.WriteByte(0);
            var offset = (int)bin.Length;
            bin.Write(data);
            var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = data.Length };
            if (target != 0) view["target"] = target;
            bufferViews.Add(view);
            return bufferViews.Count - 1;
        }

        int FloatAccessor(float[] values, int components, string type, bool minMax = false, int target = ArrayBuffer)
        {
            var view = View(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()), target);
            var accessor = new JsonObject
            {
                ["bufferView"] = view, ["componentType"] = Float, ["count"] = values.Length / components, ["type"] = type,
            };
            if (minMax)
            {
                var min = new float[components];
                var max = new float[components];
                Array.Fill(min, float.MaxValue);
                Array.Fill(max, float.MinValue);
                for (var i = 0; i < values.Length; i++)
                {
                    var c = i % components;
                    min[c] = Math.Min(min[c], values[i]);
                    max[c] = Math.Max(max[c], values[i]);
                }
                accessor["min"] = new JsonArray(min.Select(v => (JsonNode)v).ToArray());
                accessor["max"] = new JsonArray(max.Select(v => (JsonNode)v).ToArray());
            }
            accessors.Add(accessor);
            return accessors.Count - 1;
        }

        // One skin for the whole scene: every bone of every mesh, by name.
        var boneIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var inverseBind = new List<Matrix4x4>();
        if (animated)
            foreach (var bone in scene.Meshes.SelectMany(m => m.Bones))
                if (!string.IsNullOrEmpty(bone.Name) && boneIndex.TryAdd(bone.Name, inverseBind.Count)) inverseBind.Add(AssimpMatrix.ToNumerics(bone.OffsetMatrix));

        // One primitive per Assimp mesh (Assimp meshes have a single material); null when it has no triangles.
        var primitives = new JsonObject?[scene.MeshCount];
        var skinned = new bool[scene.MeshCount];
        for (var m = 0; m < scene.MeshCount; m++)
        {
            var mesh = scene.Meshes[m];
            var indices = mesh.Faces.Where(f => f.IndexCount == 3).SelectMany(f => f.Indices).Select(i => (uint)i).ToArray();
            if (indices.Length == 0 || mesh.VertexCount == 0) continue;

            var attributes = new JsonObject
            {
                ["POSITION"] = FloatAccessor(mesh.Vertices.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray(), 3, "VEC3", minMax: true),
            };
            if (mesh.HasNormals && mesh.Normals.Count == mesh.VertexCount)
                attributes["NORMAL"] = FloatAccessor(mesh.Normals.SelectMany(n => Normalized(n)).ToArray(), 3, "VEC3");
            if (mesh.HasTextureCoords(0) && mesh.TextureCoordinateChannels[0].Count == mesh.VertexCount)
                attributes["TEXCOORD_0"] = FloatAccessor(mesh.TextureCoordinateChannels[0].SelectMany(t => new[] { t.X, 1 - t.Y }).ToArray(), 2, "VEC2");
            if (mesh.HasVertexColors(0) && mesh.VertexColorChannels[0].Count == mesh.VertexCount)
                attributes["COLOR_0"] = FloatAccessor(mesh.VertexColorChannels[0].SelectMany(c => new[] { c.X, c.Y, c.Z, c.W }).ToArray(), 4, "VEC4");

            if (animated && mesh.HasBones)
            {
                var (joints, weights) = SkinWeights(mesh, boneIndex);
                attributes["JOINTS_0"] = accessors.Count;
                accessors.Add(new JsonObject
                {
                    ["bufferView"] = View(System.Runtime.InteropServices.MemoryMarshal.AsBytes(joints.AsSpan()), ArrayBuffer),
                    ["componentType"] = UnsignedShort, ["count"] = mesh.VertexCount, ["type"] = "VEC4",
                });
                attributes["WEIGHTS_0"] = FloatAccessor(weights, 4, "VEC4");
                skinned[m] = true;
            }

            var indexView = View(System.Runtime.InteropServices.MemoryMarshal.AsBytes(indices.AsSpan()), ElementArrayBuffer);
            accessors.Add(new JsonObject { ["bufferView"] = indexView, ["componentType"] = UnsignedInt, ["count"] = indices.Length, ["type"] = "SCALAR" });
            primitives[m] = new JsonObject
            {
                ["attributes"] = attributes, ["indices"] = accessors.Count - 1, ["mode"] = 4,
                ["material"] = Math.Clamp(mesh.MaterialIndex, 0, Math.Max(0, scene.MaterialCount - 1)),
            };
        }

        var materials = new JsonArray();
        foreach (var material in scene.Materials)
        {
            var color = material.HasColorDiffuse ? material.ColorDiffuse : Vector4.One;
            var opacity = material.HasOpacity ? Math.Clamp(material.Opacity, 0, 1) : 1;
            var gltf = new JsonObject
            {
                ["name"] = material.Name ?? "",
                ["pbrMetallicRoughness"] = new JsonObject
                {
                    ["baseColorFactor"] = new JsonArray(Clamp01(color.X), Clamp01(color.Y), Clamp01(color.Z), opacity),
                    ["metallicFactor"] = 0f,
                    ["roughnessFactor"] = 0.9f,
                },
            };
            if (opacity < 1) gltf["alphaMode"] = "BLEND";
            materials.Add(gltf);
        }
        if (materials.Count == 0) materials.Add(new JsonObject { ["name"] = "default" });

        var meshes = new JsonArray();
        var nodes = new JsonArray();
        var nodeByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var skinnedNodes = new List<JsonObject>();
        int AddMesh(IEnumerable<int> meshIndices)
        {
            meshes.Add(new JsonObject { ["primitives"] = new JsonArray(meshIndices.Select(i => (JsonNode)primitives[i]!.DeepClone()).ToArray()) });
            return meshes.Count - 1;
        }
        int AddNode(Node node)
        {
            var json = new JsonObject { ["name"] = node.Name ?? "" };
            var t = AssimpMatrix.ToNumerics(node.Transform);
            if (animated && Matrix4x4.Decompose(t, out var scale, out var rotation, out var translation))
            {
                if (translation != Vector3.Zero) json["translation"] = new JsonArray(translation.X, translation.Y, translation.Z);
                if (rotation != Quaternion.Identity) json["rotation"] = new JsonArray(rotation.X, rotation.Y, rotation.Z, rotation.W);
                if (scale != Vector3.One) json["scale"] = new JsonArray(scale.X, scale.Y, scale.Z);
            }
            else if (!t.IsIdentity)
                // System.Numerics uses row vectors (translation in M41..M43); glTF's column-major array of the
                // column-vector matrix is therefore this matrix in row-major order.
                json["matrix"] = Matrix(t);
            var present = node.MeshIndices.Where(i => i >= 0 && i < primitives.Length && primitives[i] is not null).ToList();
            var plain = present.Where(i => !skinned[i]).ToList();
            var skin = present.Where(i => skinned[i]).ToList();
            if (plain.Count > 0) json["mesh"] = AddMesh(plain);
            nodes.Add(json);
            var index = nodes.Count - 1;
            if (!string.IsNullOrEmpty(node.Name)) nodeByName.TryAdd(node.Name, index);
            var children = new List<JsonNode>();
            if (skin.Count > 0)
            {
                // Skinned primitives go on their own node: glTF skins apply per node, and three.js expects every
                // primitive of a skinned mesh to carry weights.
                var skinnedNode = new JsonObject { ["name"] = (node.Name ?? "") + "_skinned", ["mesh"] = AddMesh(skin) };
                nodes.Add(skinnedNode);
                skinnedNodes.Add(skinnedNode);
                children.Add(nodes.Count - 1);
            }
            foreach (var c in node.Children) children.Add(AddNode(c));
            if (children.Count > 0) json["children"] = new JsonArray(children.ToArray());
            return index;
        }
        var root = scene.RootNode is { } r ? AddNode(r) : -1;

        var skins = new JsonArray();
        if (skinnedNodes.Count > 0 && boneIndex.Keys.All(nodeByName.ContainsKey))
        {
            var joints = boneIndex.OrderBy(b => b.Value).Select(b => (JsonNode)nodeByName[b.Key]).ToArray();
            var ibm = FloatAccessor(inverseBind.SelectMany(MatrixValues).ToArray(), 16, "MAT4", target: 0);
            skins.Add(new JsonObject { ["joints"] = new JsonArray(joints), ["inverseBindMatrices"] = ibm });
            foreach (var n in skinnedNodes) n["skin"] = 0;
        }

        var animations = new JsonArray();
        if (animated)
        {
            foreach (var (animation, ai) in scene.Animations.Select((a, i) => (a, i)))
            {
                var tps = animation.TicksPerSecond > 0 ? animation.TicksPerSecond : 25.0;
                var samplers = new JsonArray();
                var channels = new JsonArray();
                void Channel(int node, string path, float[] times, float[] values, int components)
                {
                    if (times.Length == 0) return;
                    var input = FloatAccessor(times, 1, "SCALAR", minMax: true, target: 0);
                    var output = FloatAccessor(values, components, components == 4 ? "VEC4" : "VEC3", target: 0);
                    samplers.Add(new JsonObject { ["input"] = input, ["output"] = output, ["interpolation"] = "LINEAR" });
                    channels.Add(new JsonObject { ["sampler"] = samplers.Count - 1, ["target"] = new JsonObject { ["node"] = node, ["path"] = path } });
                }
                foreach (var ch in animation.NodeAnimationChannels)
                {
                    if (!nodeByName.TryGetValue(ch.NodeName ?? "", out var node)) continue;
                    var pos = Increasing(ch.PositionKeys.Select(k => ((float)(k.Time / tps), k.Value)));
                    Channel(node, "translation", pos.Select(k => k.Time).ToArray(), pos.SelectMany(k => new[] { k.Value.X, k.Value.Y, k.Value.Z }).ToArray(), 3);
                    var rot = Increasing(ch.RotationKeys.Select(k => ((float)(k.Time / tps), Quaternion.Normalize(k.Value))));
                    Channel(node, "rotation", rot.Select(k => k.Time).ToArray(), rot.SelectMany(k => new[] { k.Value.X, k.Value.Y, k.Value.Z, k.Value.W }).ToArray(), 4);
                    var scl = Increasing(ch.ScalingKeys.Select(k => ((float)(k.Time / tps), k.Value)));
                    Channel(node, "scale", scl.Select(k => k.Time).ToArray(), scl.SelectMany(k => new[] { k.Value.X, k.Value.Y, k.Value.Z }).ToArray(), 3);
                }
                if (channels.Count > 0)
                    animations.Add(new JsonObject
                    {
                        ["name"] = string.IsNullOrWhiteSpace(animation.Name) ? $"Animation {ai + 1}" : animation.Name,
                        ["samplers"] = samplers, ["channels"] = channels,
                    });
            }
        }

        while (bin.Length % 4 != 0) bin.WriteByte(0);
        var gltfJson = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "AssetManager" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = root >= 0 ? new JsonArray(root) : new JsonArray() }),
            ["nodes"] = nodes,
            ["meshes"] = meshes,
            ["materials"] = materials,
            ["accessors"] = accessors,
            ["bufferViews"] = bufferViews,
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Length }),
        };
        if (skins.Count > 0) gltfJson["skins"] = skins;
        if (animations.Count > 0) gltfJson["animations"] = animations;
        if (meshes.Count == 0) gltfJson.Remove("meshes");
        if (accessors.Count == 0) { gltfJson.Remove("accessors"); gltfJson.Remove("bufferViews"); gltfJson.Remove("buffers"); }

        var jsonBytes = Encoding.UTF8.GetBytes(gltfJson.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        var jsonLength = (jsonBytes.Length + 3) & ~3;
        var binLength = (int)bin.Length;
        var hasBin = binLength > 0;
        var total = 12 + 8 + jsonLength + (hasBin ? 8 + binLength : 0);

        using var output = new MemoryStream(total);
        using var w = new BinaryWriter(output);
        w.Write(0x46546C67u); // "glTF"
        w.Write(2u);
        w.Write((uint)total);
        w.Write((uint)jsonLength);
        w.Write(0x4E4F534Au); // "JSON"
        w.Write(jsonBytes);
        for (var i = jsonBytes.Length; i < jsonLength; i++) w.Write((byte)' ');
        if (hasBin)
        {
            w.Write((uint)binLength);
            w.Write(0x004E4942u); // "BIN\0"
            w.Write(bin.GetBuffer(), 0, binLength);
        }
        w.Flush();
        return output.ToArray();
    }

    private static float Clamp01(float v) => Math.Clamp(v, 0, 1);

    private static float[] MatrixValues(Matrix4x4 t) =>
        [t.M11, t.M12, t.M13, t.M14, t.M21, t.M22, t.M23, t.M24, t.M31, t.M32, t.M33, t.M34, t.M41, t.M42, t.M43, t.M44];

    private static JsonArray Matrix(Matrix4x4 t) => new(MatrixValues(t).Select(v => (JsonNode)v).ToArray());

    /// <summary>glTF sampler times must strictly increase; files sometimes repeat a key time.</summary>
    private static List<(float Time, T Value)> Increasing<T>(IEnumerable<(float Time, T Value)> keys)
    {
        var result = new List<(float Time, T Value)>();
        foreach (var k in keys)
            if (result.Count == 0 || k.Time > result[^1].Time) result.Add(k);
        return result;
    }

    /// <summary>The four strongest bone weights per vertex, normalized; unweighted vertices follow the first joint.</summary>
    private static (ushort[] Joints, float[] Weights) SkinWeights(Mesh mesh, Dictionary<string, int> boneIndex)
    {
        var influences = new List<(int Joint, float Weight)>[mesh.VertexCount];
        foreach (var bone in mesh.Bones)
        {
            if (!boneIndex.TryGetValue(bone.Name ?? "", out var joint)) continue;
            foreach (var w in bone.VertexWeights)
                if (w.VertexID >= 0 && w.VertexID < influences.Length && w.Weight > 0)
                    (influences[w.VertexID] ??= []).Add((joint, w.Weight));
        }
        var joints = new ushort[mesh.VertexCount * 4];
        var weights = new float[mesh.VertexCount * 4];
        for (var v = 0; v < influences.Length; v++)
        {
            var top = influences[v]?.OrderByDescending(i => i.Weight).Take(4).ToList();
            if (top is not { Count: > 0 })
            {
                weights[v * 4] = 1;
                continue;
            }
            var sum = top.Sum(i => i.Weight);
            for (var k = 0; k < top.Count; k++)
            {
                joints[v * 4 + k] = (ushort)top[k].Joint;
                weights[v * 4 + k] = top[k].Weight / sum;
            }
        }
        return (joints, weights);
    }

    private static float[] Normalized(Vector3 n)
    {
        var length = MathF.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
        return length > 1e-12f ? [n.X / length, n.Y / length, n.Z / length] : [0, 1, 0];
    }
}
