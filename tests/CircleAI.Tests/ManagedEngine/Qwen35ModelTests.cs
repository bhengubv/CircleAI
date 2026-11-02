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
//
// S5: THE REST OF THE FLEET, HELD TO THE SAME RULE. fixtures/bonsai2/4B was made the same way from
// the 4B PTQ1_0 (32 layers, 2560 wide, 16 query heads) and the 4B is held to the same tolerance and
// the same torn-token rule; so is the 2B in PQ2_0, which proves the PQ2_0 kernels on real weights.
// Each says SKIPPED when its file or its fixtures are not here.
//
// THE TOLERANCE IS 0.3, OR THE FORK'S OWN LARGEST DRIFT BETWEEN ITS 1-, 2- AND 4-THREAD RUNS ON THE
// SET WHEN THAT IS WIDER - the plan's rule, "logits within the spread between the reference's 1-, 2-
// and 4-thread runs ... exact next tokens wherever the top two differ by more than that spread". One
// number per set, not per step: the fork drifts only where its thread count changes its arithmetic
// (the 288-token tool prompt; every shorter prompt comes out to the bit at any thread count), so a
// step where it did not drift says nothing about how far rounding moves that model's logits, and the
// tool prompt is where the set shows it. The 2B's largest drift is 0.2485, so it is held to 0.3 as
// before; the 4B's is 0.3464 (tool step 9, 4 threads); the 2B in PQ2_0's 0.3096.
//
// This rule was per step first, and at 0.3 per step the 4B's first full run had one logit over:
// 0.309 at code step 29, the runner-up token, where the fork had not drifted.
//
// MEASURED 2026-11-02 OVER ALL 12 x 32 IN RELEASE (with the fixtures' own Q8_0 quantiser), as the
// differences of the fork's top-20 logits:
//   2B        mean 0.036, 99th percentile 0.141, worst 0.276 - held to 0.3, passes
//   4B        mean 0.035, 99th percentile 0.133, worst 0.318 - held to its drift 0.3464, passes
//   2B PQ2_0  mean 0.046, 99th percentile 0.177, worst 0.384 - FAILS: 5 of 6,340 beyond its
//             drift (0.3096), 6 beyond 0.3. Its Q8_K activations take one scale per 256 values
//             where Q8_0 takes one per 32, so each value the two engines round differently moves
//             further, and the whole distribution sits higher.
// Every top token was the fork's except where the fork's own top two were within 0.06. The worst of
// thousands of differences is the restless number: the fork's fp16 flash-attention arithmetic, copied
// step for step from its source into the fp16 cache's attention, left every mean where it was (0.036,
// 0.035, 0.046) and moved the 2B's worst to 0.313, the 4B's to 0.285 and the PQ2_0's to 0.351 - so
// that copy was not kept. A set near its tolerance can pass or fail on a change of
// arithmetic that changes nothing else; the mean and the percentiles printed below are the steadier
// reading. The rule is not widened to fit: the 2B in PQ2_0 fails it at full length (2 x 8 passes).

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
    public void The_2B_predicts_the_forks_tokens_and_logits() => Parity("2B");

    [Fact]
    public void The_4B_predicts_the_forks_tokens_and_logits() => Parity("4B");

    [Fact]
    public void The_2B_in_PQ2_0_predicts_the_forks_tokens_and_logits() => Parity("2B-PQ2_0");

    // The fork's greedy tokens and top-20 logits for the set's prompts, teacher-forced.
    private void Parity(string set)
    {
        var path = Bonsai2Model.Of(set);
        if (path is null)
        {
            output.WriteLine(Bonsai2Model.MissingOf(set));
            return;
        }
        if (!Bonsai2Model.HasFixture(set, "logits.json"))
        {
            output.WriteLine($"SKIPPED: fixtures/bonsai2/{set}/logits.json is not made yet (tools/bonsai-ref).");
            return;
        }

        using var file = GgufFile.Open(path);
        using var pool = new WorkerPool(Math.Max(1, Environment.ProcessorCount));
        var load = Stopwatch.StartNew();
        var model = Qwen35Model.Load(file, pool);
        load.Stop();

        // The fixtures were made from this very file.
        using var fixture = Bonsai2Model.Fixture(set, "logits.json");
        Assert.Equal(Path.GetFileName(path), fixture.RootElement.GetProperty("_source").GetProperty("model").GetString());
        var cases = fixture.RootElement.GetProperty("cases").EnumerateArray().Take(Setting("BONSAI2_PARITY_CASES", 2)).ToList();
        var stepCount = Setting("BONSAI2_PARITY_STEPS", 8);

        // The fork's own drift between its 1-, 2- and 4-thread runs anywhere in the set - the plan's
        // "spread" - or 0.3, whichever is wider, held at every step of every case.
        var spread = 0f;
        foreach (var c in fixture.RootElement.GetProperty("cases").EnumerateArray())
            foreach (var threads in c.GetProperty("spread_vs_1_thread").EnumerateObject())
                foreach (var drift in threads.Value.EnumerateArray()) spread = MathF.Max(spread, drift.GetSingle());
        var allowed = MathF.Max(Tolerance, spread);

        var logits = new float[model.Config.Vocabulary];
        var differences = new List<float>();
        var worst = 0f;
        var worstAt = "";
        var over = new List<string>();
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
                    differences.Add(diff);
                    if (diff > worst)
                    {
                        worst = diff;
                        worstAt = $"{name} step {s}, token {id}";
                    }
                    if (diff > allowed) over.Add($"{name} step {s}, token {id}: {diff:F4} against {allowed:F4}");
                }

                var top = ArgMax(logits);
                if (top != expected)
                {
                    var flip = $"{name} step {s}: C# {top}, fork {expected} (fork's gap {gap:F4}, C# margin {logits[top] - logits[expected]:F4})";
                    if (gap < allowed) torn.Add(flip);
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

        output.WriteLine($"{set} ({model.Packing}, {model.Config.Layers} layers): loaded in {load.ElapsedMilliseconds} ms; {cases.Count} cases x {stepCount} steps, {tokens} tokens in {run.Elapsed.TotalSeconds:F1} s ({tokens / run.Elapsed.TotalSeconds:F2} tok/s on {pool.Threads} threads)");
        output.WriteLine($"tolerance {allowed:F4}: 0.3, or the fork's own largest drift between its thread counts on this set ({spread:F4}) when wider");
        output.WriteLine($"largest top-20 logit difference: {worst:F5} ({worstAt})");
        differences.Sort();
        if (differences.Count > 0)
            output.WriteLine($"top-20 differences: {differences.Count} compared, mean {differences.Average():F4}, " +
                             $"median {differences[differences.Count / 2]:F4}, 99th percentile {differences[(int)(differences.Count * 0.99)]:F4}, " +
                             $"{differences.Count(d => d > 0.2f)} over 0.2, {differences.Count(d => d > Tolerance)} over {Tolerance}, {differences.Count(d => d > allowed)} over the tolerance");
        foreach (var flip in torn) output.WriteLine("different top token where the fork was torn: " + flip);
        foreach (var flip in flips) output.WriteLine("DIFFERENT TOP TOKEN: " + flip);

        Assert.True(over.Count == 0, $"{over.Count} top-20 logits differ by more than allowed: {string.Join("; ", over.Take(5))}");
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
