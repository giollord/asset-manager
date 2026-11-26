using System.Globalization;
using AssetManager.Core.Domain;

namespace AssetManager.Core.Search;

/// <summary>
/// Palette similarity for search by image (design D6 step 3): for each colour of one palette, the CIE76 distance in
/// Lab to the nearest colour of the other, weighted by the colour's share, averaged both ways and mapped to 0..1
/// (1 = same colours). Weights of 0 (named AI colours) count equally.
/// </summary>
public static class PaletteSimilarity
{
    /// <summary>A Lab distance at which colours count as completely different.</summary>
    public const double MaxDistance = 100;

    /// <summary>Weight of the cosine similarity in the re-rank score; the palette has the rest.</summary>
    public const double CosineWeight = 0.8;

    /// <summary>Null when either palette is empty (nothing to compare).</summary>
    public static double? Of(IReadOnlyList<ColorInfo> a, IReadOnlyList<ColorInfo> b)
    {
        var la = Labs(a);
        var lb = Labs(b);
        if (la.Count == 0 || lb.Count == 0) return null;
        var d = (Directed(la, lb) + Directed(lb, la)) / 2;
        return 1 - Math.Clamp(d / MaxDistance, 0, 1);
    }

    /// <summary>The re-rank score: <c>0.8 · cosine + 0.2 · palette</c>; cosine alone when there is no palette to compare.</summary>
    public static double Score(double cosine, double? palette) =>
        palette is { } p ? CosineWeight * cosine + (1 - CosineWeight) * p : cosine;

    private static double Directed(List<(double L, double A, double B, double W)> from, List<(double L, double A, double B, double W)> to)
    {
        var equal = from.All(c => c.W <= 0);
        double sum = 0, weights = 0;
        foreach (var c in from)
        {
            var w = equal ? 1 : Math.Max(0, c.W);
            sum += w * to.Min(t => Distance(c, t));
            weights += w;
        }
        return weights == 0 ? MaxDistance : sum / weights;
    }

    private static double Distance((double L, double A, double B, double W) x, (double L, double A, double B, double W) y) =>
        Math.Sqrt((x.L - y.L) * (x.L - y.L) + (x.A - y.A) * (x.A - y.A) + (x.B - y.B) * (x.B - y.B));

    private static List<(double L, double A, double B, double W)> Labs(IReadOnlyList<ColorInfo> colors) =>
        colors.Select(c => Lab(c.Hex) is { } lab ? (lab.L, lab.A, lab.B, c.Weight) : ((double, double, double, double)?)null)
            .OfType<(double, double, double, double)>().ToList();

    /// <summary>CIE Lab (D65) of an sRGB "#RRGGBB" colour; null when it cannot be parsed.</summary>
    public static (double L, double A, double B)? Lab(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return null;
        static double Linear(int v)
        {
            var c = v / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        double r = Linear(rgb >> 16 & 0xFF), g = Linear(rgb >> 8 & 0xFF), b = Linear(rgb & 0xFF);
        var x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047;
        var y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        var z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883;
        static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;
        double fx = F(x), fy = F(y), fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }
}
