// Qwen35ModelTests.cs
//
// The C# qwen35 model against the pinned fork: fixtures/bonsai2/2B/logits.json holds, for 12
// rendered prompts, the fork's greedy token and top-20 logits at each of 32 steps. The model is
// fed the fork's own tokens (teacher forcing), so one near-tie cannot carry a difference
// forward, and at every step:
//   - the token the fork chose must be the C# model's top token, unless the fork itself was
//     torn (its top two logits closer than the tolerance), and
//   - each of the fork's top 20 logits must match within the tolerance.
// The tolerance is not zero: the fork sums its products and attention in vector order and
// keeps attention in fp16 arithmetic, so a separate implementation agrees to within rounding.
// Measured 2026-10-30 over all 12 x 32 in Release: every greedy token equal to the fork's,
// the largest top-20 difference 0.249 (on logits of about 15 to 27); 0.3 holds that with room.
//
// Needs the 2B on this machine (says SKIPPED otherwise). It runs BONSAI2_PARITY_CASES cases of
// BONSAI2_PARITY_STEPS steps (2 x 8 unless set; 12 x 32 is everything) - in a Debug build the
// kernels are unoptimised and the full run belongs in Release.

using System.Diagnostics;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public sealed class Qwen35ModelTests(ITestOutputHelper output)
{
    private const float Tolerance = 0.3f;

    private static int Setting(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    [Fact]
    public void The_2B_predicts_the_forks_tokens_and_logits()
    {
        var path = Bonsai2Model.TwoB();
        if (path is null)
        {
            output.WriteLine(Bonsai2Model.Missing);
            return;
        }

        using var file = GgufFile.Open(path);
        using var pool = new WorkerPool(Math.Max(1, Environment.ProcessorCount));
        var load = Stopwatch.StartNew();
        var model = Qwen35Model.Load(file, pool);
        load.Stop();

        using var fixture = Bonsai2Model.Fixture("logits.json");
        var cases = fixture.RootElement.GetProperty("cases").EnumerateArray().Take(Setting("BONSAI2_PARITY_CASES", 2)).ToList();
        var stepCount = Setting("BONSAI2_PARITY_STEPS", 8);

        var logits = new float[model.Config.Vocabulary];
        var worst = 0f;
        var worstAt = "";
        var tokens = 0;
        var flips = new List<string>();
        var torn = new List<string>();
        var run = Stopwatch.StartNew();
        foreach (var c in cases)
        {
            var name = c.GetProperty("name").GetString()!;
            var prompt = c.GetProperty("prompt_ids").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            var steps = c.GetProperty("steps").EnumerateArray().Take(stepCount).ToList();
            var state = model.NewState(prompt.Length + steps.Count + 1);
            for (var i = 0; i < prompt.Length; i++) model.Forward(prompt[i], state, i == prompt.Length - 1 ? logits : default);
            tokens += prompt.Length;

            for (var s = 0; s < steps.Count; s++)
            {
                var step = steps[s];
                var expected = step.GetProperty("token").GetInt32();
                var gap = step.GetProperty("gap").GetSingle();
                foreach (var pair in step.GetProperty("top").EnumerateArray())
                {
                    var id = pair[0].GetInt32();
                    var diff = MathF.Abs(logits[id] - pair[1].GetSingle());
                    if (diff > worst)
                    {
                        worst = diff;
                        worstAt = $"{name} step {s}, token {id}";
                    }
                }

                var top = ArgMax(logits);
                if (top != expected)
                {
                    var flip = $"{name} step {s}: C# {top}, fork {expected} (fork's gap {gap:F4}, C# margin {logits[top] - logits[expected]:F4})";
                    if (gap < Tolerance) torn.Add(flip);
                    else flips.Add(flip);
                }

                if (s + 1 < steps.Count)
                {
                    model.Forward(expected, state, logits);
                    tokens++;
                }
            }
        }
        run.Stop();

        output.WriteLine($"loaded in {load.ElapsedMilliseconds} ms; {cases.Count} cases x {stepCount} steps, {tokens} tokens in {run.Elapsed.TotalSeconds:F1} s ({tokens / run.Elapsed.TotalSeconds:F2} tok/s on {pool.Threads} threads)");
        output.WriteLine($"largest top-20 logit difference: {worst:F5} ({worstAt})");
        foreach (var flip in torn) output.WriteLine("different top token where the fork was torn: " + flip);
        foreach (var flip in flips) output.WriteLine("DIFFERENT TOP TOKEN: " + flip);

        Assert.True(worst <= Tolerance, $"a top-20 logit differs by {worst} at {worstAt}");
        Assert.True(flips.Count == 0, $"{flips.Count} top tokens differ where the fork was not torn: {string.Join("; ", flips)}");
    }

    private static int ArgMax(ReadOnlySpan<float> values)
    {
        var best = 0;
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] > values[best]) best = i;
        }
        return best;
    }
}
