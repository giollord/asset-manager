using AssetManager.Core.Domain;
using ImageMagick;

namespace AssetManager.Processing.Sprites;

/// <summary>The AI's sprite-sheet answer (design D3 step 3): only a candidate the detector scores like any other.</summary>
public sealed record SpriteSheetHint(bool Likely, int? Columns = null, int? Rows = null, int? Frames = null);

/// <summary>
/// What the grid detector found. <see cref="Sheet"/> is set when a grid was found with enough confidence;
/// <see cref="Uncertain"/> when the image looks like several sprites but no grid could be trusted.
/// </summary>
/// <param name="SeveralSprites">The pixels show separate sprites (gutters), whatever the grid.</param>
public sealed record GridResult(SpriteSheetMetadata? Sheet, bool Uncertain, double Confidence, bool SeveralSprites = false)
{
    public static readonly GridResult None = new(null, false, 0);
}

/// <summary>
/// Finds a sprite grid from the pixels (design D3 step 2, spec "Algorithmic grid detection"). Images larger than 4096 px
/// are analysed downsampled. Each axis is solved on its own:
/// <list type="bullet">
/// <item><b>Gutter method</b>: columns (rows) that are entirely "empty" (alpha 0, or the corner background colour) split
/// the content into segments; the pitch is the candidate cell size whose cells each hold exactly one segment, with the
/// highest share of occupied cells. Smaller sizes that only add empty cells lose.</item>
/// <item><b>Packed method</b>, when there are no gutters: candidate cell sizes are the divisors of the length in 8-512 px
/// plus the hint. A size is scored by how much stronger the colour change across its cell borders is than inside
/// cells. The smallest size with nearly the best score wins, so multiples of the true size do not.</item>
/// </list>
/// The confidence is that of the weakest multi-cell axis. Below the threshold no grid is stored.
/// </summary>
public static class GridDetector
{
    public const int MaxAnalysedEdge = 4096;
    public const int MinCell = 8;
    public const int MaxCell = 512;
    private const int BackgroundTolerance = 8;

    /// <summary>
    /// Without gutters, a packed grid needs this many cells, at least 3 along each multi-cell axis, and cells no more
    /// than <see cref="PackedMaxAspect"/> times longer than wide; otherwise only an AI hint naming the same counts
    /// confirms it. Tuned on the sample library: regular lines inside single tiles (bricks, planks) and isometric
    /// renders otherwise read as 1×4, 2×2 or 2×1 packed grids.
    /// </summary>
    public const int PackedMinCells = 6;
    public const int PackedMaxAspect = 4;

    /// <summary>Images below this edge length are single tiles, never sheets or "uncertain" (matching corners of a 16 px tile read as gutters).</summary>
    public const int MinPackedEdge = 32;

    private sealed record Axis(int Pitch, int Offset, int Cells, double Confidence);

    public static GridResult Detect(MagickImage image, double threshold = 0.6, SpriteSheetHint? hint = null)
    {
        int w0 = (int)image.Width, h0 = (int)image.Height;
        var scale = Math.Max(w0, h0) > MaxAnalysedEdge ? (double)MaxAnalysedEdge / Math.Max(w0, h0) : 1;
        using var work = (MagickImage)image.Clone();
        if (scale < 1)
        {
            work.FilterType = FilterType.Point;
            work.Resize((uint)Math.Round(w0 * scale), (uint)Math.Round(h0 * scale));
        }
        int w = (int)work.Width, h = (int)work.Height;
        var rgba = work.GetPixelsUnsafe().ToByteArray(PixelMapping.RGBA) ?? [];
        var empty = EmptyMask(rgba, w, h);
        // A frame count alone means one row (or one column for a tall image).
        var cols = hint?.Columns ?? (hint?.Rows is null && w >= h ? hint?.Frames : null);
        var rows = hint?.Rows ?? (hint?.Columns is null && h > w ? hint?.Frames : null);
        int? hintW = cols is > 0 and var c ? (int)Math.Round((double)w / c) : null;
        int? hintH = rows is > 0 and var r ? (int)Math.Round((double)h / r) : null;

        Axis? ax, ay;
        var colEmpty = new bool[w];
        var rowEmpty = new bool[h];
        if (empty is not null)
        {
            for (var x = 0; x < w; x++) colEmpty[x] = true;
            for (var y = 0; y < h; y++) rowEmpty[y] = true;
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    if (!empty[y * w + x]) colEmpty[x] = rowEmpty[y] = false;
        }
        if (w0 < MinCell || h0 < MinCell || Math.Max(w0, h0) < MinPackedEdge) return GridResult.None; // a single tile
        var segX = Segments(colEmpty);
        var segY = Segments(rowEmpty);
        var gutters = empty is not null && (segX.Count >= 2 || segY.Count >= 2);
        if (gutters)
        {
            ax = GutterAxis(segX, w, hintW);
            ay = GutterAxis(segY, h, hintH);
        }
        else
        {
            var (dx, dy) = Changes(rgba, w, h);
            ax = PackedAxis(dx, hintW);
            ay = PackedAxis(dy, hintH);
            if (ax is not null && ay is not null && !PackedPlausible(ax, ay, cols, rows)) ax = ay = null;
        }

        // Evidence of several sprites, but no grid that explains them: review, do not guess.
        var looksLikeSprites = gutters || hint?.Likely == true;
        if (ax is null || ay is null || ax.Cells * ay.Cells < 2)
            return looksLikeSprites ? new GridResult(null, true, 0, gutters) : GridResult.None;
        var confidence = new[] { ax, ay }.Where(a => a.Cells > 1).Min(a => a.Confidence);
        if (confidence < threshold) return looksLikeSprites ? new GridResult(null, true, confidence, gutters) : GridResult.None;

        var grid = new SpriteGrid
        {
            CellW = Up(ax.Pitch), CellH = Up(ay.Pitch), OffsetX = Up(ax.Offset), OffsetY = Up(ay.Offset), Columns = ax.Cells, Rows = ay.Cells,
        };
        var frames = new List<SpriteFrame>();
        for (var row = 0; row < ay.Cells; row++)
            for (var col = 0; col < ax.Cells; col++)
                if (empty is null || !CellEmpty(empty, w, ax.Offset + col * ax.Pitch, ay.Offset + row * ay.Pitch, ax.Pitch, ay.Pitch))
                    frames.Add(grid.Cell(col, row));
        if (frames.Count < 2) return looksLikeSprites ? new GridResult(null, true, confidence, gutters) : GridResult.None;
        return new GridResult(new SpriteSheetMetadata
        {
            Frames = frames, Grid = grid, FrameCount = frames.Count, Confidence = Math.Round(confidence, 3), DetectedBy = GridDetectedBy.Algorithm,
        }, false, confidence, gutters);

        int Up(int v) => scale < 1 ? (int)Math.Round(v / scale) : v;
    }

