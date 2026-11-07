// SpeculativeDecodingTests.cs
//
// S5: SPECULATIVE DECODING THAT CHANGES NOTHING BUT THE SPEED. The trunk checks several drafted
// tokens in one batched pass and keeps exactly what plain decoding would have produced; drafts it
// rejects are taken back out of the Gated DeltaNet layers from a checkpoint.
//   - A batch of tokens gives each token's own logits and hidden state to the bit (both packings,
//     fp16 and TurboQuant working memory), and leaves the same state behind.
//   - With a draft source scripted to be right, half right and wrong, the reply is the plain reply
//     token for token - greedy and sampled with a seed - and the state afterwards is the plain
//     state: the next token's logits agree to the bit.
//   - Over many seeds, what sampling with drafts draws at each position is what plain sampling
//     draws there (the plan's distribution check), and the draws vary, so it is a distribution.
//   - The MTP head of a PQ2_0-MTP file loads, drafts, and changes nothing either.
//   - On the real 4B PQ2_0-MTP file (opt-in), the head's drafts are kept often enough to matter and
//     the reply is still the plain one.

using System.Diagnostics;
using CircleAI.Core;
using CircleAI.Core.Compression;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Runtime;
using CircleAI.Inference.Managed.Sampling;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public sealed class SpeculativeDecodingTests(ITestOutputHelper output)
{
    private static readonly int[] Prompt = [1, 5, 9, 3, 30, 12, 7];

    private static void Same(ReadOnlySpan<float> expected, ReadOnlySpan<float> actual, string what)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.True(BitConverter.SingleToInt32Bits(expected[i]) == BitConverter.SingleToInt32Bits(actual[i]),
                $"{what}[{i}]: {expected[i]} against {actual[i]}");
    }

    private static T WithModel<T>(byte[] bytes, int threads, Func<Qwen35Model, T> use) => GgufBuilder.WithFile(bytes, path =>
    {
        using var file = GgufFile.Open(path);
        using var pool = new WorkerPool(threads);
        return use(Qwen35Model.Load(file, pool));
    });

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(11)]
    public void Tokens_through_the_lanes_give_each_its_own_logits_to_the_bit(int n)
    {
        Qwen35Model.PairedForTest = true;
        try
        {
            WithModel(SyntheticQwen35.Build(GgmlType.PTQ1_0, seed: 3), threads: 3, m =>
            {
                var v = m.Config.Vocabulary;
                var batch = Enumerable.Range(0, n).Select(i => (i * 5 + 1) % v).ToArray();
                var one = m.NewState(32, KvCompressionMode.Off, TurboQuantForm.Mse);
                var together = m.NewState(32, KvCompressionMode.Off, TurboQuantForm.Mse);

                var want = batch.Select(t =>
                {
                    var logits = new float[v];
                    m.Forward(t, one, logits);
                    return logits;
                }).ToArray();
                var got = batch.Select(_ => new float[v]).ToArray();
                m.ForwardBatch(batch, together, got);

                for (var i = 0; i < n; i++) Assert.Equal(want[i], got[i]);
                return 0;
            });
        }
        finally
        {
            Qwen35Model.PairedForTest = false;
        }
    }

    [Theory]
    [InlineData(GgmlType.PQ2_0, KvCompressionMode.Off)]
    [InlineData(GgmlType.PTQ1_0, KvCompressionMode.Off)]
    [InlineData(GgmlType.PQ2_0, KvCompressionMode.TurboQuant4Bit)]
    public void Tokens_run_together_give_each_its_own_logits_to_the_bit(GgmlType packing, KvCompressionMode mode)
    {
        WithModel(SyntheticQwen35.Build(packing, seed: 3), threads: 3, m =>
        {
            var v = m.Config.Vocabulary;
            var e = m.Config.Embedding;
            var one = m.NewState(32, mode, TurboQuantForm.Mse);
            var together = m.NewState(32, mode, TurboQuantForm.Mse);
            foreach (var t in Prompt[..3])
            {
                m.Forward(t, one);
                m.Forward(t, together);
            }

            var batch = Prompt[3..];
            var logits = batch.Select(_ => new float[v]).ToArray();
            var hidden = batch.Select(_ => new float[e]).ToArray();
            m.ForwardBatch(batch, together, logits, hidden);

            for (var i = 0; i < batch.Length; i++)
            {
                var l = new float[v];
                var h = new float[e];
                m.Forward(batch[i], one, l, h);
                Same(l, logits[i], $"logits of token {i}");
                Same(h, hidden[i], $"h_nextn of token {i}");
            }
            Assert.Equal(one.Position, together.Position);

            var after1 = new float[v];
            var after2 = new float[v];
            m.Forward(2, one, after1);
            m.Forward(2, together, after2);
            Same(after1, after2, "the next token's logits");
            return 0;
        });
    }

    // Proposes plain decoding's own next tokens - except where told to be wrong - so a test chooses
    // which drafts the trunk keeps.
    private sealed class Scripted(IReadOnlyList<int> truth, int vocabulary, Func<int, int, bool> wrong) : IDraftSource
    {
        public int Observed { get; private set; }

        public void Observe(ReadOnlySpan<int> tokens, int position, IReadOnlyList<float[]> hidden) => Observed += tokens.Length;

        public int Draft(int token, int position, Span<int> drafts)
        {
            for (var i = 0; i < drafts.Length; i++)
            {
                var at = position + 1 + i;
                var right = at < truth.Count ? truth[at] : 0;
                drafts[i] = wrong(position, i) ? (right + 1) % vocabulary : right;
            }
            return drafts.Length;
        }

        public void Forget() { }
    }

    // Plain decoding: the prompt, then `count` tokens drawn by a fresh `draw`, each fed back but the last.
    private static (List<int> Tokens, Qwen35State State) Plain(Qwen35Model m, Func<Func<float[], List<int>, int>> draw, int count)
    {
        var state = m.NewState(64);
        var logits = new float[m.Config.Vocabulary];
        for (var i = 0; i < Prompt.Length; i++) m.Forward(Prompt[i], state, i == Prompt.Length - 1 ? logits : default);
        var pick = draw();
        var generated = new List<int>();
        while (true)
        {
            var token = pick(logits, generated);
            generated.Add(token);
            if (generated.Count == count) break;
            m.Forward(token, state, logits);
        }
        return (generated, state);
    }

    // The same through the speculator.
    private static (List<int> Tokens, Qwen35State State, Speculator Speculator) Speculative(
        Qwen35Model m, IDraftSource source, int drafts, Func<Func<float[], List<int>, int>> draw, int count)
    {
        var state = m.NewState(64);
        var speculator = new Speculator(m, source, drafts);
        var logits = new float[m.Config.Vocabulary];
        for (var i = 0; i < Prompt.Length; i++) speculator.Run(Prompt[i], state, i == Prompt.Length - 1 ? logits : default);
        var pick = draw();
        var generated = new List<int>();
        (int, bool) Step(float[] l)
        {
            var token = pick(l, generated);
            generated.Add(token);
            return (token, generated.Count == count);
        }
        var history = new List<int>(Prompt);
        var (next, end) = Step(logits);
        while (!end) (next, end) = speculator.Step(next, state, history, Step);
        Assert.Equal(state.Position, history.Count);
        return (generated, state, speculator);
    }

    private static Func<Func<float[], List<int>, int>> Greedy => () => (logits, _) => ArgMax(logits);

    private static Func<Func<float[], List<int>, int>> Seeded(int seed) => () =>
    {
        var sampler = new TokenSampler(SamplingSettings.Direct, seed);
        return (logits, generated) => sampler.Sample(logits, generated);
    };

    // Hot and uncut, for a count over seeds: at the card's settings this small model leaves one token
    // to draw at every position, every seed draws it, and there is no distribution to compare.
    private static Func<Func<float[], List<int>, int>> Hot(int seed) => () =>
    {
        var sampler = new TokenSampler(SamplingSettings.Direct with { Temperature = 2f, TopP = 1f }, seed);
        return (logits, generated) => sampler.Sample(logits, generated);
    };

    private static int ArgMax(ReadOnlySpan<float> values)
    {
        var best = 0;
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] > values[best]) best = i;
        }
        return best;
    }

    [Theory]
    [InlineData(GgmlType.PQ2_0, false)]
    [InlineData(GgmlType.PTQ1_0, false)]
    [InlineData(GgmlType.PQ2_0, true)]
    public void Drafts_right_half_right_and_wrong_leave_the_plain_reply_and_the_plain_state(GgmlType packing, bool sampled)
    {
        const int count = 24;
        WithModel(SyntheticQwen35.Build(packing, seed: 9), threads: 2, m =>
        {
            var draw = sampled ? Seeded(5) : Greedy;
            var (plain, plainState) = Plain(m, draw, count);
            var truth = Prompt.Concat(plain).ToList();

            // Right at most positions; the second draft wrong every third position; both wrong every fifth.
            var source = new Scripted(truth, m.Config.Vocabulary, (position, i) => position % 5 == 0 || (i == 1 && position % 3 == 0));
            var (spec, specState, speculator) = Speculative(m, source, drafts: 3, draw, count);

            Assert.Equal(plain, spec);
            Assert.True(speculator.Accepted > 0, "no draft was ever kept");
            Assert.True(speculator.Accepted < speculator.Drafted, "no draft was ever turned down");
            Assert.True(source.Observed >= Prompt.Length);

            Assert.Equal(plainState.Position, specState.Position);
            var a = new float[m.Config.Vocabulary];
            var b = new float[m.Config.Vocabulary];
            m.Forward(plain[^1], plainState, a);
            m.Forward(spec[^1], specState, b);
            Same(a, b, "the logits after the reply");
            output.WriteLine($"{packing}, {(sampled ? "sampled" : "greedy")}: {speculator.Accepted} of {speculator.Drafted} drafts kept");
            return 0;
        });
    }

    [Fact]
    public void Over_many_seeds_sampling_with_drafts_draws_what_plain_sampling_draws()
    {
        // THE PLAN'S DISTRIBUTION CHECK WITH SAMPLING. Each seed's drafts are right and wrong at
        // places that move with the seed; what lands at each position, counted over the seeds, must
        // be what plain sampling lands there - and the seeds must not all agree, or nothing was sampled.
        const int count = 8, seeds = 48;
        WithModel(SyntheticQwen35.Build(GgmlType.PQ2_0, seed: 9), threads: 2, m =>
        {
            var plainDraws = new Dictionary<(int Position, int Token), int>();
            var draftedDraws = new Dictionary<(int Position, int Token), int>();
            long drafted = 0, kept = 0;
            for (var seed = 1; seed <= seeds; seed++)
            {
                var (plain, _) = Plain(m, Hot(seed), count);
                var truth = Prompt.Concat(plain).ToList();
                var source = new Scripted(truth, m.Config.Vocabulary, (position, i) => (position + i + seed) % 3 == 0);
                var (spec, _, speculator) = Speculative(m, source, drafts: 2, Hot(seed), count);
                for (var p = 0; p < count; p++)
                {
                    plainDraws[(p, plain[p])] = plainDraws.GetValueOrDefault((p, plain[p])) + 1;
                    draftedDraws[(p, spec[p])] = draftedDraws.GetValueOrDefault((p, spec[p])) + 1;
                }
                drafted += speculator.Drafted;
                kept += speculator.Accepted;
            }

            static List<(int Position, int Token, int Times)> Sorted(Dictionary<(int Position, int Token), int> draws) =>
                [.. draws.OrderBy(d => d.Key).Select(d => (d.Key.Position, d.Key.Token, d.Value))];
            Assert.Equal(Sorted(plainDraws), Sorted(draftedDraws));
            Assert.True(plainDraws.Count > count, "every seed drew the same tokens: nothing was sampled");
            Assert.True(kept > 0 && kept < drafted, $"{kept} of {drafted} drafts kept: the count needs drafts kept and drafts turned down");
            output.WriteLine($"{seeds} seeds x {count} tokens: {plainDraws.Count} different draws, {kept} of {drafted} drafts kept");
            return 0;
        });
    }

    [Fact]
    public void Drafting_is_off_unless_a_host_asks_for_it()
    {
        // OPEN-GAPS C7: lossless, and slower everywhere it was measured - 0.78-0.89 tokens a second
        // against 1.60-2.08 on the desktop, and on the P30 the check pass alone 2.4 times dearer
        // than single passes. So a brain is built without it; a host that measures it faster asks.
        Assert.Equal(0, new CircleAI.Inference.Managed.ManagedGeneratorOptions().Drafts);
        var factory = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CircleAI", "Inference", "BrainFactory.cs"));
        Assert.DoesNotContain("Drafts", factory, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "CircleAI", "Inference")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new DirectoryNotFoundException("the repository root was not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void A_file_with_an_MTP_head_loads_it_and_drafting_with_it_changes_nothing()
    {
        const int count = 20;
        WithModel(SyntheticQwen35.Build(GgmlType.PQ2_0, seed: 4, mtp: true), threads: 2, m =>
        {
            Assert.True(m.HasMtp);
            Assert.Equal(SyntheticQwen35.Layers, m.Config.Layers);
            Assert.Equal(1, m.Config.MtpLayers);

            var (plain, _) = Plain(m, Greedy, count);
            var (spec, _, speculator) = Speculative(m, new MtpDrafter(m, 64), drafts: 2, Greedy, count);
            Assert.Equal(plain, spec);
            Assert.True(speculator.Drafted > 0);
            output.WriteLine($"synthetic MTP head: {speculator.Accepted} of {speculator.Drafted} drafts kept (its weights are random)");
            return 0;
        });

        WithModel(SyntheticQwen35.Build(GgmlType.PQ2_0, seed: 4), threads: 1, m =>
        {
            Assert.False(m.HasMtp);
            Assert.Throws<InvalidOperationException>(() => new MtpDrafter(m, 8));
            return 0;
        });
    }

    [Fact]
    public void The_4B_MTP_heads_drafts_are_kept_and_the_reply_is_the_plain_reply()
    {
        const string set = "4B-PQ2_0-MTP";
        var path = Bonsai2Model.Of(set);
        if (path is null)
        {
            output.WriteLine(Bonsai2Model.MissingOf(set));
            return;
        }

        // The fleet shares one tokenizer, so the 2B fixtures' rendered prompts are the 4B's too.
        using var fixture = Bonsai2Model.Fixture("logits.json");
        var cases = fixture.RootElement.GetProperty("cases").EnumerateArray()
            .Take(int.TryParse(Environment.GetEnvironmentVariable("BONSAI2_MTP_CASES"), out var n) && n > 0 ? n : 2).ToList();
        const int count = 32;

        using var file = GgufFile.Open(path);
        using var pool = new WorkerPool(Math.Max(1, Environment.ProcessorCount));
        var m = Qwen35Model.Load(file, pool);
        Assert.True(m.HasMtp);
        Assert.Equal(GgmlType.PQ2_0, m.Packing);

        long drafted = 0, accepted = 0;
        double plainSeconds = 0, specSeconds = 0;
        foreach (var c in cases)
        {
            var prompt = c.GetProperty("prompt_ids").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            var plainState = m.NewState(prompt.Length + count + 4);
            var logits = new float[m.Config.Vocabulary];
            var clock = Stopwatch.StartNew();
            for (var i = 0; i < prompt.Length; i++) m.Forward(prompt[i], plainState, i == prompt.Length - 1 ? logits : default);
            var started = clock.Elapsed;
            var plain = new List<int>();
            while (plain.Count < count)
            {
                var token = ArgMax(logits);
                plain.Add(token);
                if (plain.Count < count) m.Forward(token, plainState, logits);
            }
            plainSeconds += (clock.Elapsed - started).TotalSeconds;

            var state = m.NewState(prompt.Length + count + 4);
            var speculator = new Speculator(m, new MtpDrafter(m, state.Capacity), drafts: 2);
            for (var i = 0; i < prompt.Length; i++) speculator.Run(prompt[i], state, i == prompt.Length - 1 ? logits : default);
            clock.Restart();
            var spec = new List<int>();
            (int, bool) Step(float[] l)
            {
                var token = ArgMax(l);
                spec.Add(token);
                return (token, spec.Count == count);
            }
            var history = new List<int>(prompt);
            var (next, end) = Step(logits);
            while (!end) (next, end) = speculator.Step(next, state, history, Step);
            specSeconds += clock.Elapsed.TotalSeconds;

            Assert.Equal(plain, spec);
            drafted += speculator.Drafted;
            accepted += speculator.Accepted;
            output.WriteLine($"{c.GetProperty("name").GetString()}: {speculator.Accepted} of {speculator.Drafted} drafts kept");
        }

        output.WriteLine($"{cases.Count} prompts x {count} tokens: plain {cases.Count * count / plainSeconds:F2} tok/s, speculative {cases.Count * count / specSeconds:F2} tok/s on {pool.Threads} threads; the trunk kept {accepted} of {drafted} drafts ({100.0 * accepted / drafted:F0}%)");
        // A head computed wrongly would almost never agree with the trunk; the card's head agrees often.
        Assert.True(accepted * 10 >= drafted * 3, $"only {accepted} of {drafted} drafts were kept");
    }

    [Fact]
    public async Task Through_the_generator_the_4B_MTP_file_answers_the_same_with_its_head_drafting_or_not()
    {
        var path = Bonsai2Model.Of("4B-PQ2_0-MTP");
        if (path is null)
        {
            output.WriteLine(Bonsai2Model.MissingOf("4B-PQ2_0-MTP"));
            return;
        }

        CircleAI.Inference.ChatMessage[] question = [new("user", "Name three rivers in Africa and say where each one ends.")];
        var greedy = new CircleAI.Inference.GenerationOptions { Temperature = 0, MaxTokens = 40, Budget = CircleAI.Inference.PowerBudget.None };

        // Primed starts in memory only (an empty directory), so nothing is left on disk.
        string plain, drafted;
        using (var g = CircleAI.Inference.Managed.ManagedChatGenerator.Load(path, new() { ContextLength = 512, Drafts = 0, PrimedStartDirectory = "" }))
            plain = await g.GenerateAsync(question, greedy);
        using (var g = CircleAI.Inference.Managed.ManagedChatGenerator.Load(path, new() { ContextLength = 512, Drafts = 2, PrimedStartDirectory = "" }))
            drafted = await g.GenerateAsync(question, greedy);
        output.WriteLine(plain);
        Assert.False(string.IsNullOrWhiteSpace(plain));
        Assert.Equal(plain, drafted);
    }
}
