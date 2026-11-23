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
}

public enum SourceKind { File, Folder, Archive, UnityPackage }

public enum FileRole { Primary, Companion }

public enum TagOrigin { User, Import, AI }

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
}

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
}
