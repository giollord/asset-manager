using System.Numerics;
using AssetManager.Core.Domain;
using AssetManager.Processing.Settings;
using Assimp;
using Assimp.Configs;

namespace AssetManager.Processing.Models;

/// <summary>A model loaded with Assimp, plus what was derived from it.</summary>
public sealed class LoadedModel(Scene scene, string path) : IDisposable
{
    public Scene Scene { get; } = scene;
    public string Path { get; } = path;

    public void Dispose() => Scene.Clear();
}

/// <summary>Loads models with Assimp and computes stats, scale analysis and the metadata-based orientation.</summary>
public static class ModelAnalyzer
{
    private const PostProcessSteps Steps = PostProcessSteps.Triangulate | PostProcessSteps.SortByPrimitiveType;

    /// <summary>
    /// AssimpNet is not safe for concurrent use (shared native state); parallel imports corrupted the process heap
    /// (access violation in coreclr). Every Assimp call in the process goes through this lock.
    /// </summary>
    public static readonly Lock Gate = new();

    /// <exception cref="InvalidDataException">The file cannot be loaded or has no geometry.</exception>
    public static LoadedModel Load(string path)
    {
        Scene? scene;
        try
        {
            lock (Gate)
            {
                using var ctx = new AssimpContext();
                // Keep authored transforms visible: no global scale, no pivot helper nodes.
                ctx.SetConfig(new FBXPreservePivotsConfig(false));
                scene = ctx.ImportFile(path, Steps);
            }
        }
        catch (AssimpException e)
        {
            throw new InvalidDataException($"Cannot load model: {e.Message}", e);
        }
        // Animation-only files (a skeleton and clips, no mesh) are kept; they preview as a stick figure.
        if (scene is null || (!scene.HasMeshes && !scene.HasAnimations)) throw new InvalidDataException("The model contains no meshes.");
        return new LoadedModel(scene, path);
    }

    /// <summary>Writes the loaded scene as binary glTF (GLB) in memory, for the renderer (see <see cref="GlbWriter"/>).</summary>
    /// <param name="animated">Also write the skin and the animation clips (for animation previews and the viewer).</param>
    public static byte[] ToGlb(Scene scene, bool animated = false) => GlbWriter.Write(scene, animated);

    /// <summary>Clips shorter than this are a pose (e.g. a bind-pose take), not an animation.</summary>
    public const double MinClipSeconds = 0.2;

