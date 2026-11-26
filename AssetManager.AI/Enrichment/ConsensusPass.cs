using System.ComponentModel;
using System.Text;
using AssetManager.Core.Domain;
using AssetManager.Core.Settings;
using AssetManager.Data;
using AssetManager.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace AssetManager.AI.Enrichment;

[Description("For each candidate: does it describe the whole source?")]
public sealed record ConsensusResult : IAiResult
{
    /// <summary>The candidates the request contained; set by the caller, not part of the schema.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<string>? Expected { get; set; }

    [Description("One decision per candidate, in the order given.")]
    public List<ConsensusDecision> Decisions { get; init; } = [];

    public IEnumerable<string> Validate()
    {
        var got = Decisions.Select(d => Key(d.Tag)).ToList();
        foreach (var dup in got.GroupBy(g => g).Where(g => g.Count() > 1)) yield return $"candidate \"{dup.Key}\" appears more than once";
        if (Expected is null) yield break;
        var expected = Expected.Select(Key).ToHashSet();
        var missing = expected.Where(e => !got.Contains(e)).ToList();
        var extra = got.Where(g => !expected.Contains(g)).ToList();
        if (missing.Count > 0) yield return $"missing decisions for: {string.Join(", ", missing)}";
        if (extra.Count > 0) yield return $"unknown candidates (use the names exactly as given): {string.Join(", ", extra)}";
    }

    public static string Key(string tag) => TagService.Normalize(tag);
}

public sealed record ConsensusDecision
{
    [Description("The candidate's name exactly as given.")]
    public string Tag { get; init; } = "";

    [Description("True only when the candidate describes the whole source, including the assets that lack it (e.g. the same art style); false for a mixed source.")]
    public bool ApplyToSource { get; init; }

    [Description("One short English sentence explaining the decision.")]
    public string Reason { get; init; } = "";
}

/// <summary>Sample previews of assets with and without one candidate (JPEG/PNG/WebP bytes).</summary>
public sealed record ConsensusSamples(IReadOnlyList<byte[]> With, IReadOnlyList<byte[]> Without);

/// <summary>
/// The tag consensus step (design D2, spec "Source tag consensus decision"): tags and styles most of a source's game
/// assets share are shown to the AI with counts and samples; the ones it accepts become source tags with origin AI.
/// It only ever adds tags.
/// </summary>
public static class ConsensusPass
{
    /// <summary>At most this many candidates per request (each brings two contact sheets).</summary>
    public const int MaxCandidates = 6;

    public const int SamplesPerSide = 4;

    public static List<ChatMessage> BuildPrompt(Source source, ConsensusInput input, IReadOnlyDictionary<string, ConsensusSamples>? samples)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Decide which of these tags describe a whole game asset pack.");
        sb.AppendLine($"Pack: \"{source.DisplayName}\"");
        if (!string.IsNullOrWhiteSpace(source.Description)) sb.AppendLine($"Pack description: {source.Description.Trim()}");
        sb.AppendLine($"Game assets: {input.GameAssets} ({string.Join(", ", input.TypeCounts.OrderByDescending(t => t.Value).Select(t => $"{t.Value} {t.Key}"))})");
        sb.AppendLine();
        sb.AppendLine("Each candidate is a tag or style that many of its assets already have. Accept a candidate only when it is true of the pack as a whole,");
        sb.AppendLine("including the assets that lack it (for example icons drawn in the same pixel-art style). Reject it when the pack is mixed");
        sb.AppendLine("(for example low-poly props together with realistic skyboxes) or when it names only some of the content.");
        sb.AppendLine();
        foreach (var c in input.Candidates)
        {
            var perType = c.PerType.Count > 1 ? "; by type: " + string.Join(", ", c.PerType.Select(t => $"{t.Count} of {t.Total} {t.Type}")) : "";
            var kind = c.FromTag && c.FromStyle ? "tag and style" : c.FromStyle ? "style" : "tag";
            sb.AppendLine($"Candidate \"{c.Name}\" ({kind}): {c.Count} of {c.Total} game assets ({c.Share:P0}){perType}");
        }
        if (samples is { Count: > 0 })
            sb.AppendLine("\nFor each candidate, a sheet of assets that have it and a sheet of assets that lack it follow.");

