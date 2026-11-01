// TurboKvCacheTests.cs
//
// The C# engine's working memory as TurboQuant codes (S3). Attention on the codes follows exact
// attention - the scores correlate, the mixed values land close - at every width; a compressed
// state survives a session; and against the pinned fork, teacher-forced over its own fixtures,
// each mode and key form is measured: how often its top token is the fork's, and how far its top-20
// logits drift. That table is what chooses the default per bit width (the plan's "both forms
// measured").
//
// The measurement needs the 2B (says SKIPPED otherwise) and runs BONSAI2_KV_CASES cases of
// BONSAI2_KV_STEPS steps (2 x 8 unless set; 12 x 32 is everything, minutes in Release).

using CircleAI.Core;
using CircleAI.Core.Compression;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public sealed class TurboKvCacheTests(ITestOutputHelper output)
{
    private static int Setting(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static float[] Gaussian(int n, int seed)
    {
        var random = new Random(seed);
        var v = new float[n];
        for (var i = 0; i < n; i++)
            v[i] = (float)(Math.Sqrt(-2.0 * Math.Log(1.0 - random.NextDouble())) * Math.Cos(2.0 * Math.PI * random.NextDouble()));
        return v;
    }

    private static double Correlation(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double ma = 0, mb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            ma += a[i];
            mb += b[i];
        }
        ma /= a.Length;
        mb /= b.Length;
        double ab = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            ab += (a[i] - ma) * (b[i] - mb);
            aa += (a[i] - ma) * (a[i] - ma);
            bb += (b[i] - mb) * (b[i] - mb);
        }
        return ab / Math.Sqrt(aa * bb);
    }

    // Measured 2026-11-01 over 96 Gaussian keys and values of 256: score correlation 0.994 / 0.959
    // (4 bits, Mse / Prod keys), 0.983 (3, Mse), 0.933 / 0.807 (2, Mse / Prod); the mix off by
    // 0.10 / 0.18 / 0.35 at 4 / 3 / 2 bits. The limits sit just under those.
    [Theory]
    [InlineData(KvCompressionMode.TurboQuant4Bit, TurboQuantForm.Mse, 0.98, 0.13)]
    [InlineData(KvCompressionMode.TurboQuant4Bit, TurboQuantForm.Prod, 0.94, 0.13)]
    [InlineData(KvCompressionMode.TurboQuant3Bit, TurboQuantForm.Mse, 0.97, 0.22)]
    [InlineData(KvCompressionMode.TurboQuant2Bit, TurboQuantForm.Mse, 0.91, 0.40)]
    [InlineData(KvCompressionMode.TurboQuant2Bit, TurboQuantForm.Prod, 0.78, 0.40)]
    public void Attention_on_the_codes_follows_exact_attention(KvCompressionMode mode, TurboQuantForm form, double minCorrelation, double maxMixError)
    {
        const int heads = 2, dim = 256, count = 96, head = 1;
        var cache = new TurboKvCache(count, heads, dim, mode, form);
        var keys = new float[count][];
        var values = new float[count][];
        for (var t = 0; t < count; t++)
        {
            keys[t] = Gaussian(heads * dim, t);
            values[t] = Gaussian(heads * dim, 1000 + t);
            cache.Write(t, keys[t], values[t]);
        }

        var query = Gaussian(dim, 77);
        var scores = new float[count];
        cache.Scores(head, query, count, scores);
        var exact = new float[count];
        for (var t = 0; t < count; t++)
        {
            for (var j = 0; j < dim; j++) exact[t] += query[j] * keys[t][head * dim + j];
        }
        var correlation = Correlation(scores, exact);

        // Weights from the exact scores, so the mix is judged on its own.
        var weights = new float[count];
        var max = exact.Max();
        for (var t = 0; t < count; t++) weights[t] = MathF.Exp((exact[t] - max) / 16f);
        var total = weights.Sum();
        for (var t = 0; t < count; t++) weights[t] /= total;
        var mixed = new float[dim];
        cache.Mix(head, weights, count, mixed);
        var expected = new float[dim];
        for (var t = 0; t < count; t++)
        {
            for (var j = 0; j < dim; j++) expected[j] += weights[t] * values[t][head * dim + j];
        }
        double error = 0, size = 0;
        for (var j = 0; j < dim; j++)
        {
            error += (mixed[j] - expected[j]) * (mixed[j] - expected[j]);
            size += expected[j] * expected[j];
        }
        var mixError = Math.Sqrt(error / size);

        output.WriteLine($"{mode}, keys {form}: score correlation {correlation:F4}, mix error {mixError:F4}");
        Assert.True(correlation >= minCorrelation, $"scores correlate {correlation:F4}");
        Assert.True(mixError <= maxMixError, $"the mix is off by {mixError:F4}");
    }

    [Fact]
    public void A_compressed_state_survives_a_session()
    {
        var path = Bonsai2Model.TwoB();
        if (path is null)
        {
            output.WriteLine(Bonsai2Model.Missing);
            return;
        }

        using var file = GgufFile.Open(path);
        using var pool = new WorkerPool(Math.Max(1, Environment.ProcessorCount));
        var model = Qwen35Model.Load(file, pool);
        var live = model.NewState(64, KvCompressionMode.TurboQuant3Bit, TurboQuantForm.Prod);
        int[] prompt = [9419, 0, 10334, 525, 498, 30];
        foreach (var token in prompt) model.Forward(token, live);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true)) live.Write(writer, model.Config);
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var read = Qwen35State.Read(reader, model.Config);
        Assert.Equal(KvCompressionMode.TurboQuant3Bit, read.KvMode);
        Assert.Equal(TurboQuantForm.Prod, read.KeyForm);
        Assert.Equal(prompt.Length, read.Position);

        var fromLive = new float[model.Config.Vocabulary];
        var fromRead = new float[model.Config.Vocabulary];
        model.Forward(13, live, fromLive);
        model.Forward(13, read, fromRead);
        Assert.Equal(fromLive, fromRead);
    }

    [Fact]
    public void Each_working_memory_mode_measured_against_the_forks_tokens()
    {
        var path = Bonsai2Model.TwoB();
        if (path is null)
        {
            output.WriteLine(Bonsai2Model.Missing);
            return;
        }

        using var file = GgufFile.Open(path);
        using var pool = new WorkerPool(Math.Max(1, Environment.ProcessorCount));
        var model = Qwen35Model.Load(file, pool);
        using var fixture = Bonsai2Model.Fixture("logits.json");
        var cases = fixture.RootElement.GetProperty("cases").EnumerateArray().Take(Setting("BONSAI2_KV_CASES", 2)).ToList();
        var stepCount = Setting("BONSAI2_KV_STEPS", 8);
        var logits = new float[model.Config.Vocabulary];

        (KvCompressionMode Mode, TurboQuantForm Form)[] configurations =
        [
            (KvCompressionMode.Off, TurboQuantForm.Mse),
            (KvCompressionMode.TurboQuant4Bit, TurboQuantForm.Mse),
            (KvCompressionMode.TurboQuant4Bit, TurboQuantForm.Prod),
            (KvCompressionMode.TurboQuant3Bit, TurboQuantForm.Mse),
            (KvCompressionMode.TurboQuant3Bit, TurboQuantForm.Prod),
            (KvCompressionMode.TurboQuant2Bit, TurboQuantForm.Mse),
            (KvCompressionMode.TurboQuant2Bit, TurboQuantForm.Prod),
        ];
        // Measured 2026-11-01 over all 12 x 32 (289 steps judged): the fork's token / the largest
        // top-20 drift -
        //   Off                 289 / 0.25
        //   4 bits  Mse keys    289 / 1.05     Prod keys 286 / 2.87
        //   3 bits  Mse keys    282 / 2.57     Prod keys 280 / 4.63
        //   2 bits  Mse keys    276 / 5.35     Prod keys 270 / 7.38
        // so on this model the keys take the MSE form at every width - the second stage's noise
        // costs attention more than the first stage's bias - and 4 bits keep every fork token.
        output.WriteLine($"{cases.Count} cases x {stepCount} steps, teacher-forced on the fork's tokens");
        var agreement = new Dictionary<(KvCompressionMode, TurboQuantForm), double>();
        var drift = new Dictionary<(KvCompressionMode, TurboQuantForm), float>();
        foreach (var (mode, form) in configurations)
        {
            int agree = 0, judged = 0;
            var worst = 0f;
            foreach (var c in cases)
            {
                var prompt = c.GetProperty("prompt_ids").EnumerateArray().Select(e => e.GetInt32()).ToArray();
                var steps = c.GetProperty("steps").EnumerateArray().Take(stepCount).ToList();
                var state = model.NewState(prompt.Length + steps.Count + 1, mode, form);
                for (var i = 0; i < prompt.Length; i++) model.Forward(prompt[i], state, i == prompt.Length - 1 ? logits : default);
                for (var s = 0; s < steps.Count; s++)
                {
                    var step = steps[s];
                    var expected = step.GetProperty("token").GetInt32();
                    foreach (var pair in step.GetProperty("top").EnumerateArray())
                        worst = MathF.Max(worst, MathF.Abs(logits[pair[0].GetInt32()] - pair[1].GetSingle()));
                    if (step.GetProperty("gap").GetSingle() >= 0.3f)
                    {
                        judged++;
                        if (ArgMax(logits) == expected) agree++;
                    }
                    if (s + 1 < steps.Count) model.Forward(expected, state, logits);
                }
            }
            var share = judged == 0 ? 1.0 : (double)agree / judged;
            agreement[(mode, form)] = share;
            drift[(mode, form)] = worst;
            output.WriteLine($"{mode,-15} keys {form,-4}: the fork's token {agree} of {judged} ({share:P0}), largest top-20 drift {worst:F3}");
            Assert.All(logits, l => Assert.True(float.IsFinite(l)));
        }

        Assert.Equal(1.0, agreement[(KvCompressionMode.Off, TurboQuantForm.Mse)]);
        Assert.Equal(1.0, agreement[(KvCompressionMode.TurboQuant4Bit, TurboQuantForm.Mse)]); // the default
        foreach (var mode in new[] { KvCompressionMode.TurboQuant4Bit, KvCompressionMode.TurboQuant3Bit, KvCompressionMode.TurboQuant2Bit })
        {
            Assert.True(agreement[(mode, TurboQuantForm.Mse)] >= agreement[(mode, TurboQuantForm.Prod)], $"{mode}: Prod keys agreed more often");
            Assert.True(drift[(mode, TurboQuantForm.Mse)] < drift[(mode, TurboQuantForm.Prod)], $"{mode}: Prod keys drifted less");
        }
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
