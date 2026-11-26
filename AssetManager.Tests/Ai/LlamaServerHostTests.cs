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

    /// <summary>Design D4/D7: GPU and CPU flags on the one server build; model loading left at llama.cpp's mmap default.</summary>
    [Fact]
    public void Server_arguments()
    {
        var chat = new LlamaServerOptions(@"C:\m\chat.gguf", null, AiAcceleration.Auto, false, 8192, 3);
        var gpu = LlamaServerHost.BuildArguments("vulkan", chat, 5000);
        var cpu = LlamaServerHost.BuildArguments("cpu", chat with { Parallel = 1 }, 5000);

        Assert.Equal("999", gpu[gpu.IndexOf("-ngl") + 1]);
        Assert.DoesNotContain("--device", gpu);
        Assert.Equal("24576", gpu[gpu.IndexOf("-c") + 1]); // context per slot x 3 slots
        Assert.Equal("0", cpu[cpu.IndexOf("-ngl") + 1]);
        Assert.Equal("none", cpu[cpu.IndexOf("--device") + 1]);
        foreach (var args in new[] { gpu, cpu })
        {
            Assert.DoesNotContain("--no-mmap", args);
            Assert.DoesNotContain("--load-mode", args);
            Assert.DoesNotContain("-lm", args);
        }
    }

    /// <summary>Design D4: one llama.cpp build serves both GPU and CPU mode.</summary>
    [Theory]
    [InlineData(AiAcceleration.Gpu, "vulkan")]
    [InlineData(AiAcceleration.Cpu, "cpu")]
    public async Task One_server_build_runs_on_gpu_and_cpu(AiAcceleration acceleration, string backend)
    {
        Assert.SkipWhen(AiModelFactAttribute.Embed is null, "Set ASSETMANAGER_AI_EMBED to an embedding GGUF to run.");
        await using var host = new LlamaServerHost("embedding");
        await host.StartAsync(new LlamaServerOptions(AiModelFactAttribute.Embed!, null, acceleration, true, 2048), TestContext.Current.CancellationToken);

        Assert.Equal(AiServerState.Ready, host.State);
        Assert.Equal(backend, host.Backend);
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
