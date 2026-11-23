using System.Numerics;
using AssetManager.Core.Domain;
using Assimp;

namespace AssetManager.Processing.Models;

/// <summary>
/// The six up-axis candidates of the orientation probe (labels 1–6, same order as the JS renderer) and a geometric
/// pre-ranking given to the AI as a hint: models are usually taller along "up" than their footprint, and their mass
/// sits low.
/// </summary>
public static class OrientationProbe
{
    public sealed record Candidate(int Label, string UpAxis, int RotX, int RotY, int RotZ)
    {
        public ModelOrientation ToOrientation(OrientationDecidedBy by, double confidence) =>
            new() { RotX = RotX, RotY = RotY, RotZ = RotZ, DecidedBy = by, Confidence = confidence };
    }

    public static readonly IReadOnlyList<Candidate> Candidates =
    [
        new(1, "+Y", 0, 0, 0),
        new(2, "-Y", 180, 0, 0),
        new(3, "+Z", 270, 0, 0),
        new(4, "-Z", 90, 0, 0),
        new(5, "+X", 0, 0, 90),
        new(6, "-X", 0, 0, 270),
    ];

    /// <summary>The rotation applied by the renderer: X, then Y, then Z (degrees).</summary>
    public static Matrix4x4 Rotation(int rotX, int rotY, int rotZ) =>
        Matrix4x4.CreateRotationX(Rad(rotX)) * Matrix4x4.CreateRotationY(Rad(rotY)) * Matrix4x4.CreateRotationZ(Rad(rotZ));

    /// <summary>Candidate labels, most plausible first.</summary>
    public static IReadOnlyList<int> Rank(Scene scene, int maxSamples = 20000)
    {
        var points = WorldVertices(scene, maxSamples);
        if (points.Count == 0) return Candidates.Select(c => c.Label).ToList();
        return Candidates
            .Select(c => (c.Label, Score: Score(points, Rotation(c.RotX, c.RotY, c.RotZ))))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Label)
            .Select(x => x.Label)
            .ToList();
    }

    private static double Score(List<Vector3> points, Matrix4x4 rotation)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var sumY = 0.0;
        foreach (var p in points)
        {
            var r = Vector3.Transform(p, rotation);
            min = Vector3.Min(min, r);
            max = Vector3.Max(max, r);
            sumY += r.Y;
        }
        var size = max - min;
        var height = Math.Max(size.Y, 1e-6);
        var footprint = Math.Max(Math.Max(size.X, size.Z), 1e-6);
        var tallness = Math.Min(height / footprint, 3) / 3;          // 0..1, taller is more plausible
        var lowMass = 1 - ((sumY / points.Count) - min.Y) / height;  // 0..1, a low centre of mass is more plausible
        return Math.Round(0.5 * tallness + 0.5 * lowMass, 6);
    }

    private static List<Vector3> WorldVertices(Scene scene, int maxSamples)
    {
        var total = Math.Max(1, scene.Meshes.Sum(m => m.VertexCount));
        var step = Math.Max(1, total / maxSamples);
        var points = new List<Vector3>(Math.Min(total, maxSamples + 64));

        void Visit(Node node, Matrix4x4 parent)
        {
            var world = AssimpMatrix.ToNumerics(node.Transform) * parent;
            foreach (var mi in node.MeshIndices)
            {
                var verts = scene.Meshes[mi].Vertices;
                for (var i = 0; i < verts.Count; i += step) points.Add(Vector3.Transform(verts[i], world));
            }
            foreach (var child in node.Children) Visit(child, world);
        }

        Visit(scene.RootNode, Matrix4x4.Identity);
        return points;
    }

    private static float Rad(int degrees) => degrees * MathF.PI / 180f;
}
