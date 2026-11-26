using System.ClientModel.Primitives;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetManager.AI.Runtime;

/// <summary>What llama-server reports for one chat request (its <c>timings</c> object).</summary>
/// <param name="CachedTokens">Prompt tokens reused from the slot's cache instead of being processed again.</param>
public sealed record AiRequestTiming(int PromptTokens, int CachedTokens, double PromptMs, int GeneratedTokens, double GenerationMs)
{
    /// <summary>Reads <c>timings</c> from a chat completion body; null when the server sent none (design D10).</summary>
    public static AiRequestTiming? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("timings", out var t) || t.ValueKind != JsonValueKind.Object)
                return null;
            int Int(string name) => t.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;
            double Num(string name) => t.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : 0;
            return new AiRequestTiming(Int("prompt_n") + Int("cache_n"), Int("cache_n"), Num("prompt_ms"), Int("predicted_n"), Num("predicted_ms"));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Sums the AI request timings of one enrichment run.</summary>
public sealed class AiTimingTotals
{
    private readonly Lock _lock = new();

    public int Requests { get; private set; }
    public long PromptTokens { get; private set; }
    public long CachedTokens { get; private set; }
    public double PromptMs { get; private set; }
    public long GeneratedTokens { get; private set; }
    public double GenerationMs { get; private set; }

    public void Add(AiRequestTiming t)
    {
        lock (_lock)
        {
            Requests++;
            PromptTokens += t.PromptTokens;
            CachedTokens += t.CachedTokens;
            PromptMs += t.PromptMs;
            GeneratedTokens += t.GeneratedTokens;
            GenerationMs += t.GenerationMs;
        }
    }

    public override string ToString() => string.Format(CultureInfo.InvariantCulture,
        "{0} requests; prompt {1:N0} tokens ({2:N0} reused from cache), {3:0.0} s; generated {4:N0} tokens, {5:0.0} s",
        Requests, PromptTokens, CachedTokens, PromptMs / 1000, GeneratedTokens, GenerationMs / 1000);
}

/// <summary>
/// Collects AI request timings for the code running inside <see cref="Collect"/> (design D10): the ambient totals flow
/// into everything that code starts, including parallel requests, and <see cref="LlamaTimingsPolicy"/> adds to them.
/// </summary>
public static class AiTimings
{
    private static readonly AsyncLocal<AiTimingTotals?> Current = new();

    public static Scope Collect()
    {
        var previous = Current.Value;
        var totals = new AiTimingTotals();
        Current.Value = totals;
        return new Scope(totals, previous);
    }

    public static void Record(AiRequestTiming timing) => Current.Value?.Add(timing);

    public readonly struct Scope(AiTimingTotals totals, AiTimingTotals? previous) : IDisposable
    {
        public AiTimingTotals Totals => totals;

        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>Reads llama-server's <c>timings</c> from each chat response and records them (design D10).</summary>
public sealed class LlamaTimingsPolicy(ILogger? logger = null) : PipelinePolicy
{
    private readonly ILogger _log = logger ?? NullLogger.Instance;

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ProcessNext(message, pipeline, currentIndex);
        Read(message);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        Read(message);
    }

    private void Read(PipelineMessage message)
    {
        // Only buffered (non-streaming) answers: the app asks for whole JSON answers.
        if (message.Response is not { IsError: false } response || !message.BufferResponse) return;
        if (AiRequestTiming.Parse(response.Content.ToMemory().Span) is not { } timing) return;
        AiTimings.Record(timing);
        _log.LogDebug("AI request: prompt {Prompt} tokens ({Cached} cached) {PromptMs:0} ms; generated {Generated} tokens {GenMs:0} ms",
            timing.PromptTokens, timing.CachedTokens, timing.PromptMs, timing.GeneratedTokens, timing.GenerationMs);
    }
}
