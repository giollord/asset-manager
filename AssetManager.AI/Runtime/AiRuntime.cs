using System.ClientModel;
using AssetManager.Core;
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
    private (string? Chat, string? Mmproj, string? Embed, AiAcceleration Acc, int Parallel) _applied;

    public AiRuntime(IAppSettingsStore settings, ILoggerFactory? loggers = null)
    {
        _settings = settings;
        loggers ??= NullLoggerFactory.Instance;
        _log = loggers.CreateLogger<AiRuntime>();
        Chat = new LlamaServerHost("chat", loggers.CreateLogger("AssetManager.AI.LlamaServer.Chat"));
        Embedding = new LlamaServerHost("embedding", loggers.CreateLogger("AssetManager.AI.LlamaServer.Embedding"));
        Chat.StateChanged += OnChanged;
        Embedding.StateChanged += OnChanged;
        settings.Changed += s =>
        {
            if ((s.ChatModelPath, s.MmprojPath, s.EmbeddingModelPath, s.Acceleration, ParallelOf(s)) != _applied) _ = StartAsync();
        };
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

    /// <summary>One line for the UI, e.g. "Ready (GPU) – vision".</summary>
    public string StatusText => Status switch
    {
        AiRuntimeStatus.Ready => $"Ready ({(Chat.Backend == "cpu" ? "CPU" : "GPU")})" +
            (Chat.Modalities.Vision ? ", vision" : "") + (Chat.Modalities.Audio ? ", audio" : "") +
            (EmbeddingsReady ? ", embeddings" : Embedding.State == AiServerState.Failed ? ", embeddings failed" : ""),
        AiRuntimeStatus.Starting => "Starting...",
        AiRuntimeStatus.Failed => $"Failed: {Chat.Error}",
        _ => EmbeddingsReady ? "No chat model (embeddings only)" : "No model installed",
    };

    /// <summary>The chat client, or null when the chat server is not ready.</summary>
    public IChatClient? ChatClient => Chat.Endpoint is { } e && IsReady
        ? new OpenAIClient(new ApiKeyCredential("local"), new OpenAIClientOptions { Endpoint = new Uri(e, "v1") }).GetChatClient("local").AsIChatClient()
        : null;

    /// <summary>The embedding generator, or null when the embedding server is not ready.</summary>
    public IEmbeddingGenerator<string, Embedding<float>>? Embeddings => Embedding.Endpoint is { } e && EmbeddingsReady
        ? new OpenAIClient(new ApiKeyCredential("local"), new OpenAIClientOptions { Endpoint = new Uri(e, "v1") }).GetEmbeddingClient("local").AsIEmbeddingGenerator()
        : null;

    /// <summary>Chat slots for the settings: 1-4, and 1 on the CPU (slots help only when the GPU has spare compute).</summary>
    /// <summary>The current app settings (AI tagging options such as the consensus thresholds).</summary>
    public AppSettings Settings => _settings.Current;

    public static int ParallelOf(AppSettings s) => s.Acceleration == AiAcceleration.Cpu ? 1 : Math.Clamp(s.AiParallelRequests, 1, 4);

    /// <summary>(Re)starts the servers for the current settings; servers without a configured model stay off.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var s = _settings.Current;
            var wanted = (s.ChatModelPath, s.MmprojPath, s.EmbeddingModelPath, s.Acceleration, ParallelOf(s));
            if (wanted == _applied && Chat.State is AiServerState.Ready or AiServerState.Starting) return;
            _applied = wanted;

            if (string.IsNullOrWhiteSpace(s.ChatModelPath)) await Chat.StopAsync();
            else await Chat.StartAsync(new LlamaServerOptions(s.ChatModelPath, s.MmprojPath, s.Acceleration, false, ChatContext, ParallelOf(s)), ct);

            if (string.IsNullOrWhiteSpace(s.EmbeddingModelPath)) await Embedding.StopAsync();
            else await Embedding.StartAsync(new LlamaServerOptions(s.EmbeddingModelPath, null, s.Acceleration, true, EmbeddingContext), ct);
            _log.LogInformation("AI status: {Status}", StatusText);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogError(e, "AI runtime failed to start");
        }
        finally
        {
            _gate.Release();
            OnChanged();
        }
    }

    private void OnChanged() => Changed?.Invoke();

    public async ValueTask DisposeAsync()
    {
        await Chat.DisposeAsync();
        await Embedding.DisposeAsync();
    }
}

/// <summary>Starts the AI servers in the background once the app is up (never blocks startup).</summary>
internal sealed class AiRuntimeStartupTask(AiRuntime runtime) : IStartupTask
{
    public int Order => 30;

    public Task RunAsync(CancellationToken ct)
    {
        _ = Task.Run(() => runtime.StartAsync(CancellationToken.None), CancellationToken.None);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => runtime.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        return Task.CompletedTask;
    }
}
