using System.Formats.Tar;
using System.IO.Compression;
using AssetManager.Tests.Fixtures;

namespace AssetManager.Tests;

public class FixtureBuilderTests
{
    [Fact]
    public void Zip_contains_files()
    {
        var bytes = FixtureBuilder.Zip(FixtureFile.Text("a/b.txt", "x"));
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal("a/b.txt", Assert.Single(zip.Entries).FullName);
    }

    [Fact]
    public void UnityPackage_is_gzipped_tar_with_guid_folders()
    {
        var guid = FixtureBuilder.Guid("tex");
        var bytes = FixtureBuilder.UnityPackage([new(guid, "Assets/Tex/a.png", [1, 2, 3])], new("123", "Pack", "Pub", "1.0"));
        Assert.Equal(0x04, bytes[3] & 0x04);
        using var tar = new TarReader(new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress));
        var names = new List<string>();
        while (tar.GetNextEntry() is { } e) names.Add(e.Name);
        Assert.Contains($"{guid}/pathname", names);
        Assert.Contains($"{guid}/asset", names);
    }

    [Fact]
    public void SevenZip_has_7z_signature() => Assert.Equal("377ABCAF271C", Convert.ToHexString(FixtureBuilder.SevenZip()[..6]));

    [SamplesFact]
    public void Samples_folder_has_files() => Assert.NotEmpty(Directory.EnumerateFileSystemEntries(SamplesFactAttribute.SamplesRoot!));
}
