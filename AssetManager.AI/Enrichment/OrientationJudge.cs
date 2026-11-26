using System.Numerics;
using AssetManager.Core.Domain;
using AssetManager.Processing.Models;
using Microsoft.Extensions.AI;

namespace AssetManager.AI.Enrichment;

/// <summary>What the judgment decided for one model.</summary>
/// <param name="Orientation">The orientation to store (unchanged when the answer was not confident).</param>
/// <param name="Rerender">The rotation differs from the one the current previews were rendered with.</param>
public sealed record OrientationDecision(ModelOrientation Orientation, bool Rerender, OrientationResult? Answer)
{
    /// <summary>False when the two answers of the consistency check pointed at different candidates.</summary>
    public bool Consistent { get; init; } = true;
}

/// <summary>
/// The AI orientation judgment (design D6/D7): the model sees the 3×2 probe sheet (candidates 1-6) plus the geometric
/// ranking as a hint, and answers a candidate or 0 for "no natural up", a yaw fix and a confidence. Below the
/// threshold nothing changes and the model is marked "orientation uncertain". User decisions are never touched.
/// </summary>
public static class OrientationJudge
{
    public const double DefaultThreshold = 0.6;

    /// <summary>Models whose orientation the AI should judge: a probe exists and the user has not decided.</summary>
    public static bool NeedsJudgment(Asset asset, IReadOnlyList<PreviewBlob> previews) =>
        asset.Model is { Orientation.DecidedBy: not OrientationDecidedBy.User }
        && previews.Any(p => p.Kind == PreviewKind.OrientationProbe);

    /// <summary>Presentation order of the consistency check: every candidate moves to another position.</summary>
    public static readonly int[] ShuffledOrder = [6, 5, 4, 3, 2, 1];

    private static readonly int[] NaturalOrder = [1, 2, 3, 4, 5, 6];

    /// <summary>
    /// The probe tiles as separate images, presented in <paramref name="order"/> (order[k-1] is the probe candidate
    /// shown as "Candidate k"). The geometric ranking is passed as a hint in the shown numbering.
    /// </summary>
    /// <summary>The last line of the text that is the same for every model.</summary>
    public const string FixedEnd = "About this model:";

    public static List<ChatMessage> BuildPrompt(Asset asset, PreviewBlob probe, IReadOnlyList<int>? order = null)
    {
        order ??= NaturalOrder;
        var shownOf = order.Select((candidate, i) => (candidate, shown: i + 1)).ToDictionary(x => x.candidate, x => x.shown);
        var ranking = asset.Model?.ProbeRanking is { Count: > 0 } r ? string.Join(", ", r.Where(shownOf.ContainsKey).Select(c => shownOf[c])) : "none";
        // Fixed rules first, the model's own lines last: every model of a pack starts with the same text, which
        // llama-server reuses from its cache (fast-portable-startup design D8).
        var text = $"""
            The images show the same 3D model in 6 candidate orientations, each preceded by its number.
            Which candidate shows it upright, the way it would stand in a game world (characters and trees standing on their feet or base, buildings on their foundation, vehicles on their wheels)?
            Upside-down and lying candidates are wrong.
            Answer 0 when the object has no natural up direction (a flat floor tile, a rock, a sphere, an abstract shape).
            yaw_fix is an optional extra turn around the vertical axis so the front faces the viewer; use 0 when unsure.
            confidence is how sure you are, from 0 to 1.
            {FixedEnd}
            The model is "{asset.Name}".
            A geometric heuristic ranks the candidates (most likely first): {ranking}. It is only a hint; trust what you see.
            """;
        var tiles = SplitProbe(probe.Bytes).ToDictionary(t => t.Label, t => t.Jpeg);
        var contents = new List<AIContent> { new TextContent(text) };
        for (var i = 0; i < order.Count; i++)
        {
            contents.Add(new TextContent($"Candidate {i + 1}:"));
            contents.Add(new DataContent(tiles[order[i]], "image/jpeg"));
        }
        return [new ChatMessage(ChatRole.User, contents)];
    }

    /// <summary>
    /// Cuts the 3×2 probe sheet into its six tiles. Small vision models mix up tiles of a composed sheet; separate
    /// images, each introduced by its number, are judged far more reliably.
    /// </summary>
    public static IEnumerable<(int Label, byte[] Jpeg)> SplitProbe(byte[] sheet)
    {
        using var image = new ImageMagick.MagickImage(sheet);
        var w = image.Width / 3;
        var h = image.Height / 2;
        for (var i = 0; i < 6; i++)
        {
            using var tile = (ImageMagick.MagickImage)image.CloneArea((int)(i % 3 * w), (int)(i / 3 * h), w, h);
            tile.BackgroundColor = new ImageMagick.MagickColor(255, 255, 255);
            tile.Alpha(ImageMagick.AlphaAction.Remove);
            // Hide the number drawn on the sheet: the prompt numbers the tiles itself, possibly in another order.
            new ImageMagick.Drawing.Drawables().FillColor(ImageMagick.MagickColors.White).Rectangle(0, 0, w * 0.18, h * 0.18).Draw(tile);
            tile.Quality = 90;
            yield return (i + 1, tile.ToByteArray(ImageMagick.MagickFormat.Jpeg));
        }
    }

