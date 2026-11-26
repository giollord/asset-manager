using AssetManager.AI.Enrichment;
using AssetManager.Core.Domain;
using AssetManager.AI.Import;
using AssetManager.AI.Runtime;
using AssetManager.Core.Jobs;
using AssetManager.Core.Settings;
using AssetManager.Data;
using AssetManager.Data.Jobs;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Import;
using AssetManager.Processing.Locations;
using AssetManager.Processing.Settings;
using AssetManager.Tests.Data;
using AssetManager.Tests.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace AssetManager.Tests.Import;

/// <summary>The whole import stack over a temp catalog: stager, enricher (with a replaceable chat client) and job queue.</summary>
public sealed class ImportKit : IAsyncDisposable
{
    private ImportKit(TestCatalog catalog) => Catalog = catalog;

    public TestCatalog Catalog { get; }
    public CatalogStore Store => Catalog.Store;
    public ProcessingKit Processing { get; private set; } = null!;
    public ImportStager Stager { get; private set; } = null!;
    public ImportEnricher Enricher { get; private set; } = null!;
    public ImportService Imports { get; private set; } = null!;
    public JobQueue Jobs { get; private set; } = null!;
    public ProgressHub Hub { get; } = new();
    public AiQueue Ai { get; private set; } = null!;
    public PreviewService Previews { get; private set; } = null!;
    public SourceVersions Versions { get; private set; } = null!;
    public ReimportService Reimports { get; private set; } = null!;
    public ImportReviewService Review { get; private set; } = null!;
    public AssetManager.Processing.Sync.RescanService Rescan { get; private set; } = null!;

    /// <summary>The app settings file the library scan reads (library folders, startup check).</summary>
    public IAppSettingsStore SettingsStore { get; private set; } = null!;

    /// <summary>The app settings the enricher reads (consensus thresholds); tests may change them.</summary>
    public AppSettings AppSettings { get; } = new();
    private AiRuntime _runtime = null!;

    /// <param name="chat">The AI; null means no model is installed.</param>
    /// <param name="chatAvailable">When set, the AI counts as configured in the settings and the chat client exists only once this returns true (a model still loading).</param>
    /// <param name="parallelism">AI requests the item pass runs at once (the server's slots).</param>
    public static async Task<ImportKit> CreateAsync(IChatClient? chat = null, AiModalities? modalities = null, Func<bool>? chatAvailable = null, int parallelism = 1)
    {
        var kit = new ImportKit(await TestCatalog.CreateAsync());
        var store = kit.Store;
        kit.Processing = new ProcessingKit(store);
        var registry = new HandlerRegistry([new ImageHandler(), new AudioHandler(), new FallbackHandler()]);
        var previewSettings = new PreviewSettings(store);
        kit.Previews = new PreviewService(store, kit.Processing.Resolver, new AssetProcessor(registry), previewSettings);
        var classification = new ClassificationSettings(store);
        kit.Versions = new SourceVersions(store);
        kit.Stager = new ImportStager(store, kit.Processing.Vfs, new VolumeRegistry(kit.Processing.Volumes, store), classification,
            new CompanionResolver(), kit.Previews, previewSettings, new DuplicateDetector(store, kit.Processing.Resolver), kit.Versions) { Parallelism = 2 };
        var settings = new JsonAppSettingsStore(Path.Combine(kit.Catalog.Dir.Path, "settings.json"));
        if (chatAvailable is not null) settings.Update(s => s.ChatModelPath = Path.Combine(kit.Catalog.Dir.Path, "model.gguf"));
        kit.SettingsStore = settings;
        kit._runtime = new AiRuntime(settings);
        var late = new LateQueue();
        kit.Ai = new AiQueue(store, kit._runtime, late, kit.Hub);
        kit.Enricher = new ImportEnricher(store, kit.Ai, kit._runtime, kit.Processing.Resolver, registry, classification, previewSettings,
            new CatalogPreviewRerenderer(store, kit.Previews))
        {
            Chat = () => chat is null || chatAvailable?.Invoke() == false ? null : new GatedChatClient(chat, kit.Ai),
            LoadModel = _ => Task.CompletedTask, // the fake model "loads" by itself (chatAvailable)
            Modalities = () => modalities ?? new AiModalities(true, false),
            Parallelism = () => parallelism,
            Settings = () => kit.AppSettings,
        };
        kit.Jobs = new JobQueue(store, [new ImportJob(store, kit.Stager, kit.Enricher), new ImportCommitJob(store, late, kit.Versions),
            new ReimportJob(store, kit.Processing.Resolver, kit.Previews, previewSettings, late), new EnrichAssetsJob(kit.Enricher),
            new ConsensusJob(kit.Enricher), new AssetManager.Processing.Jobs.RedetectSpritesJob(kit.Previews, store),
            new AssetManager.Processing.Sync.RescanJob(kit.Rescan = new AssetManager.Processing.Sync.RescanService(store, kit.Processing.Resolver, classification, kit.Processing.Readers)),
            new AssetManager.Processing.Sync.LibraryScanJob(new AssetManager.Processing.Sync.LibraryScanService(store, kit.Processing.Resolver, settings,
                classification, kit.Processing.Readers, kit.Versions))], kit.Hub);
        late.Target = kit.Jobs;
        await kit.Jobs.StartAsync();
        kit.Imports = new ImportService(store, kit.Stager, kit.Jobs);
        kit.Reimports = new ReimportService(kit.Jobs);
        kit.Review = new ImportReviewService(store, kit.Jobs, new AssetManager.Data.Services.MetadataEditService(store),
            new AssetManager.Data.Services.TagService(store), new StagedRegrouping(store, classification, kit.Previews, previewSettings));
        return kit;
    }

