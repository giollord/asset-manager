using System.Globalization;
using AssetManager.Core.Domain;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Media;
using AssetManager.Processing.Settings;
using AssetManager.Tests.Fixtures;
using ImageMagick;

namespace AssetManager.Tests.Processing;

public class AudioHandlerTests
{
    private static readonly AssetProcessor Processor = new(new HandlerRegistry([new AudioHandler(), new FallbackHandler()]));

    /// <summary>Generates a stereo 44.1 kHz WAV sine tone with the bundled ffmpeg.</summary>
    internal static async Task<string> ToneAsync(TempDir dir, string name, double seconds)
    {
        var path = Path.Combine(dir.Path, name);
        await Ffmpeg.RunAsync(["-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=44100:duration={seconds.ToString(CultureInfo.InvariantCulture)}",
            "-ac", "2", path], CancellationToken.None);
        return path;
    }

    private static async Task<(double Seconds, string Codec, int Channels)> ProbeClipAsync(PreviewOutput clip)
    {
        using var dir = FixtureBuilder.TempDir();
        var path = dir.Write("clip.ogg", clip.Bytes);
        var a = await Ffmpeg.ProbeAsync(path, CancellationToken.None);
        return (a.Duration.TotalSeconds, a.PrimaryAudioStream!.CodecName, a.PrimaryAudioStream.Channels ?? 0);
    }

    [Fact]
    public async Task Long_music_track()
    {
        using var dir = FixtureBuilder.TempDir();
        var item = ImageHandlerTests.Item(await ToneAsync(dir, "theme.wav", 180), AssetType.Audio);

        var result = await Processor.ProcessAsync(item, new PreviewOptions());

        Assert.Null(result.Error);
        Assert.Equal(AssetType.Music, item.Asset.Type);
        var meta = item.Asset.Audio!;
        Assert.InRange(meta.DurationSeconds, 179.9, 180.1);
        Assert.Equal(44100, meta.SampleRate);
        Assert.Equal(2, meta.Channels);
        Assert.Equal("pcm_s16le", meta.Codec);
        Assert.NotNull(meta.Bitrate);

        var clip = Assert.Single(result.Previews, p => p.Kind == PreviewKind.AudioClip);
        Assert.Equal("audio/ogg", clip.Mime);
        var (seconds, codec, channels) = await ProbeClipAsync(clip);
        Assert.InRange(seconds, 9.5, 10.05);
        Assert.Equal("opus", codec);
        Assert.Equal(1, channels);
        Assert.True(clip.Bytes.Length < 100_000, $"low bitrate clip ({clip.Bytes.Length} bytes)");

        var wave = Assert.Single(result.Previews, p => p.Kind == PreviewKind.Waveform);
        using (var img = new MagickImage(wave.Bytes)) Assert.Equal(new PreviewOptions().WaveformWidth, (int)img.Width);
        Assert.Single(result.Previews, p => p.Kind == PreviewKind.Spectrogram && p.Mime == "image/webp");
    }

    [Fact]
    public async Task Short_sound_effect_is_stored_in_full()
    {
        using var dir = FixtureBuilder.TempDir();
        var item = ImageHandlerTests.Item(await ToneAsync(dir, "jump.wav", 1.2), AssetType.Audio);

        var result = await Processor.ProcessAsync(item, new PreviewOptions());

        Assert.Equal(AssetType.Audio, item.Asset.Type);
        var (seconds, codec, _) = await ProbeClipAsync(Assert.Single(result.Previews, p => p.Kind == PreviewKind.AudioClip));
        Assert.InRange(seconds, 1.15, 1.25);
        Assert.Equal("opus", codec);
    }

    [Fact]
    public async Task Custom_audio_clip_length()
    {
        using var dir = FixtureBuilder.TempDir();
        var item = ImageHandlerTests.Item(await ToneAsync(dir, "rain.wav", 30), AssetType.Audio);

        var result = await Processor.ProcessAsync(item, new PreviewOptions { AudioClipSeconds = 5 });

        var (seconds, _, _) = await ProbeClipAsync(Assert.Single(result.Previews, p => p.Kind == PreviewKind.AudioClip));
        Assert.InRange(seconds, 4.5, 5.05);
    }

    [Fact]
    public async Task Not_audio_records_an_error()
    {
        using var dir = FixtureBuilder.TempDir();
        var item = ImageHandlerTests.Item(dir.Write("broken.wav", "nope"), AssetType.Audio);
        var result = await Processor.ProcessAsync(item, new PreviewOptions());
        Assert.NotNull(result.Error);
        Assert.Empty(result.Previews);
    }
}