    public static async Task<OrientationDecision> JudgeAsync(StructuredChat chat, Asset asset, PreviewBlob probe, double threshold = DefaultThreshold, CancellationToken ct = default)
    {
        var current = asset.Model?.Orientation ?? new ModelOrientation();
        var first = await chat.AskAsync<OrientationResult>(BuildPrompt(asset, probe), ct: ct);
        if (first.Confidence < threshold) return Decide(current, first, threshold);

        // Self-consistency: ask again with the candidates in another order. Small models are often confidently wrong,
        // and a guess rarely lands on the same candidate twice; disagreement means "uncertain".
        var second = await chat.AskAsync<OrientationResult>(BuildPrompt(asset, probe, ShuffledOrder), ct: ct);
        var secondChoice = second.Choice == 0 ? 0 : ShuffledOrder[second.Choice - 1];
        var agreed = secondChoice == first.Choice;
        var answer = first with { Confidence = agreed ? Math.Min(first.Confidence, second.Confidence) : 0 };
        return Decide(current, answer, threshold) with { Answer = answer, Consistent = agreed };
    }

    /// <summary>Turns an answer into the orientation to store; pure, so the rules can be tested without a model.</summary>
    public static OrientationDecision Decide(ModelOrientation current, OrientationResult answer, double threshold = DefaultThreshold)
    {
        if (current.DecidedBy == OrientationDecidedBy.User) return new OrientationDecision(current, false, answer);
        if (answer.Confidence < threshold)
            return new OrientationDecision(current with { Uncertain = true, Verified = false, Confidence = answer.Confidence }, false, answer);

        ModelOrientation next;
        if (answer.Choice == 0)
            next = new ModelOrientation { DecidedBy = OrientationDecidedBy.AI, Confidence = answer.Confidence, Verified = true, NoNaturalUp = true };
        else
        {
            var c = OrientationProbe.Candidates[answer.Choice - 1];
            var (x, y, z) = WithYaw(c.RotX, c.RotY, c.RotZ, answer.YawFix);
            next = new ModelOrientation { RotX = x, RotY = y, RotZ = z, DecidedBy = OrientationDecidedBy.AI, Confidence = answer.Confidence, Verified = true };
        }
        return new OrientationDecision(next, !SameRotation(current, next), answer);
    }

    /// <summary>Stores the decision on the asset (metadata and the flattened search flags).</summary>
    public static void Apply(Asset asset, OrientationDecision decision)
    {
        if (asset.Model is null) return;
        asset.Model = asset.Model with { Orientation = decision.Orientation };
        asset.OrientationVerified = decision.Orientation.Verified;
        asset.OrientationUncertain = decision.Orientation.Uncertain;
    }

    /// <summary>No AI: orientation comes from file metadata only and is marked "not verified".</summary>
    public static void MarkNotVerified(Asset asset)
    {
        if (asset.Model is null || asset.Model.Orientation.DecidedBy == OrientationDecidedBy.User) return;
        asset.Model = asset.Model with { Orientation = asset.Model.Orientation with { Verified = false } };
        asset.OrientationVerified = false;
    }

    public static bool SameRotation(ModelOrientation a, ModelOrientation b) =>
        Near(OrientationProbe.Rotation(a.RotX, a.RotY, a.RotZ), OrientationProbe.Rotation(b.RotX, b.RotY, b.RotZ));

    /// <summary>
    /// The candidate rotation followed by a turn about the world up axis, expressed again as X-then-Y-then-Z Euler
    /// angles in multiples of 90° (searched over all 64 combinations; every axis-aligned rotation has one).
    /// </summary>
    public static (int X, int Y, int Z) WithYaw(int rotX, int rotY, int rotZ, int yaw)
    {
        if (yaw % 360 == 0) return (rotX, rotY, rotZ);
        var target = OrientationProbe.Rotation(rotX, rotY, rotZ) * Matrix4x4.CreateRotationY(yaw * MathF.PI / 180);
        int[] steps = [0, 90, 180, 270];
        foreach (var x in steps)
        foreach (var y in steps)
        foreach (var z in steps)
            if (Near(OrientationProbe.Rotation(x, y, z), target)) return (x, y, z);
        throw new InvalidOperationException("No axis-aligned rotation matches.");
    }

    private static bool Near(Matrix4x4 a, Matrix4x4 b)
    {
        var d = a - b;
        return MathF.Abs(d.M11) + MathF.Abs(d.M12) + MathF.Abs(d.M13) + MathF.Abs(d.M21) + MathF.Abs(d.M22) + MathF.Abs(d.M23)
               + MathF.Abs(d.M31) + MathF.Abs(d.M32) + MathF.Abs(d.M33) < 1e-3f;
    }
}