    /// <summary>Starts an import and waits until its job has finished.</summary>
    public async Task<(long SessionId, JobSnapshot Job)> ImportAsync(ImportRequest request)
    {
        var session = await Imports.StartAsync(request, TestContext.Current.CancellationToken);
        var job = await Jobs.WaitAsync(session.JobId!.Value, TestContext.Current.CancellationToken);
        return (session.Id, job);
    }

    /// <summary>Imports and confirms; returns the session id.</summary>
    public async Task<long> CatalogAsync(ImportRequest request)
    {
        var ct = TestContext.Current.CancellationToken;
        var (session, import) = await ImportAsync(request);
        Assert.True(import.State == JobState.Completed, import.Error);
        var commit = await Jobs.WaitAsync(await Imports.ConfirmAsync(session, ct), ct);
        Assert.True(commit.State == JobState.Completed, commit.Error);
        return session;
    }

    /// <summary>Waits until the session's previews are done and it can be confirmed.</summary>
    public async Task WaitReadyAsync(long sessionId)
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            await using (var db = Catalog.Db())
            {
                var state = await db.ImportSessions.Where(s => s.Id == sessionId).Select(s => s.State).FirstAsync(ct);
                if (state != ImportSessionState.Scanning) return;
            }
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Import session never became ready.");
            await Task.Delay(50, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Jobs.DisposeAsync();
        await _runtime.DisposeAsync();
        Processing.Dispose();
        await Catalog.DisposeAsync();
    }

    /// <summary>Lets the AI queue be created before the job queue that runs the import handler.</summary>
    private sealed class LateQueue : IJobQueue
    {
        public IJobQueue Target { get; set; } = null!;
        public Task<long> EnqueueAsync(string type, string title, object payload, long? parentJobId = null, CancellationToken ct = default) => Target.EnqueueAsync(type, title, payload, parentJobId, ct);
        public Task CancelAsync(long jobId, CancellationToken ct = default) => Target.CancelAsync(jobId, ct);
        public Task<long?> RetryFailedAsync(long jobId, CancellationToken ct = default) => Target.RetryFailedAsync(jobId, ct);
        public Task<JobSnapshot> WaitAsync(long jobId, CancellationToken ct = default) => Target.WaitAsync(jobId, ct);
    }
}