        var contents = new List<AIContent> { new TextContent(sb.ToString()) };
        if (samples is not null)
            foreach (var c in input.Candidates)
            {
                if (!samples.TryGetValue(c.Name, out var s)) continue;
                if (s.With.Count > 0)
                {
                    contents.Add(new TextContent($"Assets with \"{c.Name}\":"));
                    contents.Add(new DataContent(ItemPass.ContactSheet(s.With.Select(b => ("", b)).ToList(), labels: false), "image/jpeg"));
                }
                if (s.Without.Count > 0)
                {
                    contents.Add(new TextContent($"Assets without \"{c.Name}\":"));
                    contents.Add(new DataContent(ItemPass.ContactSheet(s.Without.Select(b => ("", b)).ToList(), labels: false), "image/jpeg"));
                }
            }
        return [new ChatMessage(ChatRole.User, contents)];
    }

    public static Task<ConsensusResult> RunAsync(StructuredChat chat, Source source, ConsensusInput input, IReadOnlyDictionary<string, ConsensusSamples>? samples,
        CancellationToken ct = default)
    {
        var expected = input.Candidates.Select(c => c.Name).ToList();
        return chat.AskAsync<ConsensusResult>(BuildPrompt(source, input, samples), r => { r.Expected = expected; return []; }, ct);
    }

    /// <summary>
    /// Runs the step for one source: finds candidates with the thresholds from <paramref name="settings"/>, asks the AI
    /// when there are any, and adds the accepted ones as AI source tags.
    /// </summary>
    /// <returns>The tags that became source tags (empty when the step is off or nothing was accepted).</returns>
    /// <param name="force">Run even when the automatic step is turned off (the user asked for it).</param>
    public static async Task<IReadOnlyList<string>> RunForSourceAsync(CatalogStore catalog, StructuredChat chat, long sourceId, AppSettings settings, bool vision,
        CancellationToken ct = default, bool force = false)
    {
        if (!settings.ConsensusEnabled && !force) return [];
        await using var db = catalog.CreateDbContext();
        var source = await db.AllSources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source is null) return [];
        var input = await ConsensusCandidates.FindAsync(db, sourceId, settings.ConsensusShare, settings.ConsensusMinAssets, ct);
        if (input.Candidates.Count == 0) return [];
        input = input with { Candidates = input.Candidates.Take(MaxCandidates).ToList() };
        var samples = vision ? await SamplesAsync(db, input, ct) : null;

        var result = await RunAsync(chat, source, input, samples, ct);
        var names = input.Candidates.ToDictionary(c => ConsensusResult.Key(c.Name), c => c.Name);
        var accepted = result.Decisions.Where(d => d.ApplyToSource && names.ContainsKey(ConsensusResult.Key(d.Tag)))
            .Select(d => names[ConsensusResult.Key(d.Tag)]).Distinct().ToList();
        if (accepted.Count > 0) await SourceTagService.AddAsync(db, sourceId, accepted, TagOrigin.AI, ct);
        return accepted;
    }

    /// <summary>Up to <see cref="SamplesPerSide"/> thumbnails of assets with and without each candidate, spread over the source.</summary>
    private static async Task<Dictionary<string, ConsensusSamples>> SamplesAsync(CatalogDbContext db, ConsensusInput input, CancellationToken ct)
    {
        var all = await db.AllAssets.AsNoTracking().Where(a => a.SourceId == input.SourceId && a.IsGameAsset && a.Included)
            .OrderBy(a => a.Id).Select(a => a.Id).ToListAsync(ct);
        var picks = new Dictionary<string, (List<long> With, List<long> Without)>();
        foreach (var c in input.Candidates)
        {
            var with = all.Where(c.Carriers.Contains).ToList();
            var without = all.Where(id => !c.Carriers.Contains(id)).ToList();
            picks[c.Name] = (Spread(with), Spread(without));
        }
        var ids = picks.Values.SelectMany(p => p.With.Concat(p.Without)).Distinct().ToList();
        var thumbs = (await db.Previews.AsNoTracking()
                .Where(p => ids.Contains(p.AssetId) && p.Kind == PreviewKind.Thumbnail && p.Mime.StartsWith("image/"))
                .Select(p => new { p.AssetId, p.Bytes }).ToListAsync(ct))
            .GroupBy(p => p.AssetId).ToDictionary(g => g.Key, g => g.First().Bytes);
        return picks.ToDictionary(p => p.Key, p => new ConsensusSamples(
            p.Value.With.Where(thumbs.ContainsKey).Select(id => thumbs[id]).ToList(),
            p.Value.Without.Where(thumbs.ContainsKey).Select(id => thumbs[id]).ToList()));

        static List<long> Spread(List<long> ids) => ids.Count <= SamplesPerSide
            ? ids
            : Enumerable.Range(0, SamplesPerSide).Select(i => ids[i * ids.Count / SamplesPerSide]).ToList();
    }
}