    /// <summary>
    /// Whether a model file is an animation clip rather than a model: a skeleton with no mesh, or a single real clip on
    /// a skinned stand-in character when the file says so — a Unity <c>model@clip</c> name, a Mixamo take, or an
    /// "anim…" folder. Characters with several clips stay models.
    /// </summary>
    public static bool LooksLikeAnimationClip(ModelMetadata meta, string relativePath)
    {
        var real = meta.Animations.Where((_, i) => i >= meta.AnimationSeconds.Count || meta.AnimationSeconds[i] >= MinClipSeconds).ToList();
        if (real.Count == 0) return false;
        if (meta.Meshes == 0) return true;
        if (real.Count != 1 || meta.Bones == 0) return false;
        var path = relativePath.Replace('\\', '/');
        if (System.IO.Path.GetFileNameWithoutExtension(path).Contains('@')) return true;
        if (string.Equals(real[0], "mixamo.com", StringComparison.OrdinalIgnoreCase)) return true;
        var folders = path.Split('/', StringSplitOptions.RemoveEmptyEntries).SkipLast(1);
        return folders.Any(f => f.Contains("anim", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Seconds of a clip (ticks per second defaults to 25 when the file does not say).</summary>
    public static double ClipSeconds(Animation a) => a.DurationInTicks / (a.TicksPerSecond > 0 ? a.TicksPerSecond : 25.0);

    public static ModelMetadata Describe(Scene scene, string extension, PreviewOptions options, ModelOrientation? keepOrientation = null)
    {
        var bones = scene.Meshes.SelectMany(m => m.Bones).Select(b => b.Name).Distinct().Count();
        var (min, max) = WorldBounds(scene);
        var size = max - min;
        var transform = AnalyzeScale(scene, extension, size, options);
        return new ModelMetadata
        {
            Vertices = scene.Meshes.Sum(m => m.VertexCount),
            Triangles = scene.Meshes.Sum(m => m.FaceCount),
            Meshes = scene.MeshCount,
            Materials = scene.MaterialCount,
            Bones = bones,
            Animations = scene.Animations.Select((a, i) => string.IsNullOrWhiteSpace(a.Name) ? $"Animation {i + 1}" : a.Name).ToList(),
            AnimationSeconds = scene.Animations.Select(a => Math.Round(ClipSeconds(a), 3)).ToList(),
            Format = extension.TrimStart('.').ToUpperInvariant(),
            BoundsSize = [Round(size.X), Round(size.Y), Round(size.Z)],
            Transform = transform,
            Orientation = keepOrientation ?? OrientationFromUpAxis(transform.SourceUpAxis),
        };
    }

    /// <summary>Metres per file unit: FBX declares centimetres per unit; glTF is in metres; others are assumed metres.</summary>
    public static double MetresPerUnit(Scene scene, string extension) =>
        extension == ".fbx" ? (MetaDouble(scene, "UnitScaleFactor") ?? 1.0) / 100.0 : 1.0;

    public static ModelTransformInfo AnalyzeScale(Scene scene, string extension, Vector3 sizeUnits, PreviewOptions options)
    {
        var unitScale = extension == ".fbx" ? MetaDouble(scene, "UnitScaleFactor") ?? 1.0 : 1.0;
        var rootScale = RootScale(scene);
        var metres = sizeUnits * (float)MetresPerUnit(scene, extension);
        var largest = Math.Max(metres.X, Math.Max(metres.Y, metres.Z));
        var uniform = Near(rootScale.X, rootScale.Y) && Near(rootScale.Y, rootScale.Z);
        var meanRoot = (rootScale.X + rootScale.Y + rootScale.Z) / 3;

        var anomaly =
            largest < options.ScaleTooSmallMeters ? ScaleAnomaly.TooSmall :
            largest > options.ScaleTooLargeMeters ? ScaleAnomaly.TooLarge :
            !uniform ? ScaleAnomaly.NonUniform :
            meanRoot >= 10 ? ScaleAnomaly.TooLarge :
            meanRoot <= 0.1 ? ScaleAnomaly.TooSmall :
            !Near(meanRoot, 1) || !Near(unitScale, 1) ? ScaleAnomaly.NonUnitScale :
            ScaleAnomaly.None;

        return new ModelTransformInfo
        {
            SizeMeters = [Round(metres.X), Round(metres.Y), Round(metres.Z)],
            UnitScale = unitScale,
            RootScale = [Round(rootScale.X), Round(rootScale.Y), Round(rootScale.Z)],
            Uniform = uniform,
            Anomaly = anomaly,
            SourceUpAxis = UpAxis(scene, extension),
        };
    }

    /// <summary>The axis-aligned rotation that turns the declared up-axis into +Y (identity when unknown or already Y-up).</summary>
    public static ModelOrientation OrientationFromUpAxis(string? up) => up switch
    {
        "+Z" => new ModelOrientation { RotX = 270, DecidedBy = OrientationDecidedBy.Metadata, Confidence = 1 },
        "-Z" => new ModelOrientation { RotX = 90, DecidedBy = OrientationDecidedBy.Metadata, Confidence = 1 },
        "+X" => new ModelOrientation { RotZ = 90, DecidedBy = OrientationDecidedBy.Metadata, Confidence = 1 },
        "-X" => new ModelOrientation { RotZ = 270, DecidedBy = OrientationDecidedBy.Metadata, Confidence = 1 },
        "-Y" => new ModelOrientation { RotX = 180, DecidedBy = OrientationDecidedBy.Metadata, Confidence = 1 },
        "+Y" => new ModelOrientation { DecidedBy = OrientationDecidedBy.Metadata, Confidence = 1 },
        _ => new ModelOrientation(),
    };

    /// <summary>Up axis from the file: FBX <c>UpAxis</c>/<c>UpAxisSign</c> metadata; glTF is Y-up by definition.</summary>
    public static string? UpAxis(Scene scene, string extension)
    {
        if (extension is ".gltf" or ".glb") return "+Y";
        var axis = MetaDouble(scene, "UpAxis");
        if (axis is null) return null;
        var sign = MetaDouble(scene, "UpAxisSign") is < 0 ? "-" : "+";
        return sign + ((int)axis.Value switch { 0 => "X", 2 => "Z", _ => "Y" });
    }

    /// <summary>World-space AABB of all meshes in file units, including node transforms.</summary>
    public static (Vector3 Min, Vector3 Max) WorldBounds(Scene scene)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var local = scene.Meshes.Select(m =>
        {
            if (m.VertexCount == 0) return (Min: Vector3.Zero, Max: Vector3.Zero, Empty: true);
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            foreach (var v in m.Vertices)
            {
                lo = Vector3.Min(lo, v);
                hi = Vector3.Max(hi, v);
            }
            return (lo, hi, false);
        }).ToArray();

        void Visit(Node node, Matrix4x4 parent)
        {
            var world = AssimpMatrix.ToNumerics(node.Transform) * parent;
            foreach (var mi in node.MeshIndices)
            {
                var (lo, hi, empty) = local[mi];
                if (empty) continue;
                for (var c = 0; c < 8; c++)
                {
                    var corner = new Vector3((c & 1) == 0 ? lo.X : hi.X, (c & 2) == 0 ? lo.Y : hi.Y, (c & 4) == 0 ? lo.Z : hi.Z);
                    var p = Vector3.Transform(corner, world);
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
            foreach (var child in node.Children) Visit(child, world);
        }

        Visit(scene.RootNode, Matrix4x4.Identity);
        if (min.X > max.X && !scene.HasMeshes)
        {
            // A skeleton without a mesh: the bounds of the joints in their rest pose.
            void Joints(Node node, Matrix4x4 parent)
            {
                var world = AssimpMatrix.ToNumerics(node.Transform) * parent;
                if (node.Parent is not null)
                {
                    min = Vector3.Min(min, world.Translation);
                    max = Vector3.Max(max, world.Translation);
                }
                foreach (var child in node.Children) Joints(child, world);
            }
            Joints(scene.RootNode, Matrix4x4.Identity);
        }
        return min.X > max.X ? (Vector3.Zero, Vector3.Zero) : (min, max);
    }

    /// <summary>Scale of the root node, times the shared scale of the first-level nodes (exporters put unit fixes there).</summary>
    private static Vector3 RootScale(Scene scene)
    {
        Matrix4x4.Decompose(AssimpMatrix.ToNumerics(scene.RootNode.Transform), out var root, out _, out _);
        var kids = scene.RootNode.Children.Where(c => c.HasMeshes || c.HasChildren).ToList();
        if (kids.Count == 0) return Abs(root);
        var scales = kids.Select(k => { Matrix4x4.Decompose(AssimpMatrix.ToNumerics(k.Transform), out var s, out _, out _); return Abs(s); }).ToList();
        var first = scales[0];
        return scales.All(s => Near(s.X, first.X) && Near(s.Y, first.Y) && Near(s.Z, first.Z)) ? Abs(root) * first : Abs(root);
    }

    private static Vector3 Abs(Vector3 v) => Vector3.Abs(v);

    private static double? MetaDouble(Scene scene, string key)
    {
        if (scene.Metadata is null || !scene.Metadata.TryGetValue(key, out var e)) return null;
        return e.Data switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            uint u => u,
            ulong ul => ul,
            string s when double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var p) => p,
            _ => null,
        };
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) <= 1e-3 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));

    private static double Round(double v) => Math.Round(v, 5);
}
