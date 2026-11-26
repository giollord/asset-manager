using System.ComponentModel;
using AssetManager.AI.Runtime;
using AssetManager.Core.Domain;
using AssetManager.Processing.Handlers;
using Microsoft.Extensions.AI;

namespace AssetManager.AI.Enrichment;

/// <summary>The texture matching settings in effect (Settings → AI).</summary>
public sealed record TextureMatchOptions(bool Enabled = true, double MatchThreshold = 0.7, double SuggestThreshold = 0.4)
{
    public static TextureMatchOptions From(Core.Settings.AppSettings s) => new(s.TextureMatchEnabled, s.TextureMatchThreshold, s.TextureSuggestThreshold);
}

/// <summary>The AI's texture choice for one material.</summary>
public sealed record TextureResult : IAiResult
{
    /// <summary>Asked first so the model looks at every render before choosing.</summary>
    [Description("For each candidate, in a few words: does the texture fit the model's shape, or does it look smeared, stretched or random?")]
    public List<string> Observations { get; init; } = [];

    [Description("Number of the candidate that makes the model look as intended, or 0 when none fits.")]
    public int Choice { get; init; }

    [Description("Confidence from 0 to 1.")]
    public double Confidence { get; init; }

    [Description("One short sentence: why this candidate (or none).")]
    public string Reason { get; init; } = "";

    public IEnumerable<string> Validate()
    {
        if (Choice is < 0 or > TextureMatcher.MaxCandidates) yield return $"choice must be between 0 and {TextureMatcher.MaxCandidates}";
        if (Confidence is < 0 or > 1 || double.IsNaN(Confidence)) yield return "confidence must be between 0 and 1";
    }
}

/// <summary>What the judgment did for one material.</summary>
public sealed record TextureDecision(string Material, TextureCandidate? Chosen, double Confidence, bool Attached, bool Suggested, string Reason);

/// <summary>
/// The AI texture choice (design D20, spec "Choosing a model's texture", "Texture choice consistency"): for each material
/// without a base colour, the model sees the model rendered as it is and with each candidate, and answers a candidate
/// or 0. It is asked again with the candidates in reverse order; a different answer counts as unsure. Confident choices
/// are attached (origin AI), less confident ones become suggestions. Without image input, the best-ranked candidate
/// only becomes a suggestion.
/// </summary>
public static class TextureJudge
{
    /// <summary>The last line of the text that is the same for every material.</summary>
    public const string FixedEnd = "About this model and its candidates:";

    public static List<ChatMessage> BuildPrompt(Asset asset, MaterialToMatch match, IReadOnlyList<byte[]> renders, IReadOnlyList<int> order)
    {
        var lines = order.Select((c, i) => $"Candidate {i + 1}: {match.Candidates[c].RelativePath}");
        // Fixed rules first, the model's own lines last: every material of a pack starts with the same text, which
        // llama-server reuses from its cache (fast-portable-startup design D8).
        var text = $"""
            A 3D model has a material that has no texture.
            The first image shows the model as it is. Each following image shows the model with one candidate texture of the same pack applied to that material.
            Which candidate makes the model look as its artist intended: details that follow the model's shape (planks along the boards, a face on the head, bark on the trunk)?
            A wrong texture looks smeared, stretched, noisy or random on the model. Answer 0 when no candidate fits.
            confidence is how sure you are, from 0 to 1.
            {FixedEnd}
            The model is "{asset.Name}"; the material is "{match.Material}"{(match.MissingReference is null ? "" : $" (it refers to \"{match.MissingReference}\", which the pack does not contain)")}.
            The candidates:
            {string.Join("\n", lines)}
            """;
        var contents = new List<AIContent> { new TextContent(text), new TextContent("The model as it is:"), new DataContent(renders[0], "image/jpeg") };
        for (var i = 0; i < order.Count; i++)
        {
            contents.Add(new TextContent($"Candidate {i + 1}:"));
            contents.Add(new DataContent(renders[order[i] + 1], "image/jpeg"));
        }
        return [new ChatMessage(ChatRole.User, contents)];
    }

    /// <summary>Judges and applies every material of <paramref name="asset"/> that needs a texture; returns the decisions.</summary>
    public static async Task<IReadOnlyList<TextureDecision>> MatchAsync(StructuredChat chat, Asset asset, ITextureMatcher matcher, AiModalities modalities,
        TextureMatchOptions options, CancellationToken ct = default)
    {
        if (!options.Enabled || asset.UserEdited.HasFlag(EditedFields.Textures)) return [];
        var decisions = new List<TextureDecision>();
        foreach (var match in await matcher.PlanAsync(asset.Id, ct))
        {
            TextureDecision decision;
            if (!modalities.Vision || !matcher.CanRender)
            {
                var best = match.Candidates[0];
                decision = new TextureDecision(match.Material, best, 0, false, true, "Best name match; not checked by the AI.");
            }
            else
            {
                var renders = await matcher.RenderCandidatesAsync(asset.Id, match, ct);
                var natural = Enumerable.Range(0, match.Candidates.Count).ToList();
                var first = await chat.AskAsync<TextureResult>(BuildPrompt(asset, match, renders, natural), ct: ct);
                var firstChoice = Pick(first, natural);
                var confidence = first.Confidence;
                if (firstChoice is not null && confidence >= options.SuggestThreshold && match.Candidates.Count > 1)
                {
                    // Self-consistency: the same question with the candidates reversed must point at the same texture.
                    var reversed = Enumerable.Reverse(natural).ToList();
                    var second = await chat.AskAsync<TextureResult>(BuildPrompt(asset, match, renders, reversed), ct: ct);
                    confidence = Pick(second, reversed) == firstChoice ? Math.Min(first.Confidence, second.Confidence) : 0;
                }
                var chosen = firstChoice is { } c ? match.Candidates[c] : null;
                var attach = chosen is not null && confidence >= options.MatchThreshold;
                var suggest = chosen is not null && !attach && confidence >= options.SuggestThreshold;
                decision = new TextureDecision(match.Material, chosen, confidence, attach, suggest, first.Reason);
            }
            if (decision.Attached)
                await matcher.AttachAsync(asset, match.Material, decision.Chosen!.FileId, CompanionOrigin.Ai, match.MissingReference, ct);
            else if (decision.Suggested)
                TextureMatcher.Suggest(asset, new TextureSuggestion(match.Material, decision.Chosen!.FileId, decision.Chosen.RelativePath, Math.Round(decision.Confidence, 2), decision.Reason));
            decisions.Add(decision);
        }
        return decisions;
    }

    /// <summary>The candidate index an answer points at in the shown <paramref name="order"/>; null for 0.</summary>
    private static int? Pick(TextureResult answer, IReadOnlyList<int> order) =>
        answer.Choice >= 1 && answer.Choice <= order.Count ? order[answer.Choice - 1] : null;
}
