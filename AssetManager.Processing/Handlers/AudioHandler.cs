using System.Globalization;
using AssetManager.Core.Domain;
using AssetManager.Processing.Media;
using AssetManager.Processing.Settings;
using ImageMagick;

namespace AssetManager.Processing.Handlers;

/// <summary>
/// Sounds and music via the bundled FFmpeg: ffprobe metadata, a short low-bitrate Opus clip from the start
/// (the whole sound when shorter than the clip length), a waveform and a spectrogram (both WebP).
/// </summary>
public sealed class AudioHandler : AssetTypeHandlerBase
{
    /// <summary>Sounds at least this long (or found under a "music" folder) are typed as music.</summary>
    public static readonly TimeSpan MusicMinDuration = TimeSpan.FromSeconds(45);

    public override string Name => "audio";

    public override bool CanHandle(AssetType type) => type is AssetType.Audio or AssetType.Music;

    public override async Task ExtractMetadataAsync(HandlerItem item, PreviewOptions options, CancellationToken ct = default)
    {
        var analysis = await Ffmpeg.ProbeAsync(item.PrimaryPath, ct);
        var audio = analysis.PrimaryAudioStream ?? throw new InvalidDataException("The file has no audio stream.");
        var duration = analysis.Duration > TimeSpan.Zero ? analysis.Duration : audio.Duration;
        item.Asset.Audio = new AudioMetadata
        {
            DurationSeconds = Math.Round(duration.TotalSeconds, 3),
            SampleRate = audio.SampleRateHz ?? 0,
            Channels = audio.Channels ?? 0,
            Codec = audio.CodecName ?? "",
            Bitrate = audio.BitRate ?? analysis.Format.BitRate,
        };

        if (item.Asset.Type == AssetType.Audio && !item.Asset.UserEdited.HasFlag(EditedFields.Type))
        {
            var path = item.Asset.PrimaryFile?.RelativePath ?? "";
            var inMusicFolder = path.Replace('\\', '/').Split('/').SkipLast(1).Any(s => s.Contains("music", StringComparison.OrdinalIgnoreCase));
            if (duration >= MusicMinDuration || inMusicFolder) item.Asset.Type = AssetType.Music;
        }
    }

    public override async Task<IReadOnlyList<PreviewOutput>> GeneratePreviewsAsync(HandlerItem item, PreviewOptions options, CancellationToken ct = default)
    {
        using var tmp = Ffmpeg.CreateTemp();
        var seconds = options.AudioClipSeconds.ToString(CultureInfo.InvariantCulture);

        var clip = tmp.File("clip.ogg");
        await Ffmpeg.RunAsync(
        [
            "-i", item.PrimaryPath, "-t", seconds, "-vn", "-map_metadata", "-1",
            "-ac", options.AudioChannels.ToString(CultureInfo.InvariantCulture),
            "-c:a", "libopus", "-b:a", $"{options.AudioBitrateKbps}k", "-f", "ogg", clip,
        ], ct);

        // showwavespic draws nothing (and ffmpeg still exits 0) when the sound has fewer samples than the picture is
        // wide, e.g. a 10 ms click; apad pads such a sound with silence up to the width.
        var wave = tmp.File("wave.png");
        await Ffmpeg.RunAsync(
        [
            "-i", item.PrimaryPath, "-filter_complex",
            $"aformat=channel_layouts=mono,apad=whole_len={options.WaveformWidth},showwavespic=s={options.WaveformWidth}x{options.WaveformHeight}:colors=0x5C6BC0",
            "-frames:v", "1", wave,
        ], ct);

        var spectrum = tmp.File("spectrum.png");
        await Ffmpeg.RunAsync(
        [
            "-i", item.PrimaryPath, "-t", seconds, "-lavfi",
            $"showspectrumpic=s={options.SpectrogramWidth}x{options.SpectrogramHeight}:legend=0",
            "-frames:v", "1", spectrum,
        ], ct);

        return
        [
            new PreviewOutput(PreviewKind.AudioClip, 0, "audio/ogg", 0, 0, await File.ReadAllBytesAsync(clip, ct)),
            ToWebP(wave, PreviewKind.Waveform, lossless: true),
            ToWebP(spectrum, PreviewKind.Spectrogram, lossless: false, options.ImageQuality),
        ];
    }

    private static PreviewOutput ToWebP(string png, PreviewKind kind, bool lossless, int quality = 100)
    {
        using var image = new MagickImage(png);
        if (lossless) image.Settings.SetDefine(MagickFormat.WebP, "lossless", true);
        return ImageHandler.Encode(image, kind, quality);
    }
}
