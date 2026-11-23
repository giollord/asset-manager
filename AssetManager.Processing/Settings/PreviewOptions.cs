using System.ComponentModel.DataAnnotations;
using System.IO.Hashing;
using System.Text.Json;

namespace AssetManager.Processing.Settings;

/// <summary>
/// Every size, threshold and quality value used for preview generation (spec "Preview settings").
/// Stored in the catalog; handlers read only from this record. Defaults are the documented defaults.
/// </summary>
public sealed record PreviewOptions : IValidatableObject
{
    [Display(Name = "Image preview maximum edge (px)")]
    [Range(16, 4096)] public int ImageMaxEdge { get; init; } = 512;

    [Display(Name = "Image preview lossy quality")]
    [Range(1, 100)] public int ImageQuality { get; init; } = 80;

    [Display(Name = "Keep-as-is maximum dimension (px)")]
    [Range(0, 4096)] public int KeepAsIsMaxDimension { get; init; } = 512;

    [Display(Name = "Keep-as-is maximum file size (KB)")]
    [Range(0, 16384)] public int KeepAsIsMaxKb { get; init; } = 64;

    [Display(Name = "Pixel-art detection")]
    public bool PixelArtDetection { get; init; } = true;

    [Display(Name = "Pixel-art detection: maximum edge (px)")]
    [Range(1, 4096)] public int PixelArtMaxEdge { get; init; } = 128;

    [Display(Name = "Pixel-art detection: maximum color count")]
    [Range(2, 4096)] public int PixelArtMaxColors { get; init; } = 64;

    [Display(Name = "Palette color count")]
    [Range(1, 16)] public int PaletteColors { get; init; } = 5;

    [Display(Name = "3D render size (px)")]
    [Range(64, 2048)] public int RenderSize { get; init; } = 384;

    [Display(Name = "3D elevation angles (°)")]
    public IReadOnlyList<int> Elevations { get; init; } = [-15, 20, 55];

    [Display(Name = "3D azimuths per ring")]
    [Range(1, 16)] public int AzimuthsPerRing { get; init; } = 8;

    [Display(Name = "3D render quality")]
    [Range(1, 100)] public int RenderQuality { get; init; } = 80;

    [Display(Name = "Primary view: elevation ring (0-based)")]
    [Range(0, 5)] public int PrimaryRing { get; init; } = 1;

    [Display(Name = "Primary view: azimuth index (0-based)")]
    [Range(0, 15)] public int PrimaryAzimuth { get; init; } = 1;

    [Display(Name = "Orientation probe tile size (px)")]
    [Range(64, 1024)] public int ProbeTileSize { get; init; } = 256;

    [Display(Name = "Scale anomaly: too small below (m)")]
    [Range(0.0, 1000.0)] public double ScaleTooSmallMeters { get; init; } = 0.01;

    [Display(Name = "Scale anomaly: too large above (m)")]
    [Range(1.0, 1_000_000.0)] public double ScaleTooLargeMeters { get; init; } = 1000;

    [Display(Name = "Audio clip maximum length (s)")]
    [Range(1, 60)] public int AudioClipSeconds { get; init; } = 10;

    [Display(Name = "Audio bitrate (kbps)")]
    [Range(8, 256)] public int AudioBitrateKbps { get; init; } = 48;

    [Display(Name = "Audio channels (1 = mono)")]
    [Range(1, 2)] public int AudioChannels { get; init; } = 1;

    [Display(Name = "Waveform width (px)")]
    [Range(64, 4096)] public int WaveformWidth { get; init; } = 640;

    [Display(Name = "Waveform height (px)")]
    [Range(16, 1024)] public int WaveformHeight { get; init; } = 120;

    [Display(Name = "Spectrogram width (px)")]
    [Range(64, 4096)] public int SpectrogramWidth { get; init; } = 512;

    [Display(Name = "Spectrogram height (px)")]
    [Range(16, 2048)] public int SpectrogramHeight { get; init; } = 256;

    [Display(Name = "Video frame preview maximum edge (px)")]
    [Range(16, 4096)] public int VideoFrameMaxEdge { get; init; } = 512;

    [Display(Name = "Animation preview frames per second")]
    [Range(1, 30)] public int AnimationFps { get; init; } = 4;

    [Display(Name = "Animation preview maximum length (s)")]
    [Range(1, 60)] public int AnimationMaxSeconds { get; init; } = 10;

    [Display(Name = "Animation preview size (px)")]
    [Range(64, 1024)] public int AnimationSize { get; init; } = 256;

    [Display(Name = "Animation preview quality (lossy WebP)")]
    [Range(1, 100)] public int AnimationQuality { get; init; } = 60;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Elevations.Count is < 1 or > 6)
            yield return new ValidationResult("Between 1 and 6 elevation angles are required.", [nameof(Elevations)]);
        if (Elevations.Any(e => e is < -89 or > 89))
            yield return new ValidationResult("Elevation angles must be between -89° and 89°.", [nameof(Elevations)]);
        if (PrimaryRing >= Elevations.Count)
            yield return new ValidationResult("The primary ring must be one of the elevation rings.", [nameof(PrimaryRing)]);
        if (PrimaryAzimuth >= AzimuthsPerRing)
            yield return new ValidationResult("The primary azimuth must be less than the azimuth count.", [nameof(PrimaryAzimuth)]);
        if (ScaleTooSmallMeters >= ScaleTooLargeMeters)
            yield return new ValidationResult("The 'too small' threshold must be below the 'too large' threshold.", [nameof(ScaleTooSmallMeters)]);
    }

    /// <summary>Validation messages; empty when the options are valid.</summary>
    public IReadOnlyList<string> Errors()
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(this, new ValidationContext(this), results, validateAllProperties: true);
        return results.Select(r => r.ErrorMessage ?? "Invalid value.").ToList();
    }

    /// <summary>Stable version number of these values, stored with each preview to find outdated ones.</summary>
    public int Version()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(this);
        return (int)(XxHash32.HashToUInt32(json) & 0x7FFFFFFF);
    }

    /// <summary>Number of model views (rings × azimuths).</summary>
    public int ViewCount => Elevations.Count * AzimuthsPerRing;

    /// <summary>Index of the primary model view.</summary>
    public int PrimaryViewIndex => PrimaryRing * AzimuthsPerRing + PrimaryAzimuth;
}
