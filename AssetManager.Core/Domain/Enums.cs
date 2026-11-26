namespace AssetManager.Core.Domain;

public enum AssetType
{
    Model3D,
    Image,
    Texture,
    SpriteSheet,
    Audio,
    Music,
    Font,
    Video,
    Material,
    Document,
    Other,
    /// <summary>A motion clip (e.g. a humanoid walk cycle), even when the file carries a stand-in character mesh.</summary>
    Animation,
    /// <summary>A Unity prefab: cataloged so it can be exported with the files it references (design D8). No preview.</summary>
    Prefab,
}

public enum SourceKind { File, Folder, Archive, UnityPackage }

public enum FileRole { Primary, Companion }

/// <summary>How a companion texture was attached to its model (design D20).</summary>
public enum CompanionOrigin
{
    /// <summary>Found from the model's or a Unity material's own references, or the existing file-name rule.</summary>
    Reference,
    /// <summary>Same name after stripping prefixes, map suffixes and the extension.</summary>
    NameMatch,
    /// <summary>Chosen by the AI from rendered candidates.</summary>
    Ai,
    /// <summary>Chosen by the user in the details drawer.</summary>
    User,
}

public enum TagOrigin
{
    User,
    Import,
    AI,
    /// <summary>Materialized from a <see cref="SourceTag"/>; removed when the source tag is removed.</summary>
    Source,
}

public enum AiStatus { NotEnriched, Pending, Enriched, Failed }

/// <summary>A yes/no answer that may be unknown (license flags).</summary>
public enum TriState { Unknown, Yes, No }

public enum TextOrigin { User, AI }

/// <summary>How the AI received an audio asset.</summary>
public enum AiInputMode { None, Visual, Audio, Spectrogram, TextOnly }

public enum PreviewKind
{
    /// <summary>Main grid thumbnail (image preview, primary model view, waveform for audio).</summary>
    Thumbnail,
    /// <summary>One of the multi-angle model renders; Index = ring * azimuths + azimuth.</summary>
    ModelView,
    AudioClip,
    Waveform,
    Spectrogram,
    /// <summary>3×2 contact sheet of the six candidate up-axes.</summary>
    OrientationProbe,
    /// <summary>Unity's embedded preview.png.</summary>
    UnityPreview,
    /// <summary>An animated WebP of an animation clip from one angle.</summary>
    Animation,
    /// <summary>A larger still of a sprite sheet, kept when the thumbnail has fewer than 64 px per cell (design D5).</summary>
    SpriteSheet,
}

/// <summary>Where a sprite sheet's frames came from (design D3).</summary>
public enum GridDetectedBy { SlicingData, Algorithm, User }

public enum ScaleAnomaly { None, TooSmall, TooLarge, NonUnitScale, NonUniform }

public enum OrientationDecidedBy { None, Metadata, AI, User }

public enum JobState { Pending, Running, Completed, Failed, Cancelled }

/// <summary>Fields of an asset or source the user edited; AI enrichment never overwrites these.</summary>
[Flags]
public enum EditedFields
{
    None = 0,
    Name = 1,
    Description = 2,
    Type = 4,
    Style = 8,
    Colors = 16,
    License = 32,
    Links = 64,
    Orientation = 128,
    Notes = 256,
    /// <summary>The sprite sheet grid or the "is a sprite sheet" decision.</summary>
    Grid = 512,
    /// <summary>The model's companion textures (attached, detached or chosen by the user).</summary>
    Textures = 1024,
}
