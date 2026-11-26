using System.IO.Compression;
using AssetManager.Core.Platform;
using AssetManager.Tests.Fixtures;

namespace AssetManager.Tests.Core;

/// <summary>Design D2: the portable package serves its web content from wwwroot.zip.</summary>
public class ZipFileProviderTests
{
    private static string MakeZip(TempDir dir)
    {
        var path = Path.Combine(dir.Path, "wwwroot.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string text)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(text);
        }
        Add("index.html", "<html>app</html>");
        Add("_content/MudBlazor/MudBlazor.min.js", "mud();");
        Add("_content/AssetManager.Shared/js/layout.js", "layout();");
        return path;
    }

    private static string Read(Microsoft.Extensions.FileProviders.IFileInfo file)
    {
        using var r = new StreamReader(file.CreateReadStream());
        return r.ReadToEnd();
    }

    [Fact]
    public void Serves_the_host_page_and_package_content()
    {
        using var dir = FixtureBuilder.TempDir();
        var provider = new ZipFileProvider(MakeZip(dir));

        var index = provider.GetFileInfo("index.html");
        Assert.True(index.Exists);
        Assert.Equal("<html>app</html>", Read(index));
        Assert.Equal(16, index.Length);
        Assert.Equal("mud();", Read(provider.GetFileInfo("/_content/MudBlazor/MudBlazor.min.js")));
        Assert.Equal("layout();", Read(provider.GetFileInfo(@"_content\AssetManager.Shared\js\LAYOUT.js"))); // separators and case as on Windows
    }

    [Fact]
    public void Missing_path_is_not_found()
    {
        using var dir = FixtureBuilder.TempDir();
        var provider = new ZipFileProvider(MakeZip(dir));

        Assert.False(provider.GetFileInfo("_content/MudBlazor/missing.css").Exists);
        Assert.False(provider.GetFileInfo("_content").Exists); // a folder is not a file
        Assert.False(provider.GetDirectoryContents("nowhere").Exists);
    }

    [Fact]
    public void Lists_folders()
    {
        using var dir = FixtureBuilder.TempDir();
        var provider = new ZipFileProvider(MakeZip(dir));

        Assert.Equal(["_content", "index.html"], provider.GetDirectoryContents("").Select(f => f.Name).Order());
        Assert.Equal(["AssetManager.Shared", "MudBlazor"], provider.GetDirectoryContents("_content").Select(f => f.Name).Order());
        Assert.True(provider.GetDirectoryContents("_content").First().IsDirectory);
    }
}
