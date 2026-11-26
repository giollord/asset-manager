using AssetManager.AI.Enrichment;
using AssetManager.AI.Runtime;
using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Data;
using AssetManager.Data.Services;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Locations;
using AssetManager.Processing.Settings;
using AssetManager.Processing.Sprites;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetManager.AI.Import;

/// <summary>
/// The AI stages of an import (design D7/D8): the pack pass once per source, then the item pass for every asset
/// still pending. Results are written as they arrive, so the import preview fills in live; a failed request marks
/// only its items failed. All requests go through <see cref="AiQueue.Chat"/>, so pausing the queue pauses imports.
/// </summary>
public sealed class ImportEnricher(
    CatalogStore catalog,
    AiQueue queue,
    AiRuntime runtime,
    ILocationResolver resolver,
    HandlerRegistry handlers,
    ClassificationSettings classification,
    PreviewSettings previewSettings,
    IPreviewRerenderer rerenderer,
    ILogger<ImportEnricher>? logger = null,
    ITextureMatcher? textures = null)
{
    private readonly ILogger _log = logger ?? (ILogger)NullLogger.Instance;

    /// <summary>The chat client to use; null when no model is ready. Tests replace it.</summary>
    public Func<Microsoft.Extensions.AI.IChatClient?> Chat { get; init; } = () => queue.Chat;

    /// <summary>How many AI requests to run at once: the chat server's slots. Tests replace it.</summary>
    public Func<int> Parallelism { get; init; } = () => runtime.Chat.Parallel;

    /// <summary>Input modalities of the running model. Tests replace it.</summary>
    public Func<AiModalities> Modalities { get; init; } = () => runtime.Chat.Modalities;

    /// <summary>The app settings (consensus thresholds). Tests replace it.</summary>
    public Func<Core.Settings.AppSettings> Settings { get; init; } = () => runtime.Settings;

    public async Task PackPassAsync(long sessionId, IJobProgress progress, int stage, int stageCount, CancellationToken ct)
    {
        var session = await SessionAsync(sessionId, ct);
        var sourceIds = await SourceIdsAsync(sessionId, ct);
        progress.SetStage("AI pack pass", stage, stageCount, sourceIds.Count);
        if (session.AiDepth != AiDepth.None && session.StagedThrough < stage + 1) await WaitForModelAsync(progress, ct);
        if (session.AiDepth == AiDepth.None || session.StagedThrough >= stage + 1 || Chat() is not { } client)
        {
            // Only when this pass actually needed the model: a resumed job skips a finished pass without waiting for it.
            if (session.AiDepth != AiDepth.None && session.StagedThrough < stage + 1 && Chat() is null) await NotEnrichedAsync(sessionId, progress, ct);
            progress.Advance(sourceIds.Count);
            await MarkStageAsync(sessionId, stage + 1, ct);
            return;
        }
        var chat = new StructuredChat(client, _log);
        var classifier = new FileClassifier(await classification.GetAsync(ct));

        // Sources are picked one at a time: a confirm during this pass can replace a staged source with an existing one
        // (new version), which then needs the pass instead.
        var attempted = new HashSet<long>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var current = await SourceIdsAsync(sessionId, ct);
            if (current.FirstOrDefault(id => !attempted.Contains(id)) is not (var sourceId and not 0)) break;
            attempted.Add(sourceId);
            if (attempted.Count > sourceIds.Count) progress.SetTotal(attempted.Count);
            await using var db = catalog.CreateDbContext();
            var source = await db.AllSources.FirstOrDefaultAsync(s => s.Id == sourceId, ct);
            if (source is null) { progress.Advance(1); continue; }
            if (source.Description is not null) { progress.Advance(1, source.DisplayName); continue; } // done before a restart
            var assets = await db.AllAssets.Include(a => a.Files).Where(a => a.SourceId == sourceId).ToListAsync(ct);
            var skips = await db.StagedSkips.AsNoTracking().Where(s => s.SourceId == sourceId).ToListAsync(ct);
            if (assets.Count == 0 && !skips.Any(s => s.Kind is SkipKind.Noted or SkipKind.Document)) { progress.Advance(1); continue; }
            progress.SetCurrentItem(source.DisplayName);
            try
            {
                var input = await PackInputAsync(db, source, assets, skips, classifier, session.Instruction, ct);
                var result = await PackPass.RunAsync(chat, input, Modalities().Vision, ct);
                var defaults = PackPass.Apply(source, input, result);
                var tags = await TagService.GetOrCreateAsync(db, defaults.Tags, ct);
                foreach (var asset in assets)
                {
                    // Pack defaults are inherited at import (origin Import), so the item pass's own AI tags add to them.
                    foreach (var tag in tags.Where(t => !asset.Tags.Any(x => x.TagId == t.Id && t.Id != 0) && !asset.Tags.Any(x => ReferenceEquals(x.Tag, t))))
                        asset.Tags.Add(new AssetTag { Tag = tag, Origin = TagOrigin.Import });
                    if (asset.Style.Count == 0 && !asset.UserEdited.HasFlag(EditedFields.Style)) asset.Style = [.. defaults.Style];
                    if (session.AiDepth == AiDepth.PackOnly && asset.AiStatus == AiStatus.Pending)
                    {
                        asset.AiStatus = AiStatus.Enriched;
                        asset.AiInputMode = AiInputMode.None;
                    }
                }
                await SaveSurvivingAsync(db, ct);
            }
            catch (AiOutputException e)
            {
                progress.Fail($"source:{sourceId}", source.DisplayName, e.Message);
            }
            progress.Advance(1, source.DisplayName);
        }
        await MarkStageAsync(sessionId, stage + 1, ct);
        catalog.NotifyContentChanged();
    }

    public async Task ItemPassAsync(long sessionId, IJobProgress progress, int stage, int stageCount, CancellationToken ct)
    {
        var session = await SessionAsync(sessionId, ct);
        var sourceIds = await SourceIdsAsync(sessionId, ct);
        List<long> pending;
        await using (var db = catalog.CreateDbContext())
            pending = await db.AllAssets.Where(a => sourceIds.Contains(a.SourceId) && a.AiStatus == AiStatus.Pending).OrderBy(a => a.SourceId).ThenBy(a => a.Id)
                .Select(a => a.Id).ToListAsync(ct);
        progress.SetStage("AI item pass", stage, stageCount, pending.Count);
        if (session.AiDepth == AiDepth.Full && pending.Count > 0) await WaitForModelAsync(progress, ct);
        if (session.AiDepth != AiDepth.Full || pending.Count == 0 || Chat() is not { } client)
        {
            if (pending.Count > 0 && session.AiDepth == AiDepth.Full && Chat() is null) await NotEnrichedAsync(sessionId, progress, ct);
            progress.Advance(pending.Count);
            await MarkStageAsync(sessionId, stage + 1, ct);
            return;
        }
        var chat = new StructuredChat(client, _log);
        var attempted = new HashSet<long>();
        while (pending.Count > 0)
        {
            attempted.UnionWith(pending);
            await EnrichAssetsAsync(chat, pending, session.Instruction, null, progress, ct);
            // A confirm during this pass can turn cataloged assets of a replaced version pending again.
            sourceIds = await SourceIdsAsync(sessionId, ct);
            await using var db = catalog.CreateDbContext();
            pending = (await db.AllAssets.Where(a => sourceIds.Contains(a.SourceId) && a.AiStatus == AiStatus.Pending).OrderBy(a => a.Id)
                .Select(a => a.Id).ToListAsync(ct)).Where(id => !attempted.Contains(id)).ToList();
            if (pending.Count > 0) progress.SetTotal(attempted.Count + pending.Count);
        }
        await ConsensusAsync(chat, await SourceIdsAsync(sessionId, ct), progress, ct);
        await MarkStageAsync(sessionId, stage + 1, ct);
    }

    /// <summary>
    /// The tag consensus step (design D2) for each source: shared tags and styles the AI accepts become source tags.
    /// A failed request only leaves a warning; the assets keep their own tags.
    /// </summary>
    public async Task ConsensusAsync(StructuredChat chat, IReadOnlyList<long> sourceIds, IJobProgress progress, CancellationToken ct)
    {
        var settings = Settings();
        if (!settings.ConsensusEnabled) return;
        foreach (var sourceId in sourceIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var accepted = await ConsensusPass.RunForSourceAsync(catalog, chat, sourceId, settings, Modalities().Vision, ct);
                if (accepted.Count > 0)
                {
                    _log.LogInformation("Source {Source}: consensus added source tags {Tags}", sourceId, string.Join(", ", accepted));
                    catalog.NotifyContentChanged();
                }
            }
            catch (Exception e) when (e is AiOutputException or HttpRequestException or System.ClientModel.ClientResultException or InvalidOperationException)
            {
                _log.LogWarning("Tag consensus for source {Source} failed: {Error}", sourceId, e.Message);
                progress.Warn($"consensus:{sourceId}", null, $"Source tags could not be suggested: {e.Message}");
            }
        }
    }

    /// <summary>"Suggest source tags" on the Sources page: the consensus step for the given sources.</summary>
    public async Task ConsensusForSourcesAsync(IReadOnlyList<long> sourceIds, IJobProgress progress, CancellationToken ct)
    {
        progress.SetStage("Suggesting source tags", 0, 1, sourceIds.Count);
        await WaitForModelAsync(progress, ct);
        if (Chat() is not { } client) throw new InvalidOperationException("No AI model is installed: source tags cannot be suggested.");
        var chat = new StructuredChat(client, _log);
        var settings = Settings();
        foreach (var sourceId in sourceIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Asked for explicitly: runs even when the automatic step is turned off.
                var accepted = await ConsensusPass.RunForSourceAsync(catalog, chat, sourceId, settings, Modalities().Vision, ct, force: true);
                if (accepted.Count > 0) catalog.NotifyContentChanged();
                progress.Advance(1, accepted.Count > 0 ? $"added {string.Join(", ", accepted)}" : null);
            }
            catch (AiOutputException e)
            {
                progress.Fail($"source:{sourceId}", null, e.Message);
                progress.Advance(1);
            }
        }
    }

    /// <summary>
    /// Runs the item pass for the given assets, grouped by source. Also used for AI corrections on a selection, where
    /// <paramref name="correction"/> is added to the instruction.
    /// </summary>
    public async Task EnrichAssetsAsync(StructuredChat chat, IReadOnlyList<long> assetIds, string? instruction, string? correction,
        IJobProgress progress, CancellationToken ct)
    {
        var options = await previewSettings.GetAsync(ct);
        var modalities = Modalities();
        var bySource = new List<(long SourceId, List<long> Ids)>();
        await using (var db = catalog.CreateDbContext())
        {
            var rows = await db.AllAssets.Where(a => assetIds.Contains(a.Id)).Select(a => new { a.Id, a.SourceId, a.Type }).ToListAsync(ct);
            bySource = rows.GroupBy(r => r.SourceId).Select(g => (g.Key, g.OrderBy(r => r.Type == AssetType.Model3D).ThenBy(r => r.Id).Select(r => r.Id).ToList())).ToList();
        }
        var fullInstruction = string.Join(" ", new[] { instruction, correction is null ? null : $"Correction from the user for these items: {correction}" }.Where(s => s is not null));
        var suggested = await new CatalogSettingsService(catalog).GetSuggestedTagsAsync(ct);

        // Work units: chunks of one source. Up to Parallelism() of them talk to the model at once (the server has that
        // many slots); applying and saving results is serialized, so new tags are not created twice.
        var units = new List<(long SourceId, ItemContext Context, long[] Chunk)>();
        var remaining = new Dictionary<long, int>();
        foreach (var (sourceId, ids) in bySource)
        {
            await using var db = catalog.CreateDbContext();
            var source = await db.AllSources.AsNoTracking().FirstAsync(s => s.Id == sourceId, ct);
            var context = new ItemContext(source.DisplayName, source.Description, fullInstruction.Length > 0 ? fullInstruction : null, suggested);
            foreach (var chunk in ids.Chunk(ItemPass.SheetSize)) units.Add((sourceId, context, chunk));
            remaining[sourceId] = (ids.Count + ItemPass.SheetSize - 1) / ItemPass.SheetSize;
        }
        using var save = new SemaphoreSlim(1, 1);
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Parallelism()), CancellationToken = ct };
        await Parallel.ForEachAsync(units, parallel, async (unit, ct) =>
        {
            var (sourceId, context, chunk) = unit;
            await using var db = catalog.CreateDbContext();
            var assets = await db.AllAssets.Include(a => a.Files).Include(a => a.Tags).ThenInclude(t => t.Tag).Include(a => a.Source)
                .Where(a => chunk.Contains(a.Id)).ToListAsync(ct);
            var previews = (await db.Previews.AsNoTracking().Where(p => chunk.Contains(p.AssetId)).ToListAsync(ct)).ToLookup(p => p.AssetId);
            var inputs = assets.OrderBy(a => Array.IndexOf(chunk, a.Id))
                .Select(a => new ItemInput(a, handlers.For(a.Type).BuildAiInput(a, previews[a.Id].ToList()).Text, previews[a.Id].ToList())).ToList();
            progress.SetCurrentItem(string.Join(", ", inputs.Take(3).Select(i => i.Asset.Name)) + (inputs.Count > 3 ? ", ..." : ""));

            var requests = new List<(IReadOnlyList<ItemInput> Items, Func<Task<IReadOnlyList<ItemOutcome>>> Run)>();
            foreach (var model in inputs.Where(i => i.Asset.Type == AssetType.Model3D))
                requests.Add(([model], async () =>
                {
                    var r = await ModelEnrichment.EnrichAsync(chat, model, context, modalities, options, rerenderer, ct: ct,
                        textures: textures, textureOptions: TextureMatchOptions.From(Settings()));
                    foreach (var t in r.Textures)
                        _log.LogInformation("Texture for {Asset} / {Material}: {Choice} ({Confidence:0.00}) {Outcome}: {Reason}", model.Asset.Name, t.Material,
                            t.Chosen?.RelativePath ?? "none", t.Confidence, t.Attached ? "attached" : t.Suggested ? "suggested" : "not used", t.Reason);
                    return [r.Item];
                }));
            foreach (var request in ItemPass.Plan(inputs.Where(i => i.Asset.Type != AssetType.Model3D).ToList(), modalities))
                requests.Add((request.Items.Select(i => i.Input).ToList(), () => ItemPass.RunAsync(chat, request, context, options, ct)));

            foreach (var (items, run) in requests)
            {
                IReadOnlyList<ItemOutcome>? outcomes = null;
                Exception? error = null;
                try
                {
                    outcomes = await run();
                    // The AI thinks it is a sprite sheet: let the detector check, with the AI's counts as a candidate (design D3).
                    foreach (var o in outcomes)
                        if (ItemPass.SpriteHint(o.Result) is { } hint)
                            await SpriteSheetClassifier.RedetectWithHintAsync(resolver, o.Asset, hint, options, ct);
                }
                catch (Exception e) when (e is AiOutputException or HttpRequestException or System.ClientModel.ClientResultException
                                               or InvalidOperationException or IOException or AggregateException)
                {
                    error = e;
                }
                await save.WaitAsync(ct);
                try
                {
                    if (outcomes is not null)
                        foreach (var outcome in outcomes)
                        {
                            ItemPass.Apply(outcome);
                            await TagService.ReplaceOriginTagsAsync(db, outcome.Asset, outcome.Tags, TagOrigin.AI, ct);
                            queue.RecordItem(true);
                        }
                    else
                    {
                        // One item's failure (bad AI output, a failed re-render) fails only that item, not the import.
                        if (error is InvalidOperationException or IOException) _log.LogError(error, "AI item failed");
                        else _log.LogWarning("AI item request failed: {Error}", error!.Message);
                        ItemPass.MarkFailed(items.Select(i => i.Asset), error!.Message);
                        foreach (var i in items)
                        {
                            progress.Fail(i.Asset.Id.ToString(), i.Asset.Name, error.Message);
                            queue.RecordItem(false);
                        }
                    }
                    await SaveSurvivingAsync(db, ct);
                }
                finally
                {
                    save.Release();
                }
                progress.Advance(items.Count);
            }
            bool sourceDone;
            lock (remaining) sourceDone = --remaining[sourceId] == 0;
            if (sourceDone)
            {
                // Replacing the AI tags may have dropped an AI-origin row of a tag that is also a source tag (design D1).
                await save.WaitAsync(ct);
                try
                {
                    await using var apply = catalog.CreateDbContext();
                    await SourceTagService.ApplyAsync(apply, sourceId, ct);
                }
                finally
                {
                    save.Release();
                }
                catalog.NotifyContentChanged();
            }
        });
    }

    private async Task<PackInput> PackInputAsync(CatalogDbContext db, Source source, List<Asset> assets, List<StagedSkip> skips,
        FileClassifier classifier, string? instruction, CancellationToken ct)
    {
        var files = assets.Where(a => a.PrimaryFile is not null)
            .Select(a => new PackFile(a.PrimaryFile!.RelativePath, FileKind.Asset, a.Type))
            .Concat(skips.Where(s => s.Kind == SkipKind.Noted).Select(s => new PackFile(s.Path, FileKind.Noted, AssetType.Other)))
            .ToList();

        var docs = new List<SourceDocument>();
        foreach (var skip in skips.Where(s => s.Kind == SkipKind.Document && s.Location is not null).Take(12))
        {
            try
            {
                using var file = await resolver.ResolveAsync(skip.Location!, ct);
                docs.Add(DocumentExtractor.Extract(file.Path, skip.Path));
            }
            catch (Exception e) when (e is OriginalUnavailableException or IOException)
            {
                _log.LogDebug("Document {Path} unavailable: {Error}", skip.Path, e.Message);
            }
        }
        // Documents next to an archive or package on disk (pack.zip + license.txt).
        if (source.Kind != SourceKind.Folder && !source.Location.IsInContainer && resolver.GetOuterPath(source.Location) is { } outer)
            docs.AddRange(DocumentExtractor.FindNear(outer, classifier).Where(d => docs.All(x => x.Name != d.Name)));

        var ids = assets.Select(a => a.Id).ToList();
        var thumbs = await db.Previews.AsNoTracking()
            .Where(p => ids.Contains(p.AssetId) && (p.Kind == PreviewKind.Thumbnail || p.Kind == PreviewKind.UnityPreview) && p.Mime.StartsWith("image/"))
            .Select(p => new { p.AssetId, p.Kind, p.Mime, p.Bytes }).ToListAsync(ct);
        var samples = new List<AiImage>();
        var folders = new HashSet<string>();
        foreach (var asset in assets.OrderBy(a => a.Id))
        {
            if (samples.Count >= PackPass.MaxSamples) break;
            var folder = Path.GetDirectoryName(asset.PrimaryFile?.RelativePath ?? "") ?? "";
            if (!folders.Add(folder)) continue;
            var thumb = thumbs.FirstOrDefault(t => t.AssetId == asset.Id && t.Kind == PreviewKind.Thumbnail) ?? thumbs.FirstOrDefault(t => t.AssetId == asset.Id);
            if (thumb is not null) samples.Add(new AiImage(thumb.Mime, thumb.Bytes, asset.PrimaryFile?.RelativePath ?? asset.Name));
        }
        var suggested = await new CatalogSettingsService(catalog).GetSuggestedTagsAsync(ct);
        return new PackInput(source.DisplayName, source.Kind, files, docs, samples, source.Store.StoreId is null ? null : source.Store, instruction, suggested);
    }

    /// <summary>How long the AI stages wait for a configured model that is still loading.</summary>
    public TimeSpan ModelStartTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Waits while a configured chat model is not up yet: jobs resumed at app start run before the AI runtime starts,
    /// and must not take "no chat client" for "no model installed". Returns at once without a configured model.
    /// </summary>
    public async Task WaitForModelAsync(IJobProgress progress, CancellationToken ct)
    {
        if (Chat() is not null || !runtime.ChatConfigured) return;
        progress.SetCurrentItem("Waiting for the AI model to start...");
        var deadline = DateTime.UtcNow + ModelStartTimeout;
        while (Chat() is null && runtime.ChatConfigured && !runtime.ChatFailed && DateTime.UtcNow < deadline)
            await Task.Delay(250, ct);
    }

    /// <summary>
    /// Saves AI results, leaving out rows a confirm removed meanwhile: a new package version replaces staged assets
    /// and sources with the existing ones, which the passes then pick up as pending.
    /// </summary>
    private async Task SaveSurvivingAsync(CatalogDbContext db, CancellationToken ct)
    {
        var assetIds = db.ChangeTracker.Entries<Asset>().Where(e => e.State != EntityState.Added).Select(e => e.Entity.Id).ToList();
        var sourceIds = db.ChangeTracker.Entries<Source>().Where(e => e.State != EntityState.Added).Select(e => e.Entity.Id).ToList();
        var aliveAssets = (await db.AllAssets.Where(a => assetIds.Contains(a.Id)).Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        var aliveSources = (await db.AllSources.Where(s => sourceIds.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct)).ToHashSet();
        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            var gone = entry.Entity switch
            {
                Asset a => entry.State != EntityState.Added && !aliveAssets.Contains(a.Id),
                AssetTag t => assetIds.Contains(t.AssetId) && !aliveAssets.Contains(t.AssetId),
                Source src => entry.State != EntityState.Added && !aliveSources.Contains(src.Id),
                _ => false,
            };
            if (gone)
                entry.State = EntityState.Detached;
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException e)
        {
            _log.LogInformation("AI results for rows removed by a confirm were dropped: {Error}", e.Message);
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Enriches a selection outside an import (re-import with re-enrichment, AI corrections): the "ai.enrich" job.
    /// Without a model the pending assets become "not enriched" with one warning.
    /// </summary>
    public async Task EnrichSelectionAsync(IReadOnlyList<long> assetIds, string? instruction, string? correction, IJobProgress progress, CancellationToken ct)
    {
        progress.SetStage("AI item pass", 0, 1, assetIds.Count);
        await WaitForModelAsync(progress, ct);
        if (Chat() is not { } client)
        {
            await using var db = catalog.CreateDbContext();
            var n = 0;
            foreach (var chunk in assetIds.Chunk(500))
                n += await db.AllAssets.Where(a => chunk.Contains(a.Id) && a.AiStatus == AiStatus.Pending)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.AiStatus, AiStatus.NotEnriched), ct);
            if (n > 0) progress.Warn("ai", null, "No AI model is installed: assets are cataloged without AI enrichment (\"not enriched\").");
            progress.Advance(assetIds.Count);
            return;
        }
        var chat = new StructuredChat(client, _log);
        await EnrichAssetsAsync(chat, assetIds, instruction, correction, progress, ct);
        await ConsensusAsync(chat, await WholeSourcesAsync(assetIds, ct), progress, ct);
    }

    /// <summary>The sources all of whose assets are in <paramref name="assetIds"/> (re-enriching a whole source runs the consensus step).</summary>
    private async Task<List<long>> WholeSourcesAsync(IReadOnlyList<long> assetIds, CancellationToken ct)
    {
        await using var db = catalog.CreateDbContext();
        var selected = new Dictionary<long, int>();
        foreach (var chunk in assetIds.Distinct().Chunk(500))
            foreach (var g in await db.AllAssets.Where(a => chunk.Contains(a.Id)).GroupBy(a => a.SourceId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
                selected[g.Key] = selected.GetValueOrDefault(g.Key) + g.Count;
        var whole = new List<long>();
        foreach (var (sourceId, count) in selected)
            if (await db.AllAssets.CountAsync(a => a.SourceId == sourceId && a.Included, ct) == count) whole.Add(sourceId);
        return whole;
    }

    private async Task NotEnrichedAsync(long sessionId, IJobProgress progress, CancellationToken ct)
    {
        var sourceIds = await SourceIdsAsync(sessionId, ct);
        await using var db = catalog.CreateDbContext();
        var n = await db.AllAssets.Where(a => sourceIds.Contains(a.SourceId) && a.AiStatus == AiStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.AiStatus, AiStatus.NotEnriched), ct);
        if (n > 0) progress.Warn("ai", null, "No AI model is installed: assets are cataloged without AI enrichment (\"not enriched\").");
    }

    private async Task<ImportSession> SessionAsync(long sessionId, CancellationToken ct)
    {
        await using var db = catalog.CreateDbContext();
        return await db.ImportSessions.AsNoTracking().FirstAsync(s => s.Id == sessionId, ct);
    }

    /// <summary>The session's sources: staged ones, or (after confirm) the committed sources it created.</summary>
    private async Task<List<long>> SourceIdsAsync(long sessionId, CancellationToken ct)
    {
        await using var db = catalog.CreateDbContext();
        return await db.AllSources.Where(s => s.ImportSessionId == sessionId || s.CommittedFromSessionId == sessionId).OrderBy(s => s.Id).Select(s => s.Id).ToListAsync(ct);
    }

    private async Task MarkStageAsync(long sessionId, int through, CancellationToken ct)
    {
        await using var db = catalog.CreateDbContext();
        await db.ImportSessions.Where(s => s.Id == sessionId && s.StagedThrough < through).ExecuteUpdateAsync(u => u.SetProperty(s => s.StagedThrough, through), ct);
    }
}
