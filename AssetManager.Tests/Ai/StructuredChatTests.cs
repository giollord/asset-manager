using System.Text.Json;
using AssetManager.AI.Enrichment;
using AssetManager.AI.Runtime;
using AssetManager.Core.Domain;
using AssetManager.Core.Settings;
using Microsoft.Extensions.AI;

namespace AssetManager.Tests.Ai;

/// <summary>Replies with the queued answers in order and records every request.</summary>
public sealed class FakeChatClient(params string[] replies) : IChatClient
{
    private readonly Queue<string> _replies = new(replies);

    public List<(List<ChatMessage> Messages, ChatOptions? Options)> Requests { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        Requests.Add((messages.ToList(), options));
        var text = _replies.Count > 1 ? _replies.Dequeue() : _replies.Peek();
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

[Collection(AiModelCollection.Name)]
public class StructuredChatTests
{
    private static readonly ChatMessage[] Prompt = [new(ChatRole.User, "Describe items 1 and 2.")];

    private const string ValidBatch = """
        {"items":[
          {"index":1,"description":"A wooden crate.","type":"model3_d","tags":["crate","wood"],"colors":["brown"],"style":["low poly"],"is_game_asset":true},
          {"index":2,"description":"A metal barrel.","type":"model3_d","tags":["barrel"],"colors":[],"style":[],"is_game_asset":true}
        ]}
        """;

    [Fact]
    public async Task Invalid_json_is_retried_and_then_marked_failed()
    {
        var client = new FakeChatClient("{\"items\": [ {\"index\": 1, ");
        var chat = new StructuredChat(client);

        var e = await Assert.ThrowsAsync<AiOutputException>(() =>
            chat.AskAsync<ItemBatchResult>(Prompt, ct: TestContext.Current.CancellationToken));

        Assert.Equal(3, client.Requests.Count);
        Assert.Contains("invalid JSON", e.Message);
        Assert.Equal("{\"items\": [ {\"index\": 1, ", e.LastReply);
        // Each retry carries the rejected answer and the reason.
        var last = client.Requests[^1].Messages;
        Assert.Equal(ChatRole.Assistant, last[^2].Role);
        Assert.Contains("not valid", last[^1].Text);
    }

    [Fact]
    public async Task A_retry_that_fixes_the_answer_succeeds()
    {
        var missingItem = """{"items":[{"index":1,"description":"A wooden crate.","type":"model3_d","tags":["crate"],"colors":[],"style":[],"is_game_asset":true}]}""";
        var client = new FakeChatClient(missingItem, ValidBatch);
        var chat = new StructuredChat(client);

        var result = await chat.AskAsync<ItemBatchResult>(Prompt, r => { r.Expected = [1, 2]; return r.Validate(); }, TestContext.Current.CancellationToken);

        Assert.Equal(2, client.Requests.Count);
        Assert.Contains("missing items: 2", client.Requests[1].Messages[^1].Text);
        Assert.Equal([1, 2], result.Items.Select(i => i.Index));
        Assert.Equal(AssetType.Model3D, result.Items[0].Type);
    }

    [Fact]
    public async Task Requests_ask_for_the_json_schema_and_english()
    {
        var client = new FakeChatClient(ValidBatch);
        await new StructuredChat(client).AskAsync<ItemBatchResult>(Prompt, ct: TestContext.Current.CancellationToken);

        var (messages, options) = client.Requests.Single();
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Contains("English", messages[0].Text);
        var format = Assert.IsType<ChatResponseFormatJson>(options!.ResponseFormat);
        var schema = format.Schema!.Value.GetRawText();
        Assert.Contains("\"is_game_asset\"", schema);
        Assert.Contains("\"model3_d\"", schema);
        Assert.DoesNotContain("\"expected\"", schema, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validation_rules_catch_bad_values()
    {
        Assert.Contains(StructuredChat.Check<OrientationResult>("""{"observations":[],"choice":7,"yaw_fix":45,"confidence":2}""", null, out _),
            p => p.Contains("choice"));
        Assert.Equal(4, StructuredChat.Check<OrientationResult>("""{"observations":[],"choice":7,"yaw_fix":45,"confidence":2}""", null, out _).Count);
        Assert.Empty(StructuredChat.Check<OrientationResult>("```json\n{\"observations\":[\"a\",\"b\",\"c\",\"d\",\"e\",\"f\"],\"choice\":0,\"yaw_fix\":0,\"confidence\":0.4}\n```", null, out var o));
        Assert.Equal(0, o!.Choice);

        var pack = """{"description":"Trees.","license":{"kind":"CC0","commercial":"yes","attribution":"no","attribution_text":null,"evidence":"license.pdf"},"links":[{"url":"itch.io/x","label":null,"evidence":null}],"script_notes":null,"style":[],"tags":[]}""";
        Assert.Contains(StructuredChat.Check<PackResult>(pack, null, out var p), e => e.Contains("itch.io/x"));
        var license = p!.License.ToLicenseInfo();
        Assert.Equal((TriState.Yes, TriState.No, "CC0", "license.pdf"), (license.Commercial, license.Attribution, license.Kind, license.Evidence));
        Assert.Equal(["tree", "low poly"], AiText.NormalizeTags([" Tree", "#tree", "Low Poly", ""]));
    }

    [AiModelFact]
    public async Task Real_model_follows_the_schemas()
    {
        await using var host = new LlamaServerHost("chat");
        await host.StartAsync(new LlamaServerOptions(AiModelFactAttribute.Model!, null, AiAcceleration.Auto, false, 4096), TestContext.Current.CancellationToken);
        Assert.True(host.State == AiServerState.Ready, host.Error);
        var client = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential("x"),
            new OpenAI.OpenAIClientOptions { Endpoint = new Uri(host.Endpoint!, "v1") }).GetChatClient("local").AsIChatClient();
        var chat = new StructuredChat(client);

        var batch = await chat.AskAsync<ItemBatchResult>(
            [new(ChatRole.User, "Items (no images available):\n1. Holzkiste.fbx (3D model, Model3D), folder Props/Kisten\n2. Fass_rostig.png (Texture), folder Texturen")],
            r => { r.Expected = [1, 2]; return []; }, TestContext.Current.CancellationToken);
        Assert.Equal([1, 2], batch.Items.Select(i => i.Index).Order());
        Assert.All(batch.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.Description)));

        var pack = await chat.AskAsync<PackResult>(
            [new(ChatRole.User, "Pack \"Forest Kit\" with 120 FBX trees and rocks.\nDocument license.txt:\nThis pack is released under CC0. You may use it in commercial projects. Credit is not required. https://example.com/forest")],
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(TriState.Yes, pack.License.Commercial);
        Assert.Contains(pack.Links, l => l.Url.StartsWith("https://example.com/forest"));

        var orientation = await chat.AskAsync<OrientationResult>(
            [new(ChatRole.User, "Candidates: 1 shows a tree lying on its side, 2 shows it upside down, 3 shows it standing upright, 4-6 lying sideways. Which is upright?")],
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, orientation.Choice);
        TestContext.Current.SendDiagnosticMessage(JsonSerializer.Serialize(new { batch, pack, orientation }, StructuredChat.JsonOptions));
    }
}
