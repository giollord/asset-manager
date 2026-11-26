using AssetManager.Shared.Services;

namespace AssetManager.Tests.Ui;

/// <summary>The README's shortcut table lists exactly the shortcuts the Browse "?" dialog shows (task 4.2).</summary>
public class ShortcutTableTests
{
    private static string Readme()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "README.md");
            if (File.Exists(path) && Directory.Exists(Path.Combine(dir.FullName, "openspec"))) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("README.md not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Readme_table_matches_the_shortcut_list()
    {
        var readme = Readme();
        var section = readme[readme.IndexOf("## Keyboard shortcuts", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("\n## ", 3, StringComparison.Ordinal)];
        var rows = section.Split('\n').Where(l => l.StartsWith("| `", StringComparison.Ordinal))
            .Select(l => l.Split('|', StringSplitOptions.TrimEntries)).Select(c => (Label: c[1].Trim('`'), Description: c[2])).ToList();

        Assert.Equal(BrowseShortcuts.All.Select(s => (s.Label, s.Description)), rows);
    }
}
