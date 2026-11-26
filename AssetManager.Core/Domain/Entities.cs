namespace AssetManager.Core.Domain;

/// <summary>A storage volume identified by its serial number, so re-lettered drives still resolve.</summary>
public class Volume
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string? LastRoot { get; set; }
    public DateTime LastSeen { get; set; }
}

public class LicenseInfo
{
    public string? Kind { get; set; }
    public TriState Commercial { get; set; }
    public TriState Attribution { get; set; }
    public string? AttributionText { get; set; }
    /// <summary>Path of the document the license was derived from.</summary>
    public string? Evidence { get; set; }

    public LicenseInfo Clone() => (LicenseInfo)MemberwiseClone();
}

public sealed record LinkInfo(string Url, string? Label = null, string? Evidence = null);

public sealed record ColorInfo(string Hex, string Name, double Weight);

/// <summary>Asset Store package metadata from the .unitypackage gzip header.</summary>
public class StoreInfo
{
    public string? StoreId { get; set; }
    public string? Title { get; set; }
    public string? Publisher { get; set; }
    public string? Version { get; set; }
    public string? Url { get; set; }
}

public class Source
{
    public long Id { get; set; }
    public SourceKind Kind { get; set; }
    public Location Location { get; set; } = new(null, "");
    /// <summary>Denormalized from <see cref="Location"/> for availability queries.</summary>
    public string? VolumeId { get; set; }
    public string DisplayName { get; set; } = "";
    /// <summary>size + mtime + head/tail hash of the outer file or folder listing.</summary>
    public string? Fingerprint { get; set; }
    public long? ParentSourceId { get; set; }
    public Source? ParentSource { get; set; }
    public StoreInfo Store { get; set; } = new();
    public string? Notes { get; set; }
    public string? Description { get; set; }
    public LicenseInfo License { get; set; } = new();
    public List<LinkInfo> Links { get; set; } = [];
    public EditedFields UserEdited { get; set; }
    public DateTime ImportedAt { get; set; }
    public List<Asset> Assets { get; set; } = [];

    // Staging (see ImportSession). Null ImportSessionId = committed catalog row.
    public long? ImportSessionId { get; set; }
    /// <summary>The import session that created this source (kept after confirm, so unfinished AI work can find it).</summary>
    public long? CommittedFromSessionId { get; set; }
    /// <summary>Error that prevented reading the source (corrupt/encrypted archive).</summary>
    public string? Error { get; set; }
    /// <summary>Existing source this staged source is proposed to replace (new package version).</summary>
    public long? ReplacesSourceId { get; set; }
    public double ReplaceScore { get; set; }
    /// <summary>User decision on the replacement proposal: null = pending (accept by default), false = import as new.</summary>
    public bool? ReplaceAccepted { get; set; }
}

