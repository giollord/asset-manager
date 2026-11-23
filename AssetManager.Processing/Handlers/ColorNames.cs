namespace AssetManager.Processing.Handlers;

/// <summary>Maps an RGB color to the nearest of a small set of everyday English color names (CIE Lab distance).</summary>
public static class ColorNames
{
    private static readonly (string Name, int R, int G, int B)[] Raw =
    [
        ("black", 0, 0, 0), ("charcoal", 54, 54, 54), ("dark gray", 90, 90, 90), ("gray", 128, 128, 128),
        ("silver", 192, 192, 192), ("light gray", 220, 220, 220), ("white", 255, 255, 255),
        ("dark red", 120, 10, 15), ("red", 210, 30, 35), ("crimson", 220, 20, 60), ("pink", 255, 170, 190), ("hot pink", 255, 80, 160),
        ("maroon", 110, 20, 40), ("brown", 120, 75, 40), ("dark brown", 70, 45, 25), ("tan", 210, 180, 140), ("beige", 235, 220, 190),
        ("orange", 245, 135, 30), ("rust", 170, 70, 25), ("gold", 225, 180, 40), ("yellow", 250, 225, 40), ("cream", 250, 245, 210),
        ("olive", 120, 120, 30), ("lime", 160, 220, 50), ("green", 50, 150, 50), ("dark green", 25, 80, 35), ("forest green", 35, 105, 45),
        ("mint", 160, 230, 190), ("teal", 0, 128, 128), ("turquoise", 60, 210, 200), ("cyan", 40, 220, 240),
        ("sky blue", 120, 190, 240), ("light blue", 170, 210, 240), ("blue", 40, 90, 210), ("navy", 20, 30, 100), ("royal blue", 65, 105, 225),
        ("indigo", 75, 0, 130), ("purple", 130, 50, 160), ("violet", 170, 110, 220), ("lavender", 200, 180, 235), ("magenta", 220, 40, 200),
    ];

    private static readonly (string Name, double L, double A, double B)[] Table =
        Raw.Select(c => { var (l, a, b) = Lab(c.R, c.G, c.B); return (c.Name, l, a, b); }).ToArray();

    /// <summary>Hex of a color name from the table; for unknown names the last word is tried ("mossy green" → green).</summary>
    public static string? HexOf(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        var hit = Raw.FirstOrDefault(c => c.Name == n);
        if (hit.Name is null && n.LastIndexOf(' ') is > 0 and var i) hit = Raw.FirstOrDefault(c => c.Name == n[(i + 1)..]);
        return hit.Name is null ? null : $"#{hit.R:X2}{hit.G:X2}{hit.B:X2}";
    }

    public static string Nearest(byte r, byte g, byte b)
    {
        var (l, a, bb) = Lab(r, g, b);
        return Table.MinBy(t => (t.L - l) * (t.L - l) + (t.A - a) * (t.A - a) + (t.B - bb) * (t.B - bb)).Name;
    }

    private static (double L, double A, double B) Lab(int r, int g, int b)
    {
        static double Lin(int c) { var v = c / 255.0; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        double R = Lin(r), G = Lin(g), B = Lin(b);
        var x = (R * 0.4124 + G * 0.3576 + B * 0.1805) / 0.95047;
        var y = R * 0.2126 + G * 0.7152 + B * 0.0722;
        var z = (R * 0.0193 + G * 0.1192 + B * 0.9505) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
        return (116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
    }
}
