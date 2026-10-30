// TokenSamplerTests.cs
//
// The sampler: greedy is the highest logit after the penalties; a seed repeats; draws follow
// the softmax; top-k, top-p and min-p cut where they say; the penalties count only the
// window of generated tokens; and top-k over a real vocabulary keeps exactly the top K.

using CircleAI.Inference.Managed.Sampling;
using Xunit;

namespace CircleAI.Tests;

public sealed class TokenSamplerTests
{
    private static SamplingSettings Plain(float temperature = 1f, int topK = 0, float topP = 1f, float minP = 0f) =>
        new(temperature, topK, topP, minP, 0f, 0f, 1f, 0);

    // Logits whose softmax at temperature 1 is exactly the given probabilities.
    private static float[] FromProbabilities(params double[] p) => p.Select(x => (float)Math.Log(x)).ToArray();

    private static double[] Frequencies(TokenSampler sampler, float[] logits, int draws)
    {
        var counts = new double[logits.Length];
        for (var i = 0; i < draws; i++) counts[sampler.Sample(logits.ToArray(), [])]++;
        return counts.Select(c => c / draws).ToArray();
    }

    [Fact]
    public void Greedy_is_the_highest_logit_and_the_first_of_equals()
    {
        var sampler = new TokenSampler(SamplingSettings.Greedy);
        Assert.Equal(2, sampler.Sample(new float[] { 1f, 3f, 5f, 4f }, []));
        Assert.Equal(1, sampler.Sample(new float[] { 1f, 5f, 5f }, []));
    }

    [Fact]
    public void A_seed_repeats_its_draws()
    {
        var logits = FromProbabilities(0.4, 0.3, 0.2, 0.1);
        var a = new TokenSampler(Plain(), seed: 42);
        var b = new TokenSampler(Plain(), seed: 42);
        var first = Enumerable.Range(0, 200).Select(_ => a.Sample(logits.ToArray(), [])).ToArray();
        var second = Enumerable.Range(0, 200).Select(_ => b.Sample(logits.ToArray(), [])).ToArray();
        Assert.Equal(first, second);
    }

    [Fact]
    public void Draws_follow_the_softmax()
    {
        var f = Frequencies(new TokenSampler(Plain(), seed: 1), FromProbabilities(0.5, 0.3, 0.2), 40_000);
        Assert.InRange(f[0], 0.48, 0.52);
        Assert.InRange(f[1], 0.28, 0.32);
        Assert.InRange(f[2], 0.18, 0.22);
    }

    [Fact]
    public void Temperature_flattens_or_sharpens_before_the_draw()
    {
        // At temperature 2 the probabilities go as sqrt: 0.5,0.3,0.2 -> 0.4183,0.3240,0.2646 (normalised).
        var f = Frequencies(new TokenSampler(Plain(temperature: 2f), seed: 2), FromProbabilities(0.5, 0.3, 0.2), 40_000);
        Assert.InRange(f[0], 0.40, 0.44);
        Assert.InRange(f[2], 0.245, 0.285);
    }

    [Fact]
    public void Top_p_keeps_the_shortest_run_that_reaches_p()
    {
        // 0.5 + 0.3 reaches 0.6: the third token is never drawn, the two left share 5:3.
        var f = Frequencies(new TokenSampler(Plain(topP: 0.6f), seed: 3), FromProbabilities(0.5, 0.3, 0.2), 40_000);
        Assert.Equal(0, f[2]);
        Assert.InRange(f[0], 0.605, 0.645);
    }

    [Fact]
    public void Min_p_drops_what_is_less_likely_than_its_share_of_the_likeliest()
    {
        var f = Frequencies(new TokenSampler(Plain(minP: 0.5f), seed: 4), FromProbabilities(0.5, 0.3, 0.2), 20_000);
        Assert.Equal(0, f[2]); // 0.2 < 0.5 * 0.5
        Assert.True(f[1] > 0);
    }

    [Fact]
    public void Top_k_of_one_is_greedy_at_any_temperature()
    {
        var sampler = new TokenSampler(Plain(temperature: 5f, topK: 1), seed: 5);
        for (var i = 0; i < 100; i++) Assert.Equal(1, sampler.Sample(FromProbabilities(0.2, 0.5, 0.3), []));
    }

    [Fact]
    public void Top_k_over_a_real_vocabulary_keeps_exactly_the_top_k()
    {
        var rng = new Random(6);
        var logits = new float[248_320];
        for (var i = 0; i < logits.Length; i++) logits[i] = (float)(rng.NextDouble() * 30 - 10);
        var top20 = logits.Select((v, i) => (v, i)).OrderByDescending(p => p.v).Take(20).Select(p => p.i).ToHashSet();

        var sampler = new TokenSampler(Plain(topK: 20), seed: 7);
        for (var i = 0; i < 2_000; i++) Assert.Contains(sampler.Sample(logits.ToArray(), []), top20);

        // Near zero temperature the clear winner is always drawn. (A clear one: the top two of
        // 248,320 random logits are ~1e-4 apart, which even 0.001 cannot separate.)
        logits[123_456] = 40f;
        var cold = new TokenSampler(Plain(temperature: 0.001f, topK: 20), seed: 8);
        for (var i = 0; i < 100; i++) Assert.Equal(123_456, cold.Sample(logits.ToArray(), []));
    }

    [Fact]
    public void Penalties_count_only_the_window_of_generated_tokens()
    {
        // Presence: two equal logits, the repeated one loses.
        var presence = new TokenSampler(new SamplingSettings(0f, 0, 1f, 0f, 1.5f, 0f, 1f, 64));
        Assert.Equal(1, presence.Sample(new float[] { 1f, 1f }, [0]));

        // Frequency: three occurrences cost three times as much as one.
        var frequency = new TokenSampler(new SamplingSettings(0f, 0, 1f, 0f, 0f, 1f, 1f, 64));
        var logits = new float[] { 5f, 4f, 3f };
        frequency.Sample(logits, [0, 0, 0, 1]);
        Assert.Equal(new[] { 2f, 3f, 3f }, logits);

        // Repeat: a positive logit is divided, a negative one multiplied.
        var repeat = new TokenSampler(new SamplingSettings(0f, 0, 1f, 0f, 0f, 0f, 2f, 64));
        logits = [4f, -2f, 1f];
        repeat.Sample(logits, [0, 1]);
        Assert.Equal(new[] { 2f, -4f, 1f }, logits);

        // The window: only the last two generated tokens count, so token 0 is not penalised.
        var window = new TokenSampler(new SamplingSettings(0f, 0, 1f, 0f, 10f, 0f, 1f, 2));
        Assert.Equal(0, window.Sample(new float[] { 2f, 1f, 1f }, [0, 1, 2]));
    }

    [Fact]
    public void The_card_profiles_are_the_qwen35_cards()
    {
        Assert.Equal(new SamplingSettings(1.0f, 20, 0.95f, 0f, 1.0f, 0f, 1.0f, 64), SamplingSettings.Thinking);
        Assert.Equal(new SamplingSettings(0.7f, 20, 0.8f, 0f, 1.5f, 0f, 1.0f, 64), SamplingSettings.Direct);
    }
}
