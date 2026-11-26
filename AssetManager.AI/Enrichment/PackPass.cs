using System.Text;
using AssetManager.Core.Domain;
using AssetManager.Processing.Handlers;
using ImageMagick;
using Microsoft.Extensions.AI;

namespace AssetManager.AI.Enrichment;

/// <summary>A file of the source as the pack pass sees it: an asset (with its type) or a noted file (scripts, project files).</summary>
public sealed record PackFile(string RelativePath, FileKind Kind, AssetType Type);

/// <summary>Everything the pack pass sends for one source (design D7).</summary>
public sealed record PackInput(
    string SourceName,
    SourceKind Kind,
    IReadOnlyList<PackFile> Files,
    IReadOnlyList<SourceDocument> Documents,
    IReadOnlyList<AiImage> Samples,
    StoreInfo? Store = null,
    string? Instruction = null,
    IReadOnlyList<string>? SuggestedTags = null);

/// <summary>Defaults the items of a pack inherit (tags with origin AI, style when the item has none).</summary>
public sealed record PackDefaults(IReadOnlyList<string> Tags, IReadOnlyList<string> Style);

/// <summary>
/// One request per source: folder tree summary, sample previews, documents, script manifest and the user's import
/// instruction go in; pack description, license, links, script notes and default tags/style come out.
/// </summary>
public static class PackPass
{
    public const int MaxSamples = 6;
    private const int TreeLines = 40;
    private const int ScriptNames = 40;

    public static List<ChatMessage> BuildPrompt(PackInput input, bool vision)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Summarize this game asset pack.");
        sb.AppendLine($"Pack: \"{input.SourceName}\" ({input.Kind})");
        if (input.Store is { Title: not null } store)
            sb.AppendLine($"Asset Store: \"{store.Title}\" by {store.Publisher ?? "unknown"}, version {store.Version ?? "unknown"}{(store.Url is null ? "" : ", " + store.Url)}");
        if (!string.IsNullOrWhiteSpace(input.Instruction))
            sb.AppendLine($"Instruction from the user (follow it): {input.Instruction.Trim()}");
        sb.AppendLine();
        sb.AppendLine("Contents (folder: asset counts by type):");
        sb.AppendLine(TreeSummary(input.Files));

        var scripts = ScriptManifest(input.Files);
        sb.AppendLine();
        if (scripts.Count == 0) sb.AppendLine("Code/project files: none. Set script_notes to null.");
        else
        {
            sb.AppendLine($"Code/project files (not assets): {scripts.Summary}. Names:");
            sb.AppendLine(string.Join(", ", scripts.Names));
            sb.AppendLine("In script_notes, say in one or two sentences what these scripts appear to do.");
        }

        sb.AppendLine();
        var docs = DocumentExtractor.ToPrompt(input.Documents);
        if (docs.Length == 0) sb.AppendLine("Documents: none. Leave the license kind \"unknown\" and commercial/attribution \"unknown\" unless the Asset Store applies.");
        else
        {
            sb.AppendLine("Documents (the license must come from these; cite the file name in evidence):");
            sb.Append(docs);
        }
        if (input.Store is { StoreId: not null })
            sb.AppendLine("This is a Unity Asset Store package: when no document says otherwise, the license is \"Unity Asset Store EULA\" (commercial use yes, attribution no, evidence null).");

        var samples = vision ? input.Samples.Take(MaxSamples).ToList() : [];
        if (samples.Count > 0) sb.AppendLine($"\n{samples.Count} sample previews follow, each preceded by its file name.");
        sb.AppendLine("\nTags must apply to every item in the pack (theme, setting, genre, purpose), 3-8 tags, not to single items and not file formats or the pack name.");
        if (input.SuggestedTags is { Count: > 0 } suggested) sb.AppendLine(SuggestedTags.PromptLine(suggested));
        sb.AppendLine("Style is the visual or audio style only (e.g. \"low poly\", \"pixel art\", \"stylized\", \"realistic\", \"hand-painted\", \"orchestral\"); leave it empty when you cannot tell.");

