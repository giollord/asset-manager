using AssetManager.Core.Domain;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Media;
using AssetManager.Processing.Settings;
using AssetManager.Tests.Fixtures;
using ImageMagick;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace AssetManager.Tests.Processing;

public class MediaHandlerTests
{
    private static readonly AssetProcessor Processor = new(new HandlerRegistry(
        [new FontHandler(), new VideoHandler(), new DocumentHandler(), new FallbackHandler()]));

    [Fact]
    public async Task Font_metadata_and_specimen()
    {
        var arial = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        Assert.SkipUnless(File.Exists(arial), "Needs the Windows Arial font.");
        using var dir = FixtureBuilder.TempDir();
        var path = Path.Combine(dir.Path, "Arial.ttf");
        File.Copy(arial, path);
        var item = ImageHandlerTests.Item(path, AssetType.Font);

        var result = await Processor.ProcessAsync(item, new PreviewOptions());

        Assert.Null(result.Error);
        Assert.Equal("Arial", item.Asset.Font!.Family);
        Assert.Equal("Regular", item.Asset.Font.Style);
        Assert.True(item.Asset.Font.GlyphCount > 1000);
        var thumb = Assert.Single(result.Previews);
        using var img = new MagickImage(thumb.Bytes);
        Assert.True(img.Width > img.Height);
        Assert.True(img.HasAlpha);
    }

    [Fact]
    public async Task Video_metadata_and_first_frame()
    {
        using var dir = FixtureBuilder.TempDir();
        var path = Path.Combine(dir.Path, "clip.mp4");
        await Ffmpeg.RunAsync(["-f", "lavfi", "-i", "testsrc=duration=2:size=1280x720:rate=25", "-c:v", "mpeg4", path], CancellationToken.None);
        var item = ImageHandlerTests.Item(path, AssetType.Video);

        var result = await Processor.ProcessAsync(item, new PreviewOptions());

        Assert.Null(result.Error);
        var v = item.Asset.Video!;
        Assert.Equal((1280, 720, "mpeg4"), (v.Width, v.Height, v.Codec));
        Assert.InRange(v.DurationSeconds, 1.9, 2.1);
        Assert.Equal(25, v.FrameRate);
        var thumb = Assert.Single(result.Previews);
        Assert.Equal((512, 288), (thumb.Width, thumb.Height));
    }

    [Fact]
    public async Task Document_metadata()
    {
        using var dir = FixtureBuilder.TempDir();
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.A4).AddText("License: CC0 - free for commercial use", 12, new PdfPoint(25, 700), font);
        builder.AddPage(PageSize.A4);
        var pdf = dir.Write("manual.pdf", builder.Build());
        var html = dir.Write("guide.html", "<html><head><style>p{}</style></head><body><h1>Guide</h1><p>Use &amp; enjoy</p></body></html>");
        var txt = dir.Write("notes.txt", "  Line one\r\n\r\nLine   two ");

        var p = ImageHandlerTests.Item(pdf, AssetType.Document);
        await Processor.ProcessAsync(p, new PreviewOptions());
        Assert.Equal(("PDF", 2), (p.Asset.Document!.Format, p.Asset.Document.Pages));
        Assert.Contains("CC0", p.Asset.Document.Excerpt);

        var h = ImageHandlerTests.Item(html, AssetType.Document);
        var hr = await Processor.ProcessAsync(h, new PreviewOptions());
        Assert.Equal("Guide Use & enjoy", h.Asset.Document!.Excerpt);
        Assert.Empty(hr.Previews); // placeholder in the UI

        var t = ImageHandlerTests.Item(txt, AssetType.Document);
        await Processor.ProcessAsync(t, new PreviewOptions());
        Assert.Equal("Line one Line two", t.Asset.Document!.Excerpt);
    }

    [Fact]
    public async Task Fallback_handler_catalogs_unknown_files()
    {
        using var dir = FixtureBuilder.TempDir();
        var item = ImageHandlerTests.Item(dir.Write("data.bin", [1, 2, 3]), AssetType.Other);
        var result = await Processor.ProcessAsync(item, new PreviewOptions());
        Assert.Null(result.Error);
        Assert.Empty(result.Previews);
        Assert.Equal(new PreviewOptions().Version(), item.Asset.PreviewOptionsVersion);
    }
}
