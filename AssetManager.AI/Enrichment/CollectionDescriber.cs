using System.Text;
using AssetManager.Core.Domain;
using AssetManager.Data;
using AssetManager.Data.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace AssetManager.AI.Enrichment;

/// <summary>A member as the sampler sees it.</summary>
public sealed record CollectionMember(long Id, string Name, AssetType Type, long SourceId, string? Description);

/// <summary>
/// Generates a collection's English description from a representative sample of its members: the counts by type,
/// the most common tags, up to <see cref="MaxSample"/> item summaries spread over types and sources, and (with vision)
/// a contact sheet of thumbnails from that sample.
/// </summary>
public sealed class CollectionDescriber(CatalogStore catalog)
{
    public const int MaxSample = 40;
    public const int MaxImages = 8;

    /// <summary>
    /// Round-robin over (type, source) groups, largest groups first, so small groups are represented too.
    /// Within a group, described assets come first, then by id. Deterministic.
    /// </summary>
    public static IReadOnlyList<CollectionMember> PickSample(IReadOnlyList<CollectionMember> members, int max = MaxSample)
    {
        var queues = members.GroupBy(m => (m.Type, m.SourceId))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Type).ThenBy(g => g.Key.SourceId)
            .Select(g => new Queue<CollectionMember>(g.OrderBy(m => string.IsNullOrWhiteSpace(m.Description)).ThenBy(m => m.Id)))
            .ToList();
        var picked = new List<CollectionMember>();
        while (picked.Count < max && queues.Any(q => q.Count > 0))
            foreach (var q in queues.Where(q => q.Count > 0))
            {
                picked.Add(q.Dequeue());
                if (picked.Count == max) break;
            }
        return picked;
    }

    public async Task<List<ChatMessage>> BuildPromptAsync(Collection collection, bool vision, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var members = await db.Assets.AsNoTracking().InCollection(collection)
            .Select(a => new CollectionMember(a.Id, a.Name, a.Type, a.SourceId, a.Description)).ToListAsync(ct);
        if (members.Count == 0) throw new InvalidOperationException("The collection has no assets to describe.");

        var sample = PickSample(members);
        var ids = sample.Select(s => s.Id).ToList();
        var allIds = members.Select(m => m.Id).ToList();
        var topTags = await db.AssetTags.Where(t => allIds.Contains(t.AssetId)).GroupBy(t => t.Tag.Name)
            .Select(g => new { Name = g.Key, Count = g.Count() }).OrderByDescending(g => g.Count).ThenBy(g => g.Name).Take(25).ToListAsync(ct);
        var sampleTags = (await db.AssetTags.Where(t => ids.Contains(t.AssetId)).Select(t => new { t.AssetId, t.Tag.Name }).ToListAsync(ct))
            .ToLookup(t => t.AssetId, t => t.Name);
        var sources = await db.Sources.Where(s => members.Select(m => m.SourceId).Distinct().Contains(s.Id)).CountAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine($"Write a description of the asset collection \"{collection.Name}\" that summarizes its content and style.");
        if (collection.IncludeTags.Count > 0) sb.AppendLine($"It contains assets tagged {string.Join(" + ", collection.IncludeTags)}{(collection.ExcludeTags.Count > 0 ? $", except {string.Join(", ", collection.ExcludeTags)}" : "")}.");
        sb.AppendLine($"It has {members.Count} assets from {sources} source{(sources == 1 ? "" : "s")}: " +
                      string.Join(", ", members.GroupBy(m => m.Type).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}")) + ".");
        if (topTags.Count > 0) sb.AppendLine($"Most common tags: {string.Join(", ", topTags.Select(t => $"{t.Name} ({t.Count})"))}.");
        sb.AppendLine($"A representative sample of {sample.Count} assets:");
        foreach (var m in sample)
        {
            var tags = sampleTags[m.Id].ToList();
            sb.AppendLine($"- {m.Name} ({m.Type}){(string.IsNullOrWhiteSpace(m.Description) ? "" : ": " + m.Description.Trim())}{(tags.Count > 0 ? $" [{string.Join(", ", tags)}]" : "")}");
        }

        var contents = new List<AIContent>();
        if (vision)
        {
            var thumbIds = sample.Take(MaxImages * 2).Select(s => s.Id).ToList();
            var thumbs = await db.Previews.AsNoTracking()
                .Where(p => thumbIds.Contains(p.AssetId) && (p.Kind == PreviewKind.Thumbnail || p.Kind == PreviewKind.UnityPreview) && p.Mime.StartsWith("image/"))
                .ToListAsync(ct);
            var images = thumbIds.Select(id => thumbs.FirstOrDefault(t => t.AssetId == id && t.Kind == PreviewKind.Thumbnail) ?? thumbs.FirstOrDefault(t => t.AssetId == id))
                .OfType<PreviewBlob>().Take(MaxImages).ToList();
            if (images.Count > 0)
            {
                sb.AppendLine($"The image shows thumbnails of {images.Count} of the sampled assets.");
                contents.Add(new TextContent(sb.ToString()));
                contents.Add(new DataContent(ItemPass.ContactSheet(images.Select(i => ("", i.Bytes)).ToList(), labels: false), "image/jpeg"));
                return [new ChatMessage(ChatRole.User, contents)];
            }
        }
        contents.Add(new TextContent(sb.ToString()));
        return [new ChatMessage(ChatRole.User, contents)];
    }

    /// <summary>Generates the description and saves it with origin AI; returns it.</summary>
    public async Task<string> DescribeAsync(StructuredChat chat, long collectionId, bool vision, CancellationToken ct = default)
    {
        Collection collection;
        await using (var db = catalog.CreateDbContext())
            collection = await db.Collections.AsNoTracking().FirstAsync(c => c.Id == collectionId, ct);
        var result = await chat.AskAsync<CollectionDescriptionResult>(await BuildPromptAsync(collection, vision, ct), ct: ct);
        var description = result.Description.Trim();
        await using (var db = catalog.CreateDbContext())
            await db.Collections.Where(c => c.Id == collectionId).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Description, description)
                .SetProperty(c => c.DescriptionOrigin, TextOrigin.AI), ct);
        catalog.NotifyContentChanged();
        return description;
    }
}
