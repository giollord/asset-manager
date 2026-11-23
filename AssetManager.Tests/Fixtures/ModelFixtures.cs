using System.Numerics;
using System.Text;
using Assimp;

namespace AssetManager.Tests.Fixtures;

/// <summary>Builds small model files (box meshes, optional skeleton/animation/texture refs) with Assimp.</summary>
public static class ModelFixtures
{
    public sealed record BoxOptions
    {
        public Vector3 Size { get; init; } = new(1, 1, 1);
        public Matrix4x4 RootTransform { get; init; } = Matrix4x4.Identity;
        /// <summary>Transform of the first-level mesh node (where FBX exporters put unit/scale fixes).</summary>
        public Matrix4x4 NodeTransform { get; init; } = Matrix4x4.Identity;
        public string Material { get; init; } = "Wood";
        public string? DiffuseTexture { get; init; }
        public bool Skeleton { get; init; }
        public string? Animation { get; init; }
    }

    public static Scene Box(BoxOptions o)
    {
        var scene = new Scene();
        var mat = new Material { Name = o.Material, ColorDiffuse = new Vector4(0.8f, 0.6f, 0.4f, 1) };
        if (o.DiffuseTexture is not null)
            mat.AddMaterialTexture(new TextureSlot(o.DiffuseTexture, TextureType.Diffuse, 0, TextureMapping.FromUV, 0, 1, TextureOperation.Add,
                TextureWrapMode.Wrap, TextureWrapMode.Wrap, 0));
        scene.Materials.Add(mat);

        var mesh = new Mesh("Box", PrimitiveType.Triangle) { MaterialIndex = 0 };
        var h = o.Size / 2;
        for (var c = 0; c < 8; c++)
            mesh.Vertices.Add(new Vector3((c & 1) == 0 ? -h.X : h.X, (c & 2) == 0 ? 0 : o.Size.Y, (c & 4) == 0 ? -h.Z : h.Z));
        int[][] quads = [[0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3], [0, 4, 6, 2], [1, 3, 7, 5]];
        foreach (var q in quads)
        {
            mesh.Faces.Add(new Face([q[0], q[1], q[2]]));
            mesh.Faces.Add(new Face([q[0], q[2], q[3]]));
        }
        mesh.TextureCoordinateChannels[0].AddRange(Enumerable.Range(0, 8).Select(i => new Vector3(i & 1, (i >> 1) & 1, 0)));
        mesh.UVComponentCount[0] = 2;
        scene.Meshes.Add(mesh);

        scene.RootNode = new Node("Root") { Transform = AssetManager.Processing.Models.AssimpMatrix.FromNumerics(o.RootTransform) };
        var boxNode = new Node("Box", scene.RootNode) { Transform = AssetManager.Processing.Models.AssimpMatrix.FromNumerics(o.NodeTransform) };
        boxNode.MeshIndices.Add(0);
        scene.RootNode.Children.Add(boxNode);

        if (o.Skeleton)
        {
            var bone = new Node("Hips", scene.RootNode);
            scene.RootNode.Children.Add(bone);
            mesh.Bones.Add(new Bone { Name = "Hips", OffsetMatrix = Matrix4x4.Identity });
            foreach (var i in Enumerable.Range(0, 8)) mesh.Bones[0].VertexWeights.Add(new VertexWeight(i, 1));
        }
        if (o.Animation is not null)
        {
            var anim = new Animation { Name = o.Animation, DurationInTicks = 10, TicksPerSecond = 10 };
            var ch = new NodeAnimationChannel { NodeName = o.Skeleton ? "Hips" : "Box" };
            ch.PositionKeys.Add(new VectorKey(0, Vector3.Zero));
            ch.PositionKeys.Add(new VectorKey(10, Vector3.UnitY));
            ch.RotationKeys.Add(new QuaternionKey(0, Quaternion.Identity));
            ch.ScalingKeys.Add(new VectorKey(0, Vector3.One));
            anim.NodeAnimationChannels.Add(ch);
            scene.Animations.Add(anim);
        }
        return scene;
    }

    /// <summary>Exports with the given Assimp format id ("fbx", "fbxa", "obj", "glb2", "collada").</summary>
    public static string Write(TempDir dir, string fileName, Scene scene, string format)
    {
        var path = Path.Combine(dir.Path, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var ctx = new AssimpContext();
        if (!ctx.ExportFile(scene, path, format)) throw new InvalidOperationException($"Export to {format} failed.");
        return path;
    }

    /// <summary>ASCII FBX with the given GlobalSettings (unit scale in cm per unit, up axis 0/1/2 and sign).</summary>
    public static string WriteFbx(TempDir dir, string fileName, Scene scene, double unitScaleFactor = 1, int upAxis = 1, int upSign = 1)
    {
        // Export to a scratch name and write the patched text as a new file: rewriting the export in place
        // can collide with a virus scanner still holding the fresh file.
        var scratch = Write(dir, fileName + ".export.fbx", scene, "fbxa");
        var path = Path.Combine(dir.Path, fileName);
        var text = ReadWithRetry(scratch);
        text = SetProperty(text, "UnitScaleFactor", unitScaleFactor.ToString(System.Globalization.CultureInfo.InvariantCulture));
        text = SetProperty(text, "UpAxis", upAxis.ToString());
        text = SetProperty(text, "UpAxisSign", upSign.ToString());
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    /// <summary>
    /// Assimp writes through C runtime handles, which ffmpeg processes started by parallel tests can inherit for
    /// their (short) lifetime; wait for them to let go.
    /// </summary>
    private static string ReadWithRetry(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (attempt < 100) { Thread.Sleep(100); }
        }
    }

    private static string SetProperty(string fbx, string name, string value)
    {
        var lines = fbx.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            var key = $"P: \"{name}\",";
            var at = l.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) continue;
            var lastComma = l.LastIndexOf(',');
            lines[i] = l[..(lastComma + 1)] + value + (l.EndsWith('\r') ? "\r" : "");
        }
        return string.Join('\n', lines);
    }
}
