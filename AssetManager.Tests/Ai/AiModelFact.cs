namespace AssetManager.Tests.Ai;

/// <summary>
/// Integration test against a real local model. Runs only when ASSETMANAGER_AI_MODEL points to a chat GGUF
/// (ASSETMANAGER_AI_MMPROJ optionally to its vision projector, ASSETMANAGER_AI_EMBED to an embedding GGUF).
/// </summary>
public sealed class AiModelFactAttribute : FactAttribute
{
    public static string? Model => Existing("ASSETMANAGER_AI_MODEL");
    public static string? Mmproj => Existing("ASSETMANAGER_AI_MMPROJ");
    public static string? Embed => Existing("ASSETMANAGER_AI_EMBED");

    public AiModelFactAttribute()
    {
        if (Model is null) Skip = "Set ASSETMANAGER_AI_MODEL to a chat GGUF to run.";
    }

    private static string? Existing(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } p && File.Exists(p) ? p : null;
}

/// <summary>Starts the real model for a test and hands out a chat client; dispose to stop the server.</summary>
public sealed class AiTestServer : IAsyncDisposable
{
    private AiTestServer(AssetManager.AI.Runtime.LlamaServerHost host, Microsoft.Extensions.AI.IChatClient client) => (Host, Client) = (host, client);

    public AssetManager.AI.Runtime.LlamaServerHost Host { get; }
    public Microsoft.Extensions.AI.IChatClient Client { get; }
    public bool Vision => Host.Modalities.Vision;

    public static async Task<AiTestServer> StartAsync(bool vision = false, int context = 8192)
    {
        var host = new AssetManager.AI.Runtime.LlamaServerHost("chat");
        await host.StartAsync(new AssetManager.AI.Runtime.LlamaServerOptions(AiModelFactAttribute.Model!, vision ? AiModelFactAttribute.Mmproj : null,
            AssetManager.Core.Settings.AiAcceleration.Auto, false, context), TestContext.Current.CancellationToken);
        Assert.True(host.State == AssetManager.AI.Runtime.AiServerState.Ready, host.Error);
        var client = Microsoft.Extensions.AI.OpenAIClientExtensions.AsIChatClient(new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential("x"),
            new OpenAI.OpenAIClientOptions { Endpoint = new Uri(host.Endpoint!, "v1") }).GetChatClient("local"));
        return new AiTestServer(host, client);
    }

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}

/// <summary>
/// Test classes that start real llama-server instances run one at a time: several 5 GB models loading at once
/// exhaust GPU memory and time out.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AiModelCollection
{
    public const string Name = "Real AI model";
}
