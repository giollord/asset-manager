namespace AssetManager.Tests;

/// <summary>
/// Spec "app-branding": every logo file in the app is a copy of a master in <c>branding/</c>, and the template's
/// purple and .NET mark are gone. An edit to only one copy fails here.
/// </summary>
public class BrandingTests
{
    private static readonly string Root = FindRepoRoot();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "branding", "logo.svg")))
                return dir.FullName;
        throw new DirectoryNotFoundException("Repository root with branding/logo.svg not found above " + AppContext.BaseDirectory);
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative));

    [Theory]
    [InlineData("AssetManager/Resources/AppIcon/appicon.svg", "branding/logo.svg")]
    [InlineData("AssetManager/Resources/Splash/splash.svg", "branding/logo.svg")]
    [InlineData("AssetManager/wwwroot/logo.svg", "branding/logo.svg")]
    [InlineData("AssetManager.Shared/wwwroot/logo-mark.svg", "branding/logo-mark.svg")]
    public void Copy_matches_its_master(string copy, string master)
    {
        var expected = File.ReadAllBytes(Path.Combine(Root, master));
        var actual = File.ReadAllBytes(Path.Combine(Root, copy));
        Assert.True(expected.AsSpan().SequenceEqual(actual), $"{copy} differs from {master}; copy the master over it");
    }

    [Fact]
    public void Mark_is_the_crate_from_the_full_logo()
    {
        Assert.Equal(CrateGroup(Read("branding/logo.svg")), CrateGroup(Read("branding/logo-mark.svg")));
    }

    private static string CrateGroup(string svg)
    {
        const string start = "<g id=\"crate\">";
        var from = svg.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "no <g id=\"crate\"> group");
        var to = svg.IndexOf("</g>", from, StringComparison.Ordinal);
        return svg[from..(to + "</g>".Length)].ReplaceLineEndings("\n");
    }

    [Fact]
    public void No_template_artwork_left_in_resources()
    {
        // "#512BD4" is the MAUI template purple; the path start is the template's ".NET" lettering.
        string[] banned = ["#512BD4", "m 105.50037,281.60863"];
        var offenders =
            from file in Directory.EnumerateFiles(Path.Combine(Root, "AssetManager", "Resources"), "*", SearchOption.AllDirectories)
            let text = File.ReadAllText(file)
            from mark in banned
            where text.Contains(mark, StringComparison.OrdinalIgnoreCase)
            select $"{Path.GetRelativePath(Root, file)}: {mark}";
        Assert.Empty(offenders);
    }
}
