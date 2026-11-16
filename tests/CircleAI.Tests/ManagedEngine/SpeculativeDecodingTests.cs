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
using CircleAI.Inference.Managed;
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

    private static T WithModel<T>(byte[] bytes, int threads, Func<LanguageModel, T> use) => GgufBuilder.WithFile(bytes, path =>
    {
        using var file = GgufFile.Open(path);
        using var pool = new WorkerPool(threads);
        return use(LanguageModel.Load(file, pool));
    });

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(11)]
    public void Tokens_through_the_lanes_give_each_its_own_logits_to_the_bit(int n)
    {
        // PAIRED BY THIS MODEL'S OWN OVERRIDE, NOT THE GLOBAL SWITCH. LanguageModel.PairedForTest flipped here paired every
        // model the test classes running beside this one loaded in that moment - generator tests among them, whose paired
        // models wrote stores, opened streams and looked for drafters (2026-11-09: ten of them failed in one full run).
        GgufBuilder.WithFile(SyntheticQwen35.Build(GgmlType.PTQ1_0, seed: 3), path =>
        {
            using var file = GgufFile.Open(path);
            using var pool = new WorkerPool(3);
            var m = LanguageModel.Load(file, pool, headFromStore: false, ffnFromStore: false, pairedOverride: true);
            Assert.True(m.Paired);
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
    private static (List<int> Tokens, ModelState State) Plain(LanguageModel m, Func<Func<float[], List<int>, int>> draw, int count)
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
    private static (List<int> Tokens, ModelState State, Speculator Speculator) Speculative(
        LanguageModel m, IDraftSource source, int drafts, Func<Func<float[], List<int>, int>> draw, int count)
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

    [Fact]
    public void A_streamed_head_is_held_in_RAM_for_the_turn_only_when_drafting_and_it_fits_beside_the_reserve()
    {
        // THE SERVE-FROM-STORE LEVER (the P30 measurement's open optimisation). A streamed trunk re-reads its head
        // surface from flash on every pass; an MTP head drafting does that on every draft step and verify - 5.4 GB of
        // flash for 24 tokens at 2 drafts on the P30. So the surface is held in RAM for the turn instead, but only when
        // a drafter is running AND it fits beside what the phone is owed. The served rows are identical whether held or
        // streamed (HeadStoreTests / HeadStreamTests), so NO reply-matching test can catch a revert here - this one
        // pins the decision itself so it cannot quietly fall back to per-step flash reads while every output stays green.
        const long reserve = 384L << 20;   // DrafterReserve: kept for the phone
        const long surface = 159L << 20;   // ~the 9B's Latin head surface

        // Drafting and the surface fits beside the reserve -> held in RAM.
        Assert.True(ManagedChatGenerator.HoldStreamedHeadInRam(drafting: true, availableBytes: reserve + surface, reserveBytes: reserve, surfaceBytes: surface));
        Assert.True(ManagedChatGenerator.HoldStreamedHeadInRam(drafting: true, availableBytes: reserve + surface + (128L << 20), reserveBytes: reserve, surfaceBytes: surface));

        // Drafting, but holding it would bite into the phone's reserve by a single byte -> streamed.
        Assert.False(ManagedChatGenerator.HoldStreamedHeadInRam(drafting: true, availableBytes: reserve + surface - 1, reserveBytes: reserve, surfaceBytes: surface));

        // No drafter -> streamed however much room there is (a plain turn reads the head once a token, not many times).
        Assert.False(ManagedChatGenerator.HoldStreamedHeadInRam(drafting: false, availableBytes: long.MaxValue / 2, reserveBytes: reserve, surfaceBytes: surface));

        // Memory could not be read -> streamed; never gamble the phone's RAM on an unknown.
        Assert.False(ManagedChatGenerator.HoldStreamedHeadInRam(drafting: true, availableBytes: null, reserveBytes: reserve, surfaceBytes: surface));
    }

    [Fact]
    public void FindMtpHead_picks_a_matching_sibling_in_the_model_folders_beside_the_trunk()
    {
        // The real layout: each model id in its own folder under one store root (BundleModelLoader), so the head sits in a
        // SIBLING folder, not beside the trunk file. The scan must reach it there.
        var root = Path.Combine(Path.GetTempPath(), "mtp-find-" + Guid.NewGuid().ToString("N"));
        var trunkDir = Path.Combine(root, "Ternary-Bonsai-2-9B");
        var mtpDir = Path.Combine(root, "Ternary-Bonsai-2-9B-PQ2_0-MTP");
        Directory.CreateDirectory(trunkDir);
        Directory.CreateDirectory(mtpDir);
        try
        {
            var trunkPath = Path.Combine(trunkDir, "Ternary-Bonsai-2-9B-PTQ1_0.gguf");
            var mtpPath = Path.Combine(mtpDir, "Ternary-Bonsai-2-9B-PQ2_0-MTP-Q8_0.gguf");
            File.WriteAllBytes(trunkPath, SyntheticQwen35.Build(GgmlType.PTQ1_0, seed: 4, mtp: false));
            File.WriteAllBytes(mtpPath, SyntheticQwen35.Build(GgmlType.PQ2_0, seed: 4, mtp: true));
            using var trunkFile = GgufFile.Open(trunkPath);

            // The trunk's own folder holds only the trunk; the head is one folder over, and the scan finds it there.
            Assert.Equal(mtpPath, ManagedChatGenerator.FindMtpHead(trunkPath, trunkFile, new ManagedGeneratorOptions()));
            // An empty override turns the scan off; a named one is taken as given.
            Assert.Null(ManagedChatGenerator.FindMtpHead(trunkPath, trunkFile, new ManagedGeneratorOptions { MtpHeadPath = "" }));
            Assert.Equal(mtpPath, ManagedChatGenerator.FindMtpHead(trunkPath, trunkFile, new ManagedGeneratorOptions { MtpHeadPath = mtpPath }));

            // A trunk that already carries its own head borrows none.
            using var withHead = GgufFile.Open(mtpPath);
            Assert.Null(ManagedChatGenerator.FindMtpHead(mtpPath, withHead, new ManagedGeneratorOptions()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindMtpHead_also_finds_a_head_sitting_beside_the_trunk_file()
    {
        // The flat layout (both files in one folder) still resolves - the trunk's own folder is scanned first.
        var dir = Path.Combine(Path.GetTempPath(), "mtp-flat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var trunkPath = Path.Combine(dir, "Ternary-Bonsai-2-9B-PTQ1_0.gguf");
            var mtpPath = Path.Combine(dir, "Ternary-Bonsai-2-9B-PQ2_0-MTP-Q8_0.gguf");
            File.WriteAllBytes(trunkPath, SyntheticQwen35.Build(GgmlType.PTQ1_0, seed: 4, mtp: false));
            File.WriteAllBytes(mtpPath, SyntheticQwen35.Build(GgmlType.PQ2_0, seed: 4, mtp: true));
            using var trunkFile = GgufFile.Open(trunkPath);
            Assert.Equal(mtpPath, ManagedChatGenerator.FindMtpHead(trunkPath, trunkFile, new ManagedGeneratorOptions()));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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
    public void An_MTP_head_attached_from_a_sibling_file_drafts_and_changes_nothing()
    {
        // The same seed, so the two files hold the SAME trunk - the head is appended after it (as the 9B's PQ2_0-MTP file
        // packs the PTQ1_0 trunk's weights plus a head). So a trunk whose own file carries none can borrow the sibling's.
        const int count = 20;
        var trunk = SyntheticQwen35.Build(GgmlType.PQ2_0, seed: 4, mtp: false);
        var sibling = SyntheticQwen35.Build(GgmlType.PQ2_0, seed: 4, mtp: true);
        GgufBuilder.WithFile(sibling, siblingPath =>
        {
            using var siblingFile = GgufFile.Open(siblingPath);
            return WithModel(trunk, threads: 2, m =>
            {
                Assert.False(m.HasMtp);
                m.AttachMtp(siblingFile);
                Assert.True(m.HasMtp);

                var (plain, _) = Plain(m, Greedy, count);
                var (spec, _, speculator) = Speculative(m, new MtpDrafter(m, 64), drafts: 2, Greedy, count);
                Assert.Equal(plain, spec);
                Assert.True(speculator.Drafted > 0);
                output.WriteLine($"attached MTP head: {speculator.Accepted} of {speculator.Drafted} drafts kept (random weights)");
                return 0;
            });
        });
    }

    [Fact]
    public void Attaching_an_MTP_head_is_refused_when_one_is_present_or_the_shape_differs()
    {
        var withHead = SyntheticQwen35.Build(GgmlType.PQ2_0, seed: 4, mtp: true);
        GgufBuilder.WithFile(withHead, headPath =>
        {
            using var headFile = GgufFile.Open(headPath);
            WithModel(withHead, threads: 1, m =>
            {
                Assert.True(m.HasMtp);
                Assert.Throws<InvalidOperationException>(() => m.AttachMtp(headFile)); // one already present
                return 0;
            });

            var shaped = SyntheticQwen35.Build(GgmlType.PQ2_0, seed: 4, mtp: true, layers: 4); // a different-sized trunk
            GgufBuilder.WithFile(shaped, shapedPath =>
            {
                using var shapedFile = GgufFile.Open(shapedPath);
                return WithModel(SyntheticQwen35.Build(GgmlType.PQ2_0, seed: 4, mtp: false), threads: 1, m =>
                {
                    Assert.False(m.HasMtp);
                    Assert.Throws<ArgumentException>(() => m.AttachMtp(shapedFile)); // its head would not fit this trunk
                    Assert.False(m.HasMtp);
                    return 0;
                });
            });
            return 0;
        });
    }

    [Fact]
    public void The_MTP_head_sees_a_batched_prompt_as_it_sees_the_tokens_one_at_a_time()
    {
        // The prompt read feeds a streamed trunk's whole batch to the head at once (Speculator.Observed); the decode feeds
        // it a token at a time (Speculator.Run). The head's cache - and so its drafts - must come out the same either way.
        WithModel(SyntheticQwen35.Build(GgmlType.PQ2_0, seed: 4, mtp: true), threads: 2, m =>
        {
            var e = m.Config.Embedding;

            var one = new MtpDrafter(m, 64);
            var seq = m.NewState(64);
            var logits = new float[m.Config.Vocabulary];
            var h = new float[e];
            for (var i = 0; i < Prompt.Length; i++)
            {
                var at = seq.Position;
                m.Forward(Prompt[i], seq, logits, h);
                one.Observe([Prompt[i]], at, [(float[])h.Clone()]);
            }

            var many = new MtpDrafter(m, 64);
            var batch = m.NewState(64);
            var hidden = new float[Prompt.Length][];
            for (var i = 0; i < hidden.Length; i++) hidden[i] = new float[e];
            m.ForwardBatch(Prompt, batch, new float[]?[Prompt.Length], hidden);
            many.Observe(Prompt, 0, hidden);

            var next = Prompt[^1];
            var a = new int[4];
            var b = new int[4];
            var na = one.Draft(next, Prompt.Length, a);
            var nb = many.Draft(next, Prompt.Length, b);
            Assert.True(na > 0);
            Assert.Equal(na, nb);
            Assert.Equal(a, b);
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
        var m = LanguageModel.Load(file, pool);
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
