using System.ClientModel;
using AssetManager.Core;
using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Core.Settings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;

namespace AssetManager.AI.Runtime;

public enum AiRuntimeStatus { NotConfigured, Starting, Ready, Failed }

/// <summary>
/// Owns the chat server (Gemma, with the vision projector) and the embedding server (EmbeddingGemma) configured in
/// the app settings, restarts them when the model files or the acceleration change, and exposes them as
/// Microsoft.Extensions.AI clients over the OpenAI-compatible endpoint.
/// </summary>
public sealed class AiRuntime : IAsyncDisposable
{
    public const int ChatContext = 8192;
    public const int EmbeddingContext = 2048;

    private readonly IAppSettingsStore _settings;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly Data.Jobs.ProgressHub? _hub;

    /// <summary>The jobs-panel entry shown while models load; not a queued job, so it has a negative id.</summary>
    public const long LoadActivityId = -1;

    public AiRuntime(IAppSettingsStore settings, ILoggerFactory? loggers = null, Data.Jobs.ProgressHub? hub = null)
    {
        _settings = settings;
        _hub = hub;
        loggers ??= NullLoggerFactory.Instance;
        _log = loggers.CreateLogger<AiRuntime>();
        Chat = new LlamaServerHost("chat", loggers.CreateLogger("AssetManager.AI.LlamaServer.Chat"));
        Embedding = new LlamaServerHost("embedding", loggers.CreateLogger("AssetManager.AI.LlamaServer.Embedding"));
        Chat.StateChanged += OnChanged;
        Embedding.StateChanged += OnChanged;
        settings.Changed += OnSettingsChanged;
    }

    public LlamaServerHost Chat { get; }
    public LlamaServerHost Embedding { get; }

    public event Action? Changed;

    public AiRuntimeStatus Status => Chat.State switch
    {
        AiServerState.Ready => AiRuntimeStatus.Ready,
        AiServerState.Starting => AiRuntimeStatus.Starting,
        AiServerState.Failed => AiRuntimeStatus.Failed,
        _ => AiRuntimeStatus.NotConfigured,
    };

    public bool IsReady => Chat.State == AiServerState.Ready;

    /// <summary>A chat model is set in the settings (whether or not its server is up yet).</summary>
    public bool ChatConfigured => !string.IsNullOrWhiteSpace(_settings.Current.ChatModelPath);

    /// <summary>The chat server failed to start (a configured model will not come up without a settings change).</summary>
    public bool ChatFailed => Chat.State == AiServerState.Failed;

    public bool EmbeddingsReady => Embedding.State == AiServerState.Ready;

    /// <summary>An embedding model is set in the settings (whether or not its server is up yet).</summary>
    public bool EmbeddingsConfigured => !string.IsNullOrWhiteSpace(_settings.Current.EmbeddingModelPath);

    /// <summary>The embedding server failed to start with the current settings.</summary>
    public bool EmbeddingsFailed => Embedding.State == AiServerState.Failed;

    /// <summary>
    /// The chat model can see images: known once it runs, else assumed from a configured vision projector (mmproj).
    /// </summary>
    public bool VisionAvailable => IsReady ? Chat.Modalities.Vision
        : ChatConfigured && !ChatFailed && !string.IsNullOrWhiteSpace(_settings.Current.MmprojPath);

    /// <summary>One line for the UI, e.g. "Ready (GPU) – vision".</summary>
    public string StatusText => Status switch
    {
        AiRuntimeStatus.Ready => $"Ready ({(Chat.Backend == "cpu" ? "CPU" : "GPU")})" +
            (Chat.Modalities.Vision ? ", vision" : "") + (Chat.Modalities.Audio ? ", audio" : "") +
            (EmbeddingsReady ? ", embeddings" : Embedding.State == AiServerState.Failed ? ", embeddings failed" : ""),
        AiRuntimeStatus.Starting => "Starting...",
        AiRuntimeStatus.Failed => $"Failed: {Chat.Error}",
        _ when ChatConfigured => EmbeddingsReady ? "Embeddings ready; the chat model loads when needed" : "Not loaded: models load when needed",
        _ => EmbeddingsReady ? "No chat model (embeddings only)" : EmbeddingsConfigured ? "No chat model; embeddings load when needed" : "No model installed",
    };