public class Asset
{
    public long Id { get; set; }
    public long SourceId { get; set; }
    public Source Source { get; set; } = null!;
    public AssetType Type { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<string> Style { get; set; } = [];
    public List<ColorInfo> Colors { get; set; } = [];
    public bool IsGameAsset { get; set; } = true;

    public ImageMetadata? Image { get; set; }
    /// <summary>Frames and grid of a sprite sheet (design D3); null when the image is not a sheet.</summary>
    public SpriteSheetMetadata? SpriteSheet { get; set; }
    public AudioMetadata? Audio { get; set; }
    public ModelMetadata? Model { get; set; }
    public FontMetadata? Font { get; set; }
    public VideoMetadata? Video { get; set; }
    public DocumentMetadata? Document { get; set; }

    // Flattened model issue flags (searchable).
    public ScaleAnomaly ScaleAnomaly { get; set; }
    public bool OrientationUncertain { get; set; }
    public bool OrientationVerified { get; set; }
    public bool TexturesMissing { get; set; }

    /// <summary>Flattened from sprite detection: the grid detector was not confident, so no grid was stored (review filter).</summary>
    public bool GridUncertain { get; set; }

    // User-owned (design D10): no AI, import or re-import code writes these.
    public bool Favorite { get; set; }
    /// <summary>0 = unrated, 1-5 stars.</summary>
    public int Rating { get; set; }
    public string? Notes { get; set; }

    public AiStatus AiStatus { get; set; }
    public AiInputMode AiInputMode { get; set; }
    public string? AiError { get; set; }
    public EditedFields UserEdited { get; set; }

    /// <summary>Original files are stored inside the catalog.</summary>
    public bool IsStored { get; set; }

    // Optional per-asset license override (null = inherit from the source).
    public bool HasLicenseOverride { get; set; }
    public LicenseInfo LicenseOverride { get; set; } = new();
    public List<LinkInfo>? LinksOverride { get; set; }

    public string? PreviewError { get; set; }
    public int PreviewOptionsVersion { get; set; }
    public bool EmbeddingDirty { get; set; } = true;

    public DateTime ImportedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<AssetFile> Files { get; set; } = [];
    public List<AssetTag> Tags { get; set; } = [];

    // Staging (see ImportSession). Null ImportSessionId = committed catalog row.
    public long? ImportSessionId { get; set; }
    /// <summary>Staged item will be created on confirm.</summary>
    public bool Included { get; set; } = true;
    public long? DuplicateOfAssetId { get; set; }
    /// <summary>For a new source version: the existing asset this staged asset updates in place.</summary>
    public long? ReplacesAssetId { get; set; }

    public LicenseInfo EffectiveLicense => HasLicenseOverride ? LicenseOverride : Source.License;
    public IReadOnlyList<LinkInfo> EffectiveLinks => LinksOverride ?? Source.Links;
    public AssetFile? PrimaryFile => Files.FirstOrDefault(f => f.Role == FileRole.Primary);
}

public class AssetFile
{
    public long Id { get; set; }
    public long AssetId { get; set; }
    public Asset Asset { get; set; } = null!;
    public FileRole Role { get; set; }
    public Location Location { get; set; } = new(null, "");
    /// <summary>Path relative to the source root (Unity: the original Assets/... path).</summary>
    public string RelativePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public long Size { get; set; }
    /// <summary>Size + head/tail sample hash, for quick duplicate candidates.</summary>
    public string? FastHash { get; set; }
    /// <summary>Full XxHash3 of the content, computed on demand.</summary>
    public string? ContentHash { get; set; }
    public string? UnityGuid { get; set; }
    /// <summary>Companion textures: the model material this file is applied to, when known (Unity .mat or model reference).</summary>
    public string? Material { get; set; }
    /// <summary>Companion textures: the renderer slot ("map", "normalMap", "emissiveMap"), when known.</summary>
    public string? Slot { get; set; }
    /// <summary>Companion textures: how the file was attached (design D20).</summary>
    public CompanionOrigin Origin { get; set; }
    public StoredFile? Stored { get; set; }
}

public class Tag
{
    public long Id { get; set; }
    /// <summary>Normalized (lower-case, trimmed, single-spaced) unique name.</summary>
    public string Name { get; set; } = "";
    public List<AssetTag> Assets { get; set; } = [];
}

public class AssetTag
{
    public long AssetId { get; set; }
    public Asset Asset { get; set; } = null!;
    public long TagId { get; set; }
    public Tag Tag { get; set; } = null!;
    public TagOrigin Origin { get; set; }
}

/// <summary>
/// A tag that belongs to a whole source (design D1). Each asset of the source carries it as an <see cref="AssetTag"/>
/// with <see cref="TagOrigin.Source"/>, minus <see cref="AssetTagOptOut"/>s.
/// </summary>
public class SourceTag
{
    public long SourceId { get; set; }
    public long TagId { get; set; }
    public Tag Tag { get; set; } = null!;
    /// <summary>Who added it: <see cref="TagOrigin.User"/> or <see cref="TagOrigin.AI"/> (consensus).</summary>
    public TagOrigin Origin { get; set; }
}

/// <summary>The user removed a source tag from this one asset; <c>SourceTagService.ApplyAsync</c> never re-adds it.</summary>
public class AssetTagOptOut
{
    public long AssetId { get; set; }
    public long TagId { get; set; }
}

/// <summary>A collection is a tag rule; membership is computed by query, never stored.</summary>
public class Collection
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public List<string> IncludeTags { get; set; } = [];
    public List<string> ExcludeTags { get; set; } = [];
    public string? Description { get; set; }
    public TextOrigin DescriptionOrigin { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class PreviewBlob
{
    public long Id { get; set; }
    public long AssetId { get; set; }
    public PreviewKind Kind { get; set; }
    public int Index { get; set; }
    public string Mime { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[] Bytes { get; set; } = [];
    public int OptionsVersion { get; set; }
}

public class StoredFile
{
    public long Id { get; set; }
    public long AssetFileId { get; set; }
    public AssetFile AssetFile { get; set; } = null!;
    public long Size { get; set; }
    public int ChunkCount { get; set; }
    public DateTime StoredAt { get; set; }
}

public class StoredFileChunk
{
    public long StoredFileId { get; set; }
    public int Seq { get; set; }
    public byte[] Bytes { get; set; } = [];
}

public class Setting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>Text embedding of an asset (source of truth; the vec0/in-memory index is derived from it).</summary>
public class AssetEmbedding
{
    public long AssetId { get; set; }
    /// <summary>Little-endian float32 vector (Matryoshka-truncated).</summary>
    public byte[] Vector { get; set; } = [];
    public string Model { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}
