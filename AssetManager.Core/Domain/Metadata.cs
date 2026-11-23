namespace AssetManager.Core.Domain;

public sealed record ImageMetadata
{
    public int Width { get; init; }
    public int Height { get; init; }
    public bool HasAlpha { get; init; }
    public string Format { get; init; } = "";
    public bool IsPixelArt { get; init; }
    public int? ColorCount { get; init; }
    public int? Frames { get; init; }
}

public sealed record AudioMetadata
{
    public double DurationSeconds { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public string Codec { get; init; } = "";
    public long? Bitrate { get; init; }
}

public sealed record FontMetadata
{
    public string? Family { get; init; }
    public string? Style { get; init; }
    public int? GlyphCount { get; init; }
}

public sealed record VideoMetadata
{
    public double DurationSeconds { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string Codec { get; init; } = "";
    public double? FrameRate { get; init; }
}

public sealed record DocumentMetadata
{
    public string Format { get; init; } = "";
    public int? Pages { get; init; }
    public string? Excerpt { get; init; }
}

public sealed record ModelMetadata
{
    public int Vertices { get; init; }
    public int Triangles { get; init; }
    public int Meshes { get; init; }
    public int Materials { get; init; }
    public int Bones { get; init; }
    public bool HasSkeleton => Bones > 0;
    public List<string> Animations { get; init; } = [];
    /// <summary>Length of each animation clip in seconds (same order as <see cref="Animations"/>).</summary>
    public List<double> AnimationSeconds { get; init; } = [];
    public string Format { get; init; } = "";
    /// <summary>Bounding box size in file units (x, y, z).</summary>
    public double[] BoundsSize { get; init; } = [0, 0, 0];
    public ModelTransformInfo Transform { get; init; } = new();
    public ModelOrientation Orientation { get; init; } = new();
    public List<string> MissingTextures { get; init; } = [];
    /// <summary>Orientation probe candidates (labels 1–6) by geometric plausibility, most plausible first; a hint for the AI.</summary>
    public List<int> ProbeRanking { get; init; } = [];
    public string? RenderError { get; init; }
}

/// <summary>Scale analysis of a model (design D6).</summary>
public sealed record ModelTransformInfo
{
    /// <summary>World-space AABB in metres (x, y, z), after unit scale and node transforms.</summary>
    public double[] SizeMeters { get; init; } = [0, 0, 0];
    public double UnitScale { get; init; } = 1;
    public double[] RootScale { get; init; } = [1, 1, 1];
    public bool Uniform { get; init; } = true;
    public ScaleAnomaly Anomaly { get; init; }
    /// <summary>Source up-axis from metadata, e.g. "+Y", "+Z"; null when not declared.</summary>
    public string? SourceUpAxis { get; init; }
}

/// <summary>
/// Upright correction as one of the 24 axis-aligned rotations, expressed as Euler degrees (multiples of 90)
/// applied X then Y then Z as a root transform in the renderer. Never applied to the original file.
/// </summary>
public sealed record ModelOrientation
{
    public int RotX { get; init; }
    public int RotY { get; init; }
    public int RotZ { get; init; }
    public OrientationDecidedBy DecidedBy { get; init; }
    public double Confidence { get; init; }
    public bool Verified { get; init; }
    public bool Uncertain { get; init; }
    /// <summary>The AI answered "no natural up direction".</summary>
    public bool NoNaturalUp { get; init; }

    public bool IsIdentity => RotX % 360 == 0 && RotY % 360 == 0 && RotZ % 360 == 0;

    public string Describe() => IsIdentity
        ? "as authored"
        : $"rotated {string.Join(", ", new[] { ("X", RotX), ("Y", RotY), ("Z", RotZ) }.Where(a => a.Item2 % 360 != 0).Select(a => $"{a.Item2}° about {a.Item1}"))} ({DecidedBy})";
}
