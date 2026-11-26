using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace AssetManager.Tests.Import;

/// <summary>
/// A fake model that answers whatever schema it is asked for: pack summaries, item batches (one entry per "Item N"
/// in the prompt, with the item's name in the description), orientation and collection descriptions.
/// </summary>
public sealed partial class SchemaAwareChatClient : IChatClient
{
    public ConcurrentQueue<string> Prompts { get; } = new();

    /// <summary>Optional delay per request, to simulate a slow model.</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>Extra text every item description gets (to check corrections).</summary>
    public string? Flavor { get; set; }

    /// <summary>Items (by name) the fake calls a sprite sheet, with its sprite_sheet answer.</summary>
    public Dictionary<string, object> SpriteSheets { get; } = [];

    /// <summary>Which consensus candidates the fake accepts as source tags (default: all).</summary>
    public Func<string, bool> AcceptConsensus { get; set; } = _ => true;

    private int _running;

    /// <summary>The most requests that were in flight at the same time.</summary>
    public int MaxConcurrent { get; private set; }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var running = Interlocked.Increment(ref _running);
        lock (Prompts) MaxConcurrent = Math.Max(MaxConcurrent, running);
        try
        {
            return await AnswerAsync(messages, options, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    private async Task<ChatResponse> AnswerAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
    {
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        var prompt = messages.Last().Text;
        Prompts.Enqueue(prompt);
        var schema = (options?.ResponseFormat as ChatResponseFormatJson)?.Schema?.GetRawText() ?? "";
        string answer;
        if (schema.Contains("\"is_game_asset\""))
        {
            var items = ItemLabel().Matches(prompt).Select(m => (Index: int.Parse(m.Groups[1].Value), Name: NameAfter(prompt, m.Index))).DistinctBy(i => i.Index);
            answer = JsonSerializer.Serialize(new
            {
                items = items.Select(i => new
                {
                    index = i.Index,
                    description = $"Described {i.Name}.{(Flavor is null ? "" : " " + Flavor)}",
                    type = SpriteSheets.ContainsKey(i.Name) ? "sprite_sheet" : TypeAfter(prompt, i.Index),
                    sprite_sheet = SpriteSheets.GetValueOrDefault(i.Name),
                    tags = new[] { "ai-tag", i.Name.ToLowerInvariant() },
                    colors = new[] { "green" },
                    style = new[] { "stylized" },
                    is_game_asset = true,
                }),
            });
        }
        else if (schema.Contains("\"script_notes\""))
            answer = """{"description":"A pack of test assets.","license":{"kind":"CC0","commercial":"yes","attribution":"no","attribution_text":null,"evidence":"LICENSE.txt"},"links":[],"script_notes":"Game logic scripts.","style":["low poly"],"tags":["pack-tag"]}""";
        else if (schema.Contains("\"apply_to_source\""))
            answer = JsonSerializer.Serialize(new
            {
                decisions = CandidateLine().Matches(prompt).Select(m => m.Groups[1].Value).Distinct()
                    .Select(c => new { tag = c, apply_to_source = AcceptConsensus(c), reason = "fake" }),
            });
        else if (schema.Contains("\"yaw_fix\""))
            answer = """{"observations":["a","b","c","d","e","f"],"choice":1,"yaw_fix":0,"confidence":0.9}""";
        else
            answer = """{"description":"A collection."}""";
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, answer));
    }

    private static string NameAfter(string prompt, int at)
    {
        var m = NameLine().Match(prompt, at);
        return m.Success ? m.Groups[1].Value.Trim() : "item";
    }

    private static string TypeAfter(string prompt, int index)
    {
        var at = prompt.IndexOf($"Item {index}", StringComparison.Ordinal);
        var m = TypeLine().Match(prompt, Math.Max(0, at));
        return m.Success ? JsonNamingPolicy.SnakeCaseLower.ConvertName(m.Groups[1].Value) : "other";
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    [GeneratedRegex("Candidate \"([^\"]+)\"")]
    private static partial Regex CandidateLine();

    [GeneratedRegex(@"Item (\d+)")]
    private static partial Regex ItemLabel();

    [GeneratedRegex(@"Name: ([^\r\n]+)")]
    private static partial Regex NameLine();

    [GeneratedRegex(@"Type: (\w+)")]
    private static partial Regex TypeLine();
}
