using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssetManager.Core.Domain;

namespace AssetManager.Core.Search;

public enum SearchSort { Relevance, Newest, Name }

public enum AvailabilityFilter { Available, Unavailable, Stored }

/// <summary>
/// One search (spec "Combined filters"): every set filter must hold (AND). Also the drill-down target of dashboard
/// counts, so it round-trips through a URL (<see cref="ToToken"/> / <see cref="FromToken"/>).
/// </summary>
public sealed record SearchQuery
{
    public IReadOnlyList<AssetType> Types { get; init; } = [];

    /// <summary>Free text; every word matches as a prefix (name, file names, paths, description, tags, source).</summary>
    public string? Text { get; init; }

    public IReadOnlyList<string> IncludeTags { get; init; } = [];

    public IReadOnlyList<string> ExcludeTags { get; init; } = [];

    public long? CollectionId { get; init; }

    /// <summary>Assets of this source and of the sources nested in it.</summary>
    public long? SourceId { get; init; }

    /// <summary>The effective license's commercial use: Yes (allowed), No, or Unknown.</summary>
    public TriState? Commercial { get; init; }

    /// <summary>The effective license's attribution requirement: Yes (required), No, or Unknown.</summary>
    public TriState? Attribution { get; init; }

    public AvailabilityFilter? Availability { get; init; }

    public bool ScaleAnomaly { get; init; }

    public bool OrientationUncertain { get; init; }

    public bool OrientationNotVerified { get; init; }

    public bool TexturesMissing { get; init; }

    public AiStatus? AiStatus { get; init; }

    /// <summary>Rank by meaning (embeddings) instead of words; the other filters still apply.</summary>
    public bool Semantic { get; init; }

    /// <summary>"Find similar": rank by similarity to this asset's embedding.</summary>
    public long? SimilarTo { get; init; }

    public SearchSort Sort { get; init; } = SearchSort.Relevance;

    [JsonIgnore]
    public bool IsEmpty => this == new SearchQuery { Sort = Sort };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>A URL-safe token for navigation (e.g. <c>browse?q=...</c>).</summary>
    public string ToToken() => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this, Json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static SearchQuery FromToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return new SearchQuery();
        try
        {
            var b64 = token.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            return JsonSerializer.Deserialize<SearchQuery>(Convert.FromBase64String(b64), Json) ?? new SearchQuery();
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return new SearchQuery();
        }
    }

    // Record equality compares lists by reference; compare their contents instead.
    public bool Equals(SearchQuery? other) => other is not null && ToKey() == other.ToKey();

    public override int GetHashCode() => ToKey().GetHashCode();

    private string ToKey() => Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(this, Json));
}