    /// <summary>Transparent pixels, or pixels matching the shared corner colour; null when neither applies.</summary>
    private static bool[]? EmptyMask(byte[] rgba, int w, int h)
    {
        var mask = new bool[w * h];
        int[] corners = [0, w - 1, (h - 1) * w, (h - 1) * w + w - 1];
        if (corners.Any(i => rgba[i * 4 + 3] == 0) || rgba.Where((_, i) => i % 4 == 3).Any(a => a == 0))
        {
            var any = false;
            for (var i = 0; i < mask.Length; i++) any |= mask[i] = rgba[i * 4 + 3] < 8;
            if (any) return mask;
        }
        var bg = corners[0] * 4;
        bool Same(int i) => Math.Abs(rgba[i * 4] - rgba[bg]) <= BackgroundTolerance && Math.Abs(rgba[i * 4 + 1] - rgba[bg + 1]) <= BackgroundTolerance &&
                            Math.Abs(rgba[i * 4 + 2] - rgba[bg + 2]) <= BackgroundTolerance;
        if (!corners.All(Same)) return null;
        for (var i = 0; i < mask.Length; i++) mask[i] = Same(i);
        return mask;
    }

    /// <summary>Runs of non-empty positions: (start, end exclusive).</summary>
    private static List<(int Start, int End)> Segments(bool[] emptyLine)
    {
        var list = new List<(int, int)>();
        var start = -1;
        for (var i = 0; i <= emptyLine.Length; i++)
        {
            var content = i < emptyLine.Length && !emptyLine[i];
            if (content && start < 0) start = i;
            else if (!content && start >= 0) { list.Add((start, i)); start = -1; }
        }
        return list;
    }

    private static Axis? GutterAxis(List<(int Start, int End)> segments, int length, int? hint)
    {
        if (segments.Count == 0) return null;
        if (segments.Count == 1) return new Axis(length, 0, 1, 1); // one row (column) of cells
        var candidates = Divisors(length).ToHashSet();
        if (hint is >= MinCell) candidates.Add(hint.Value);
        var centres = segments.Select(s => (s.Start + s.End) / 2.0).ToList();
        var steps = centres.Zip(centres.Skip(1), (a, b) => b - a).Order().ToList();
        var natural = (int)Math.Round(steps[steps.Count / 2]);
        candidates.Add(natural);

        var scored = new List<(Axis Axis, double Occupancy)>();
        foreach (var p in candidates.Where(p => p >= MinCell && p <= Math.Min(MaxCell, length)))
        {
            // Offsets that put every cell border into a gutter; prefer 0, else the middle of the first gutter.
            var valid = Enumerable.Range(0, Math.Min(p, segments[0].Start + 1)).Where(o => Fits(segments, p, o)).ToList();
            if (valid.Count == 0) continue;
            var offset = valid.Contains(0) ? 0 : valid[valid.Count / 2];
            var cells = Math.Max((segments[^1].End - 1 - offset) / p + 1, (length - offset) / p);
            if (offset + cells * p > length + p / 4) continue; // the grid would run past the image
            scored.Add((new Axis(p, offset, cells, 0), (double)segments.Count / cells));
        }
        if (scored.Count == 0) return null;
        // Equal occupancy: the size closest to the spacing of the sprites' centres.
        var best = scored.OrderByDescending(s => s.Occupancy).ThenBy(s => Math.Abs(s.Axis.Pitch - natural)).First();
        // A rival describes a different grid: another cell count, and not a divisor or multiple of the winner.
        var rival = scored.Where(s => s.Axis.Cells != best.Axis.Cells && best.Axis.Pitch % s.Axis.Pitch != 0 && s.Axis.Pitch % best.Axis.Pitch != 0)
            .Select(s => s.Occupancy).DefaultIfEmpty(0).Max();
        return best.Axis with { Confidence = best.Occupancy * (1 - rival / best.Occupancy * 0.5) };
    }

