namespace AssetManager.Core.Domain;

public enum ImportSessionState { Scanning, Ready, Committing, Committed, Cancelled }

public enum AiDepth { Full, PackOnly, None }

/// <summary>Why a staged entry does not become an asset.</summary>
public enum SkipKind
{
    /// <summary>Code and project files: summarized in the source notes.</summary>
    Noted,
    /// <summary>License/readme/link documents: read for the source's license and links.</summary>
    Document,
    Ignored,
    Duplicate,
    Unsupported,
    Error,
}

/// <summary>
/// A staged import. Staged sources and assets are ordinary <see cref="Source"/>/<see cref="Asset"/> rows whose
/// <c>ImportSessionId</c> is set; global query filters hide them from the catalog until confirm clears it.
/// </summary>
public class ImportSession
{
    public long Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public ImportSessionState State { get; set; }
    public List<string> Inputs { get; set; } = [];
    public List<string> SharedTags { get; set; } = [];
    public string? Instruction { get; set; }
    public AiDepth AiDepth { get; set; }
    public bool IncludeDuplicates { get; set; }
    /// <summary>Folder ignore rules added for this import only (spec "Ignored folders").</summary>
    public List<string> IgnoredFolders { get; set; } = [];
    /// <summary>Whether the catalog's default ignored folders apply to this import.</summary>
    public bool UseDefaultIgnores { get; set; } = true;
    /// <summary>Import into this existing source (spec "Acting on rescan results"): its staged root merges into it on confirm.</summary>
    public long? IntoSourceId { get; set; }
    /// <summary>When set, only these paths (relative to the single input folder) are staged: the files a rescan found added.</summary>
    public List<string>? OnlyPaths { get; set; }
    public long? JobId { get; set; }
    /// <summary>Last staging stage that finished (0 none, 1 scan, 2 grouping, 3 metadata and previews, 4 pack pass, 5 item pass).</summary>
    public int StagedThrough { get; set; }
    public string? Error { get; set; }
}

/// <summary>An entry that will not become an asset, with the reason (script, duplicate, unsupported, error).</summary>
public class StagedSkip
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public long? SourceId { get; set; }
    public string Path { get; set; } = "";
    public string Reason { get; set; } = "";
    public SkipKind Kind { get; set; }
    /// <summary>Where the entry is (documents and noted files are read again by the AI pack pass).</summary>
    public Location? Location { get; set; }
}
