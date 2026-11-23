using System.Text;
using AssetManager.Core.Domain;
using AssetManager.Processing.Containers;
using AssetManager.Tests.Fixtures;

namespace AssetManager.Tests.Processing;

public class ContainerReaderTests
{
    private static readonly FixtureFile[] Files =
    [
        FixtureFile.Text("readme.txt", "hello"),
        FixtureFile.Text("Textures/wood.png", "PNG1"),
        FixtureFile.Text("Textures/Sub/stone.png", "PNG2"),
    ];

    [Fact]
    public async Task Folder_import_is_recursive()
    {
        using var kit = new ProcessingKit();
        foreach (var f in Files) kit.Dir.Write(Path.Combine("pack", f.Path), f.Content);
        var nodes = await kit.WalkAsync(Path.Combine(kit.Dir.Path, "pack"));
        Assert.Equal([@"readme.txt", @"Textures\Sub\stone.png", @"Textures\wood.png"], nodes.Select(n => n.RelativePath));
        Assert.All(nodes, n => Assert.Equal(VfsNodeKind.File, n.Kind));
        Assert.Equal(Path.Combine(kit.Dir.Path, @"pack\Textures\Sub\stone.png"), nodes[1].Location.Path);
    }

    public static TheoryData<string> ArchiveKinds => ["zip", "tar", "tar.gz", "tgz"];

    [Theory]
    [MemberData(nameof(ArchiveKinds))]
    public async Task Reads_archive(string ext)
    {
        using var kit = new ProcessingKit();
        var bytes = ext switch
        {
            "zip" => FixtureBuilder.Zip(Files),
            "tar" => FixtureBuilder.Tar(Files),
            _ => FixtureBuilder.TarGz(Files),
        };
        var path = kit.Dir.Write("pack." + ext, bytes);
        using var contents = await kit.Archives.OpenAsync(path);
        Assert.Equal(["readme.txt", "Textures/Sub/stone.png", "Textures/wood.png"], contents.Entries.Select(e => e.Key));
        Assert.Equal("PNG2", File.ReadAllText(contents.Entries[1].LocalPath));
    }

    [Fact]
    public async Task Reads_7z()
    {
        using var kit = new ProcessingKit();
        var path = kit.Dir.Write("pack.7z", FixtureBuilder.SevenZip());
        using var contents = await kit.Archives.OpenAsync(path);
        Assert.Equal(["readme.txt", "tex.png"], contents.Entries.Select(e => e.Key));
        Assert.Equal("hello 7z", File.ReadAllText(contents.Entries[0].LocalPath));
    }

    [Fact]
    public async Task Reads_single_gz()
    {
        using var kit = new ProcessingKit();
        var path = kit.Dir.Write("notes.txt.gz", FixtureBuilder.Gzip(Encoding.UTF8.GetBytes("gz content")));
        using var contents = await kit.Archives.OpenAsync(path);
        var e = Assert.Single(contents.Entries);
        Assert.Equal("notes.txt", e.Key);
        Assert.Equal("gz content", File.ReadAllText(e.LocalPath));
    }

    [Fact]
    public async Task Corrupt_zip_is_a_per_source_error()
    {
        using var kit = new ProcessingKit();
        var good = FixtureBuilder.Zip(Files);
        var corrupt = good[..(good.Length / 2)];
        corrupt[10] ^= 0xFF;
        kit.Dir.Write(@"in\a_corrupt.zip", corrupt);
        kit.Dir.Write(@"in\b_ok.zip", good);
        kit.Dir.Write(@"in\c.png", "PNG");

        var ex = await Assert.ThrowsAsync<ContainerReadException>(() => kit.Archives.OpenAsync(Path.Combine(kit.Dir.Path, @"in\a_corrupt.zip")));
        Assert.False(string.IsNullOrWhiteSpace(ex.Reason));

        var nodes = await kit.WalkAsync(Path.Combine(kit.Dir.Path, "in"));
        var error = Assert.Single(nodes, n => n.Kind == VfsNodeKind.ContainerError);
        Assert.Equal("a_corrupt.zip", error.Location.FileName);
        Assert.NotNull(error.Error);
        Assert.Equal(3, nodes.Count(n => n.Kind == VfsNodeKind.File && n.Depth == 1)); // the good zip still read
        Assert.Contains(nodes, n => n.RelativePath == "c.png");
    }

    [Fact]
    public async Task Encrypted_zip_is_reported()
    {
        using var kit = new ProcessingKit();
        var path = kit.Dir.Write("secret.zip", MarkEncrypted(FixtureBuilder.Zip(Files)));
        var ex = await Assert.ThrowsAsync<ContainerReadException>(() => kit.Archives.OpenAsync(path));
        Assert.Contains("Password", ex.Reason);
    }

    [Fact]
    public async Task Single_entry_extraction_from_zip_does_not_extract_everything()
    {
        using var kit = new ProcessingKit();
        var path = kit.Dir.Write("pack.zip", FixtureBuilder.Zip(Files));
        using var r = await kit.Archives.ExtractEntryAsync(path, "Textures/wood.png");
        Assert.Equal("PNG1", File.ReadAllText(r.Path));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(kit.Dir.Path, "cache"), "*.png", SearchOption.AllDirectories));
        await Assert.ThrowsAsync<FileNotFoundException>(() => kit.Archives.ExtractEntryAsync(path, "nope.png"));
    }

    [SamplesFact]
    public async Task Reads_sample_rar()
    {
        var rar = Directory.EnumerateFiles(SamplesFactAttribute.SamplesRoot!, "*.rar", SearchOption.AllDirectories)
            .OrderBy(f => new FileInfo(f).Length).FirstOrDefault();
        Assert.SkipWhen(rar is null, "No .rar in the samples.");
        using var kit = new ProcessingKit();
        using var contents = await kit.Archives.OpenAsync(rar!);
        Assert.NotEmpty(contents.Entries);
        Assert.All(contents.Entries, e => Assert.True(File.Exists(e.LocalPath)));
    }

    /// <summary>Sets the "encrypted" flag (bit 0) in every local and central zip header.</summary>
    private static byte[] MarkEncrypted(byte[] zip)
    {
        for (var i = 0; i + 4 <= zip.Length; i++)
        {
            if (zip[i] != 0x50 || zip[i + 1] != 0x4b) continue;
            if (zip[i + 2] == 3 && zip[i + 3] == 4) zip[i + 6] |= 1;      // local header: flags at +6
            else if (zip[i + 2] == 1 && zip[i + 3] == 2) zip[i + 8] |= 1; // central header: flags at +8
        }
        return zip;
    }
}