    /// <summary>The chat client, or null when the chat server is not ready.</summary>
    public IChatClient? ChatClient => Chat.Endpoint is { } e && IsReady
        ? new OpenAIClient(new ApiKeyCredential("local"), ChatOptions(e)).GetChatClient("local").AsIChatClient()
        : null;

    /// <summary>The embedding generator, or null when the embedding server is not ready.</summary>
    public IEmbeddingGenerator<string, Embedding<float>>? Embeddings => Embedding.Endpoint is { } e && EmbeddingsReady
        ? new OpenAIClient(new ApiKeyCredential("local"), new OpenAIClientOptions { Endpoint = new Uri(e, "v1") }).GetEmbeddingClient("local").AsIEmbeddingGenerator()
        : null;

    /// <summary>Chat slots for the settings: 1-4, and 1 on the CPU (slots help only when the GPU has spare compute).</summary>
    /// <summary>The current app settings (AI tagging options such as the consensus thresholds).</summary>
    public AppSettings Settings => _settings.Current;

    private LlamaTimingsPolicy? _timings;

    /// <summary>Client options for the chat server; every answer's llama.cpp timings are recorded (design D10).</summary>
    private OpenAIClientOptions ChatOptions(Uri endpoint)
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint, "v1") };
        options.AddPolicy(_timings ??= new LlamaTimingsPolicy(_log), System.ClientModel.Primitives.PipelinePosition.PerCall);
        return options;
    }

    public static int ParallelOf(AppSettings s) => s.Acceleration == AiAcceleration.Cpu ? 1 : Math.Clamp(s.AiParallelRequests, 1, 4);

    /// <summary>(Re)starts both servers for the current settings now; servers without a configured model stay off.</summary>
    public Task StartAsync(CancellationToken ct = default) => LoadAsync(embedding: true, chat: true, restart: true, ct);

    /// <summary>
    /// Loads the embedding model if it is configured and not running yet (design D16: models load when first needed).
    /// A model that failed is not retried until its settings change or <see cref="StartAsync"/> is called.
    /// </summary>
    public Task EnsureEmbeddingsAsync(CancellationToken ct = default) => LoadAsync(embedding: true, chat: false, restart: false, ct);

    /// <summary>Loads the chat model if it is configured and not running yet; see <see cref="EnsureEmbeddingsAsync"/>.</summary>
    public Task EnsureChatAsync(CancellationToken ct = default) => LoadAsync(embedding: false, chat: true, restart: false, ct);

    private object? _embeddingKey, _chatKey;

    private static object EmbeddingKeyOf(AppSettings s) => (s.EmbeddingModelPath, s.Acceleration);
    private static object ChatKeyOf(AppSettings s) => (s.ChatModelPath, s.MmprojPath, s.Acceleration, ParallelOf(s));

    /// <summary>Settings changed: reload the servers that are loaded (or loading) with other settings; the rest load on demand.</summary>
    private void OnSettingsChanged(AppSettings s)
    {
        var embedding = Embedding.State is AiServerState.Ready or AiServerState.Starting && !Equals(EmbeddingKeyOf(s), _embeddingKey);
        var chat = Chat.State is AiServerState.Ready or AiServerState.Starting && !Equals(ChatKeyOf(s), _chatKey);
        if (embedding || chat) _ = LoadAsync(embedding, chat, restart: true);
    }

    private async Task LoadAsync(bool embedding, bool chat, bool restart, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var s = _settings.Current;
            bool Wanted(LlamaServerHost host, string? path, object key, object? last) =>
                !string.IsNullOrWhiteSpace(path) && host.State switch
                {
                    AiServerState.Ready or AiServerState.Starting => restart && !Equals(key, last),
                    AiServerState.Failed => restart || !Equals(key, last),
                    _ => true,
                };
            var loadEmbedding = embedding && Wanted(Embedding, s.EmbeddingModelPath, EmbeddingKeyOf(s), _embeddingKey);
            var loadChat = chat && Wanted(Chat, s.ChatModelPath, ChatKeyOf(s), _chatKey);
            if (restart && embedding && string.IsNullOrWhiteSpace(s.EmbeddingModelPath)) await Embedding.StopAsync();
            if (restart && chat && string.IsNullOrWhiteSpace(s.ChatModelPath)) await Chat.StopAsync();
            if (!loadEmbedding && !loadChat) return;

            // Embeddings first: the small model is ready in seconds, so semantic search does not wait behind the chat
            // model, which can take minutes to load from a slow drive (design D5). The load shows as one entry in the
            // jobs panel, so a slow start is visible instead of "nothing happens".
            var models = new List<(string Stage, string? Path)>();
            if (loadEmbedding) models.Add(("Embedding model", s.EmbeddingModelPath));
            if (loadChat) models.Add(("Chat model", s.ChatModelPath));
            var started = DateTime.UtcNow;
            void Show(int index) => _hub?.Publish(LoadSnapshot(models, index, started, JobState.Running, null));

            if (loadEmbedding)
            {
                Show(0);
                _embeddingKey = EmbeddingKeyOf(s);
                await Embedding.StartAsync(new LlamaServerOptions(s.EmbeddingModelPath!, null, s.Acceleration, true, EmbeddingContext), ct);
            }
            if (loadChat)
            {
                Show(models.Count - 1);
                _chatKey = ChatKeyOf(s);
                await Chat.StartAsync(new LlamaServerOptions(s.ChatModelPath!, s.MmprojPath, s.Acceleration, false, ChatContext, ParallelOf(s)), ct);
            }
            var failed = new[] { (loadEmbedding, Embedding), (loadChat, Chat) }
                .Where(x => x.Item1 && x.Item2.State == AiServerState.Failed).Select(x => x.Item2).FirstOrDefault();
            _hub?.Finish(LoadSnapshot(models, models.Count, started, failed is null ? JobState.Completed : JobState.Failed,
                failed is null ? null : $"{failed.Name} model: {failed.Error}"));
            _log.LogInformation("AI status: {Status}", StatusText);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogError(e, "AI runtime failed to start");
        }
        finally
        {
            // Never leave the jobs-panel entry running after an exception or cancellation.
            if (_hub?.Get(LoadActivityId) is { IsActive: true } shown)
                _hub.Finish(shown with { State = ct.IsCancellationRequested ? JobState.Cancelled : JobState.Failed, FinishedAt = DateTime.UtcNow,
                                         Error = ct.IsCancellationRequested ? null : "The AI runtime failed to start; see the log." });
            _gate.Release();
            OnChanged();
        }
    }

    private void OnChanged() => Changed?.Invoke();

    private static JobSnapshot LoadSnapshot(List<(string Stage, string? Path)> models, int index, DateTime started, JobState state, string? error)
    {
        var current = index < models.Count ? models[index] : default;
        return new JobSnapshot(LoadActivityId, "ai.load", "Loading AI models", state, current.Stage, Math.Min(index, models.Count), models.Count,
            0, 0, current.Path is null ? null : Path.GetFileName(current.Path), 0, 0, started, started,
            state is JobState.Running ? null : DateTime.UtcNow, null, error, null, null);
    }

    public async ValueTask DisposeAsync()
    {
        await Chat.DisposeAsync();
        await Embedding.DisposeAsync();
    }
}

/// <summary>
/// With "Load AI models at startup" on, starts the AI servers in the background once the window shows the catalog
/// (never blocks startup; design D5). Otherwise the models load when first needed (design D16) and the AI counts as
/// settled at once, so interrupted jobs need not wait for it.
/// </summary>
public sealed class AiRuntimeStartupTask(AiRuntime runtime, StartupSignals? signals = null) : IStartupTask
{
    public int Order => 30;

    /// <summary>The longest the AI start waits for the window and its first page's data.</summary>
    public static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(60);

    public Task RunAsync(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (!runtime.Settings.AiLoadAtStartup) return;
                if (signals is not null) await signals.WaitForWindowAsync(TimeSpan.Zero, WindowWait);
                await runtime.StartAsync(CancellationToken.None);
            }
            finally
            {
                signals?.SignalAiSettled();
            }
        }, CancellationToken.None);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => runtime.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        return Task.CompletedTask;
    }
}