        var contents = new List<AIContent> { new TextContent(sb.ToString()) };
        foreach (var s in samples)
        {
            contents.Add(new TextContent($"Sample: {s.Label}"));
            contents.Add(ToModelImage(s));
        }
        return [new ChatMessage(ChatRole.User, contents)];
    }

    public static Task<PackResult> RunAsync(StructuredChat chat, PackInput input, bool vision, CancellationToken ct = default) =>
        chat.AskAsync<PackResult>(BuildPrompt(input, vision), ct: ct);

    /// <summary>Writes the result to the source (respecting user edits) and returns the defaults for its items.</summary>
    public static PackDefaults Apply(Source source, PackInput input, PackResult result)
    {
        if (!source.UserEdited.HasFlag(EditedFields.Description)) source.Description = result.Description.Trim();
        SourceFacts.Apply(source, input.Documents, result);
        if (!source.UserEdited.HasFlag(EditedFields.Notes)) source.Notes = Notes(input.Files, result.ScriptNotes);
        return new PackDefaults(SuggestedTags.Fold(AiText.NormalizeTags(result.Tags), input.SuggestedTags).Take(10).ToList(),
            SuggestedTags.Fold(AiText.NormalizeTags(result.Style), input.SuggestedTags));
    }

    /// <summary>
    /// Source notes: the script count is computed (never left to the model), followed by the model's description of
    /// what the scripts do. Null when the pack has no code.
    /// </summary>
    public static string? Notes(IReadOnlyList<PackFile> files, string? aiNotes)
    {
        var scripts = ScriptManifest(files);
        if (scripts.Count == 0) return null;
        var line = $"Includes {scripts.Count} code/project file{(scripts.Count == 1 ? "" : "s")} ({scripts.Summary}).";
        return string.IsNullOrWhiteSpace(aiNotes) ? line : line + " " + aiNotes.Trim();
    }

    public sealed record Scripts(int Count, string Summary, IReadOnlyList<string> Names);

    /// <summary>Counts of noted files by extension and folder, plus a sample of their names.</summary>
    public static Scripts ScriptManifest(IReadOnlyList<PackFile> files)
    {
        var noted = files.Where(f => f.Kind == FileKind.Noted).ToList();
        if (noted.Count == 0) return new Scripts(0, "", []);
        var byExt = noted.GroupBy(f => Path.GetExtension(f.RelativePath).ToLowerInvariant())
            .OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {(g.Key.Length > 0 ? g.Key : "no extension")}");
        var folders = noted.GroupBy(f => TopFolder(f.RelativePath)).OrderByDescending(g => g.Count()).Take(3)
            .Select(g => g.Key.Length > 0 ? g.Key + "/" : "the root");
        var summary = $"{string.Join(", ", byExt)} in {string.Join(", ", folders)}";
        var names = noted.Select(f => Path.GetFileName(f.RelativePath)).Distinct(StringComparer.OrdinalIgnoreCase).Take(ScriptNames).ToList();
        return new Scripts(noted.Count, summary, names);
    }

    /// <summary>One line per folder (up to two levels deep) with asset counts by type, largest folders first.</summary>
    public static string TreeSummary(IReadOnlyList<PackFile> files)
    {
        var assets = files.Where(f => f.Kind == FileKind.Asset).ToList();
        if (assets.Count == 0) return "(no assets)";
        var lines = assets.GroupBy(f => Folder(f.RelativePath, 2))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{(g.Key.Length > 0 ? g.Key + "/" : "./")}: {Counts(g)}; e.g. {string.Join(", ", g.Take(3).Select(f => Path.GetFileName(f.RelativePath)))}")
            .ToList();
        var sb = new StringBuilder();
        foreach (var l in lines.Take(TreeLines)) sb.AppendLine(l);
        if (lines.Count > TreeLines) sb.AppendLine($"... and {lines.Count - TreeLines} more folders");
        sb.Append($"Total: {assets.Count} assets ({Counts(assets)})");
        return sb.ToString();
    }

    /// <summary>llama.cpp decodes PNG/JPEG but not WebP, which most previews are; re-encode for the model.</summary>
    public static DataContent ToModelImage(AiImage image)
    {
        if (image.Mime is "image/png" or "image/jpeg") return new DataContent(image.Bytes, image.Mime);
        using var img = new MagickImage(image.Bytes);
        if (img.HasAlpha)
        {
            img.BackgroundColor = new MagickColor(128, 128, 128);
            img.Alpha(AlphaAction.Remove);
        }
        img.Quality = 90;
        return new DataContent(img.ToByteArray(MagickFormat.Jpeg), "image/jpeg");
    }

    private static string Counts(IEnumerable<PackFile> files) =>
        string.Join(", ", files.GroupBy(f => f.Type).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"));

    private static string Folder(string relativePath, int depth)
    {
        var parts = relativePath.Replace('\\', '/').Split('/');
        return string.Join('/', parts.Take(Math.Min(depth, parts.Length - 1)));
    }

    private static string TopFolder(string relativePath) => Folder(relativePath, 1);
}
