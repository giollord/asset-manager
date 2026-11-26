using System.Text;
using AssetManager.AI.Runtime;

namespace AssetManager.Tests.Ai;

/// <summary>Design D10: llama-server's per-request timings, summed per enrichment run.</summary>
public class AiTimingsTests
{
    // The shape of a llama-server (b12141) chat completion: OpenAI fields plus its own "timings" object.
    private const string WithTimings = """
        {"choices":[{"finish_reason":"stop","index":0,"message":{"role":"assistant","content":"{}"}}],
         "created":1764170000,"model":"local","object":"chat.completion",
         "usage":{"completion_tokens":35,"prompt_tokens":1237,"total_tokens":1272},
         "timings":{"cache_n":980,"prompt_n":257,"prompt_ms":412.5,"prompt_per_token_ms":1.6,"prompt_per_second":623.0,
                    "predicted_n":35,"predicted_ms":661.064,"predicted_per_token_ms":18.9,"predicted_per_second":52.9}}
        """;

    [Fact]
    public void Reads_the_timings_of_a_llama_server_answer()
    {
        var t = AiRequestTiming.Parse(Encoding.UTF8.GetBytes(WithTimings));

        Assert.Equal(new AiRequestTiming(1237, 980, 412.5, 35, 661.064), t);
    }

    [Theory]
    [InlineData("""{"choices":[],"usage":{"prompt_tokens":3}}""")]
    [InlineData("""{"timings":"n/a"}""")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Answers_without_timings_record_nothing(string body) =>
        Assert.Null(AiRequestTiming.Parse(Encoding.UTF8.GetBytes(body)));

    [Fact]
    public async Task Totals_collect_parallel_requests_of_the_run_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var one = new AiRequestTiming(100, 60, 10, 20, 100);
        AiTimings.Record(one); // outside any run: not counted

        using var run = AiTimings.Collect();
        await Parallel.ForEachAsync(Enumerable.Range(0, 8), ct, (i, _) => { AiTimings.Record(one); return ValueTask.CompletedTask; });
        await Task.Run(() => AiTimings.Record(one), ct);

        Assert.Equal(9, run.Totals.Requests);
        Assert.Equal((900L, 540L, 180L), (run.Totals.PromptTokens, run.Totals.CachedTokens, run.Totals.GeneratedTokens));
        Assert.Equal("9 requests; prompt 900 tokens (540 reused from cache), 0.1 s; generated 180 tokens, 0.9 s", run.Totals.ToString());
    }
}
