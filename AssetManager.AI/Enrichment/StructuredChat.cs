using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetManager.AI.Enrichment;

/// <summary>The model's answer could not be turned into a valid result, even after retries.</summary>
public sealed class AiOutputException(string message, string? lastReply) : Exception(message)
{
    public string? LastReply { get; } = lastReply;
}

/// <summary>
/// Asks the chat model for JSON matching <typeparamref name="T"/>'s schema (llama.cpp enforces it through a grammar),
/// then parses and validates it. Invalid answers are sent back with the problems listed, up to <see cref="MaxAttempts"/>
/// tries in total; after that an <see cref="AiOutputException"/> tells the caller to mark the item failed.
/// </summary>
public sealed class StructuredChat(IChatClient client, ILogger? logger = null)
{
    public const string EnglishSystemPrompt =
        "You catalog game assets (3D models, textures, sprites, sounds, music, fonts). Always answer in English, " +
        "even when names, paths or documents are in another language. Answer only with JSON matching the schema. " +
        "Be concrete and factual; do not invent licenses, URLs or details you cannot see.";

    public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // Set up front and frozen: requests run in parallel, and schema creation would otherwise fill in the resolver
        // on first use, racing with another thread ("instance is read-only").
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    }.Frozen();

    private readonly ILogger _log = logger ?? NullLogger.Instance;

    public int MaxAttempts { get; init; } = 3;

    public float Temperature { get; init; } = 0.2f;

    public int MaxOutputTokens { get; init; } = 2048;

    public async Task<T> AskAsync<T>(IEnumerable<ChatMessage> prompt, Func<T, IEnumerable<string>>? validate = null, CancellationToken ct = default)
        where T : class
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, EnglishSystemPrompt) };
        messages.AddRange(prompt);
        var options = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema<T>(JsonOptions),
            Temperature = Temperature,
            MaxOutputTokens = MaxOutputTokens,
        };

        string? reply = null;
        List<string> problems = [];
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var response = await client.GetResponseAsync(messages, options, ct);
            reply = response.Text;
            problems = Check(reply, validate, out var result);
            if (problems.Count == 0) return result!;

            _log.LogWarning("AI answer for {Type} rejected (attempt {Attempt}/{Max}): {Problems}", typeof(T).Name, attempt, MaxAttempts, string.Join("; ", problems));
            messages.Add(new ChatMessage(ChatRole.Assistant, reply));
            messages.Add(new ChatMessage(ChatRole.User,
                "That answer was not valid: " + string.Join("; ", problems) + ". Reply again with corrected JSON only."));
        }
        throw new AiOutputException($"Invalid AI output after {MaxAttempts} attempts: {string.Join("; ", problems)}", reply);
    }

    /// <summary>Parses and validates one reply; returns the problems (empty when valid).</summary>
    public static List<string> Check<T>(string? reply, Func<T, IEnumerable<string>>? validate, out T? result) where T : class
    {
        result = null;
        if (string.IsNullOrWhiteSpace(reply)) return ["the answer was empty"];
        try
        {
            result = JsonSerializer.Deserialize<T>(StripFences(reply), JsonOptions);
        }
        catch (JsonException e)
        {
            return [$"invalid JSON ({e.Message})"];
        }
        if (result is null) return ["the answer was null"];
        var problems = new List<string>();
        // The caller's check first: it may hand the result what the request contained (e.g. the expected item labels).
        if (validate is not null) problems.AddRange(validate(result));
        if (result is IAiResult self) problems.AddRange(self.Validate());
        return problems;
    }

    /// <summary>Removes a Markdown code fence some models wrap JSON in.</summary>
    internal static string StripFences(string s)
    {
        s = s.Trim();
        if (!s.StartsWith("```")) return s;
        var firstNewline = s.IndexOf('\n');
        var end = s.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline > 0 && end > firstNewline ? s[(firstNewline + 1)..end].Trim() : s;
    }
}

internal static class JsonOptionsExtensions
{
    public static System.Text.Json.JsonSerializerOptions Frozen(this System.Text.Json.JsonSerializerOptions options)
    {
        options.MakeReadOnly();
        return options;
    }
}