    /// <summary>Every segment lies inside one cell, and no cell holds two segments.</summary>
    private static bool Fits(List<(int Start, int End)> segments, int p, int offset)
    {
        var last = -1;
        foreach (var (s, e) in segments)
        {
            var cell = (s - offset) / p;
            if (s < offset || (e - 1 - offset) / p != cell || cell == last) return false;
            last = cell;
        }
        return true;
    }

    /// <summary>Mean colour change between neighbours, by border position (index i = between i-1 and i).</summary>
    private static (double[] Dx, double[] Dy) Changes(byte[] rgba, int w, int h)
    {
        var dx = new double[w];
        var dy = new double[h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                if (x > 0) dx[x] += Diff(rgba, i, i - 4);
                if (y > 0) dy[y] += Diff(rgba, i, i - w * 4);
            }
        for (var x = 0; x < w; x++) dx[x] /= h;
        for (var y = 0; y < h; y++) dy[y] /= w;
        return (dx, dy);
    }

    private static int Diff(byte[] p, int a, int b) =>
        Math.Abs(p[a] - p[b]) + Math.Abs(p[a + 1] - p[b + 1]) + Math.Abs(p[a + 2] - p[b + 2]) + Math.Abs(p[a + 3] - p[b + 3]);

    private static Axis? PackedAxis(double[] d, int? hint)
    {
        var length = d.Length;
        var inner = d.Skip(1).Order().ToList();
        if (inner.Count == 0) return null;
        var median = inner[inner.Count / 2];
        var mean = inner.Average();
        if (mean <= 0) return new Axis(length, 0, 1, 1); // flat
        // A "strong" line: a change well above both the typical and the average change between neighbours.
        var strong = Math.Max(3 * median, 2 * mean);
        var candidates = Divisors(length).Where(p => p <= length / 2).ToHashSet();
        if (hint is >= MinCell && hint <= length / 2) candidates.Add(hint.Value);

        // The smallest size whose cell borders are (nearly) all strong lines; multiples would also pass, divisors not.
        foreach (var p in candidates.Order())
        {
            var borders = Enumerable.Range(1, (length - 1) / p).Select(k => d[k * p]).ToList();
            if (borders.Count == 0 || borders.Count(v => v >= strong) < 0.8 * borders.Count) continue;
            // Confidence drops with lines between the borders that are about as strong (another, finer structure).
            var typical = borders.Order().ElementAt(borders.Count / 2);
            var between = Enumerable.Range(1, length - 1).Where(i => i % p != 0).ToList();
            var falseShare = between.Count(i => d[i] >= Math.Max(strong, 0.5 * typical)) / (double)Math.Max(1, between.Count);
            var bordersStrong = borders.Count(v => v >= strong) / (double)borders.Count;
            return new Axis(p, 0, length / p, bordersStrong * Math.Max(0, 1 - 2 * falseShare));
        }
        return new Axis(length, 0, 1, 1); // no grid on this axis: one cell
    }

    /// <summary>Whether a packed grid is believable from the pixels, or named by the hint (see <see cref="PackedMinCells"/>).</summary>
    private static bool PackedPlausible(Axis ax, Axis ay, int? hintCols, int? hintRows)
    {
        if (ax.Cells * ay.Cells < 2) return true; // no grid: nothing to doubt
        if ((hintCols is null || hintCols == ax.Cells) && (hintRows is null || hintRows == ay.Cells) && (hintCols ?? hintRows) is not null)
            return true;
        var aspect = (double)Math.Max(ax.Pitch, ay.Pitch) / Math.Min(ax.Pitch, ay.Pitch);
        return ax.Cells * ay.Cells >= PackedMinCells && ax.Cells != 2 && ay.Cells != 2 && aspect <= PackedMaxAspect;
    }

    private static IEnumerable<int> Divisors(int length) =>
        Enumerable.Range(MinCell, Math.Max(0, Math.Min(MaxCell, length) - MinCell + 1)).Where(p => length % p == 0);

    private static bool CellEmpty(bool[] empty, int w, int x0, int y0, int cw, int ch)
    {
        var h = empty.Length / w;
        for (var y = y0; y < Math.Min(y0 + ch, h); y++)
            for (var x = x0; x < Math.Min(x0 + cw, w); x++)
                if (!empty[y * w + x]) return false;
        return true;
    }
}
