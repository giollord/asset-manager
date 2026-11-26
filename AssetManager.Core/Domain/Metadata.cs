namespace AssetManager.Core.Domain;

public sealed record ImageMetadata
{
    public int Width { get; init; }
    public int Height { get; init; }
    public bool HasAlpha { get; init; }
    public string Format { get; init; } = "";
    public bool IsPixelArt { get; init; }
    /// <summary>Nearest-neighbour upscale factor of pixel art (1 = native), from <c>PixelArtAnalyzer</c>.</summary>
    public int? PixelScale { get; init; }
    public int? ColorCount { get; init; }
    public int? Frames { get; init; }
}

/// <summary>One frame of a sprite sheet, in original pixels (top-left origin).</summary>
public sealed record SpriteFrame(string? Name, int X, int Y, int W, int H);

/// <summary>A regular sprite grid, in original pixels.</summary>
public sealed record SpriteGrid
{
    public int CellW { get; init; }
    public int CellH { get; init; }
    public int OffsetX { get; init; }
    public int OffsetY { get; init; }
    public int SpacingX { get; init; }
    public int SpacingY { get; init; }
    public int Columns { get; init; }
    public int Rows { get; init; }

    /// <summary>Rectangle of the cell at <paramref name="col"/>, <paramref name="row"/>.</summary>
    public SpriteFrame Cell(int col, int row, string? name = null) =>
        new(name, OffsetX + col * (CellW + SpacingX), OffsetY + row * (CellH + SpacingY), CellW, CellH);

    /// <summary>The same cell size, offset and spacing with as many columns and rows as fit an image of this size.</summary>
    public SpriteGrid FitTo(int width, int height) => this with
    {
        Columns = CellW <= 0 ? 0 : Math.Max(0, (width - OffsetX + SpacingX) / (CellW + SpacingX)),
        Rows = CellH <= 0 ? 0 : Math.Max(0, (height - OffsetY + SpacingY) / (CellH + SpacingY)),
    };

    /// <summary>Whether every cell lies inside an image of this size.</summary>
    public bool FitsIn(int width, int height) =>
        CellW > 0 && CellH > 0 && Columns > 0 && Rows > 0 && OffsetX >= 0 && OffsetY >= 0 && SpacingX >= 0 && SpacingY >= 0 &&
        OffsetX + Columns * CellW + (Columns - 1) * SpacingX <= width && OffsetY + Rows * CellH + (Rows - 1) * SpacingY <= height;
}

/// <summary>Sprite sheet frames (design D3): slicing data gives irregular frames, a grid gives regular ones.</summary>
public sealed record SpriteSheetMetadata
{
    /// <summary>The non-empty frames, in reading order.</summary>
    public List<SpriteFrame> Frames { get; init; } = [];
    /// <summary>The grid form, when the frames lie on a regular grid.</summary>
    public SpriteGrid? Grid { get; init; }
    public int FrameCount { get; init; }
    public double Confidence { get; init; }
    public GridDetectedBy DetectedBy { get; init; }

    /// <summary>The first <paramref name="frameCount"/> cells of the grid in reading order (all cells when null).</summary>
    public static SpriteSheetMetadata FromGrid(SpriteGrid grid, int? frameCount, GridDetectedBy by, double confidence = 1)
    {
        var count = Math.Clamp(frameCount ?? grid.Columns * grid.Rows, 0, grid.Columns * grid.Rows);
        var frames = Enumerable.Range(0, count).Select(i => grid.Cell(i % grid.Columns, i / grid.Columns)).ToList();
        return new SpriteSheetMetadata { Frames = frames, Grid = grid, FrameCount = count, Confidence = confidence, DetectedBy = by };
    }
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
    /// <summary>Texture choices of the AI below the match threshold, offered in the details drawer (design D20).</summary>
    public List<TextureSuggestion> TextureSuggestions { get; init; } = [];
}

/// <summary>
/// A texture the AI suggests for one material of a model. <see cref="FileId"/> is the catalog file (a texture asset's
/// primary file or another model's companion) the suggestion points at.
/// </summary>
public sealed record TextureSuggestion(string Material, long FileId, string RelativePath, double Confidence, string Reason);

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
