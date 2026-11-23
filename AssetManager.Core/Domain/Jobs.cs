namespace AssetManager.Core.Domain;

/// <summary>A persisted background job with its latest progress snapshot (design D8).</summary>
public class Job
{
    public long Id { get; set; }
    /// <summary>Handler key, e.g. "import.scan", "ai.enrich", "previews.regenerate".</summary>
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public JobState State { get; set; }
    public int Attempts { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    // Progress snapshot.
    public string? Stage { get; set; }
    public int StageIndex { get; set; }
    public int StageCount { get; set; } = 1;
    public long Done { get; set; }
    public long Total { get; set; }
    public string? CurrentItem { get; set; }
    public int Warnings { get; set; }
    public int Errors { get; set; }
    public double? EtaSeconds { get; set; }

    /// <summary>Outcome counts (e.g. updated/added/removed) as JSON.</summary>
    public string? SummaryJson { get; set; }
    public string? Error { get; set; }
    public long? ParentJobId { get; set; }

    public bool IsFinished => State is JobState.Completed or JobState.Failed or JobState.Cancelled;
}

public class JobItemError
{
    public long Id { get; set; }
    public long JobId { get; set; }
    /// <summary>Item reference understood by the job type (asset id, path, …).</summary>
    public string ItemRef { get; set; } = "";
    public string? ItemName { get; set; }
    public string Reason { get; set; } = "";
    public bool IsWarning { get; set; }
}
