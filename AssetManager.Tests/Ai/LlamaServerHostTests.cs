using System.Diagnostics;
using AssetManager.AI.Runtime;
using AssetManager.Core.Settings;
using Microsoft.Extensions.AI;

namespace AssetManager.Tests.Ai;

[Collection(AiModelCollection.Name)]
public class LlamaServerHostTests
{
    [Fact]
    public async Task Missing_model_fails_without_starting_a_process()
    {
        await using var host = new LlamaServerHost("chat");
        await host.StartAsync(new LlamaServerOptions(@"C:\nope\model.gguf", null, AiAcceleration.Auto, false, 2048));
        Assert.Equal(AiServerState.Failed, host.State);
        Assert.Contains("not found", host.Error);
    }

    [Fact]
    public async Task Runtime_without_model_reports_not_configured()
    {
        using var dir = Fixtures.FixtureBuilder.TempDir();
        await using var runtime = new AiRuntime(new JsonAppSettingsStore(Path.Combine(dir.Path, "s.json")));
        await runtime.StartAsync();
        Assert.Equal(AiRuntimeStatus.NotConfigured, runtime.Status);
        Assert.Equal("No model installed", runtime.StatusText);
        Assert.Null(runtime.ChatClient);
    }

    [AiModelFact]
    public async Task Real_model_starts_reports_modalities_answers_and_stops()
    {
        await using var host = new LlamaServerHost("chat");
        await host.StartAsync(new LlamaServerOptions(AiModelFactAttribute.Model!, AiModelFactAttribute.Mmproj, AiAcceleration.Auto, false, 4096),
            TestContext.Current.CancellationToken);

        Assert.True(host.State == AiServerState.Ready, host.Error);
        Assert.Equal(AiModelFactAttribute.Mmproj is not null, host.Modalities.Vision);
        Assert.Equal("127.0.0.1", host.Endpoint!.Host);

        var client = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential("x"),
            new OpenAI.OpenAIClientOptions { Endpoint = new Uri(host.Endpoint, "v1") }).GetChatClient("local").AsIChatClient();
        var reply = await client.GetResponseAsync("Reply with exactly the word: pong", new ChatOptions { MaxOutputTokens = 32 },
            TestContext.Current.CancellationToken);
        Assert.True(reply.Text.Contains("pong", StringComparison.OrdinalIgnoreCase), $"reply: [{reply.Text}] finish={reply.FinishReason} usage={reply.Usage?.OutputTokenCount}");

        var servers = Process.GetProcessesByName("llama-server").Length;
        await host.StopAsync();
        Assert.Equal(AiServerState.Stopped, host.State);
        for (var i = 0; i < 50 && Process.GetProcessesByName("llama-server").Length >= servers; i++) await Task.Delay(100);
        Assert.Equal(servers - 1, Process.GetProcessesByName("llama-server").Length);
    }
}
