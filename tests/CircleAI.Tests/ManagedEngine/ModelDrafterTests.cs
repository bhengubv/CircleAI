// ModelDrafterTests.cs
//
// A smaller model drafting for a bigger one (the 2B for the 27B, streamed from flash on the P30), and the rollback
// that makes it pay for a streamed trunk: a turned-down draft is stepped back by replaying the recurrence the check
// recorded, not by a second pass over the weights. The reply is the trunk's own, token for token, whoever drafts; the
// replayed state is the state of running the kept tokens, bit for bit; a check costs one pass; and the drafter catches
// up on a new conversation from its primed start.

using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Runtime;
using CircleAI.Inference.Managed.Sampling;
using Xunit;

namespace CircleAI.Tests;

public sealed class ModelDrafterTests
{
    private static readonly int[] Prompt = [1, 5, 9, 3, 30, 12, 7];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_reply_drafted_by_another_model_is_the_trunks_own_token_for_token(bool sampled)
    {
        const int count = 24;
        WithModels(SyntheticQwen35.Build(GgmlType.PTQ1_0, seed: 9), SyntheticQwen35.Build(GgmlType.PTQ1_0, seed: 11), (trunk, small) =>
        {
            var draw = sampled ? Seeded(5) : Greedy;
            var plain = Plain(trunk, draw, count);
            var drafter = new ModelDrafter(small, 64);
            var (drafted, speculator) = Speculative(trunk, drafter, drafts: 4, draw, count);

            Assert.Equal(plain, drafted);
            Assert.True(speculator.Drafted > 0, "nothing was drafted");
        });
    }

    // 17 tokens: the first from the prompt, then four checks of three drafts and the token after them - so the reply ends
    // on a check's last token, and no draft is left unconfirmed by the reply's end.
    [Fact]
    public void A_drafter_that_is_the_trunk_has_every_greedy_draft_kept()
    {
        const int count = 17;
        var bytes = SyntheticQwen35.Build(GgmlType.PTQ1_0, seed: 9);
        WithModels(bytes, bytes, (trunk, same) =>
        {
            var (drafted, speculator) = Speculative(trunk, new ModelDrafter(same, 64), drafts: 3, Greedy, count);

            Assert.Equal(Plain(trunk, Greedy, count), drafted);
            Assert.True(speculator.Drafted > 0);
            Assert.Equal(speculator.Drafted, speculator.Accepted);
        });
    }

    // The check of [token, d1, d2, d3] with d2 wrong keeps [token, d1]: the trunk runs those four tokens once, and the
    // two it keeps are not run again.
    [Fact]
    public void A_turned_down_draft_costs_no_second_pass_over_the_trunk()
    {
        WithModel(SyntheticQwen35.Build(GgmlType.PTQ1_0, seed: 9), trunk =>
        {
            var truth = Prompt.Concat(Plain(trunk, Greedy, 8)).ToList();
            var source = new Scripted(truth, trunk.Config.Vocabulary, wrongAt: 1);
            var speculator = new Speculator(trunk, source, drafts: 3);
            var state = trunk.NewState(64);
            var logits = new float[trunk.Config.Vocabulary];
            for (var i = 0; i < Prompt.Length; i++) speculator.Run(Prompt[i], state, i == Prompt.Length - 1 ? logits : default);
            var history = new List<int>(Prompt);
            trunk.TakeTimes();

            var first = ArgMax(logits);
            speculator.Step(first, state, history, l => (ArgMax(l), false));

            Assert.Equal(4, trunk.TakeTimes().Tokens);
            Assert.Equal(Prompt.Length + 2, history.Count);
            Assert.Equal(history.Count, state.Position);
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void The_replayed_recurrence_is_the_state_of_running_the_kept_tokens(int kept)
    {
        WithModel(SyntheticQwen35.Build(GgmlType.PTQ1_0, seed: 13), trunk =>
        {
            int[] batch = [4, 19, 7, 22, 2];
            var checkedState = trunk.NewState(64);
            var plainState = trunk.NewState(64);
            foreach (var token in Prompt)
            {
                trunk.Forward(token, checkedState);
                trunk.Forward(token, plainState);
            }

            // The checked batch steps the recurrence aside; the kept tokens step the state after it.
            var position = checkedState.Position;
            var rope = checkedState.RopePosition;
            trunk.RecordRecurrence(batch.Length);
            trunk.ForwardBatch(batch, checkedState, new float[batch.Length][]);
            trunk.StopRecording();
            trunk.ReplayRecurrence(checkedState, kept, position, rope);

            trunk.ForwardBatch(batch.AsSpan(0, kept), plainState, new float[kept][]);

            Assert.Equal(plainState.Position, checkedState.Position);
            Assert.Equal(plainState.RopePosition, checkedState.RopePosition);
            var a = new float[trunk.Config.Vocabulary];
            var b = new float[trunk.Config.Vocabulary];
            trunk.Forward(25, plainState, a);
            trunk.Forward(25, checkedState, b);
            Assert.Equal(a.Select(BitConverter.SingleToInt32Bits), b.Select(BitConverter.SingleToInt32Bits));
        });
    }

    // A new question with the same system turn: the drafter goes back to its primed start and reads only the new turn -
    // and then drafts what a drafter that read the new conversation from the start drafts.
    [Fact]
    public void The_drafter_catches_up_on_a_new_conversation_from_its_primed_start()
    {
        var bytes = SyntheticQwen35.Build(GgmlType.PTQ1_0, seed: 11);
        WithModels(bytes, bytes, (small, fresh) =>
        {
            int[] system = [1, 5, 9, 3];
            var first = system.Concat(new[] { 30, 12, 7, 8 }).ToList();
            var second = system.Concat(new[] { 14, 2, 27 }).ToList();

            var drafter = new ModelDrafter(small, 64);
            drafter.Sync(first, primedAt: system.Length);
            Assert.Equal(first.Count, drafter.Position);
            drafter.Sync(second, primedAt: system.Length);
            Assert.Equal(second.Count, drafter.Position);

            var reference = new ModelDrafter(fresh, 64);
            reference.Sync(second, primedAt: system.Length);

            var a = new int[4];
            var b = new int[4];
            Assert.Equal(4, drafter.Draft(20, second.Count, a));
            Assert.Equal(4, reference.Draft(20, second.Count, b));
            Assert.Equal(b, a);
        });
    }

    // The catalogue keeps each model in a folder of its own: the drafter is the smaller qwen35 file with the same vocabulary
    // in a folder beside the trunk's; nothing is smaller than the smallest; an empty path turns it off.
    [Fact]
    public void The_drafter_is_the_smaller_model_with_the_same_vocabulary_beside_this_one()
    {
        var root = Directory.CreateTempSubdirectory("models-").FullName;
        try
        {
            var big = Path.Combine(root, "Big", "big.gguf");
            var small = Path.Combine(root, "Small", "small.gguf");
            var tower = Path.Combine(root, "Small", "small-mmproj.gguf");
            Directory.CreateDirectory(Path.GetDirectoryName(big)!);
            Directory.CreateDirectory(Path.GetDirectoryName(small)!);
            File.WriteAllBytes(big, SyntheticQwen35.Build(GgmlType.PTQ1_0, seed: 9, layers: 4));
            File.WriteAllBytes(small, SyntheticQwen35.Build(GgmlType.PTQ1_0, seed: 11, layers: 2));
            File.WriteAllBytes(tower, SyntheticQwen35.Build(GgmlType.PTQ1_0, seed: 12, layers: 2)); // a tower's name: never a drafter

            using (var file = GgufFile.Open(big))
            {
                Assert.Equal(Path.GetFullPath(small), CircleAI.Inference.Managed.ManagedChatGenerator.FindDrafter(big, file, new CircleAI.Inference.Managed.ManagedGeneratorOptions()));
                Assert.Null(CircleAI.Inference.Managed.ManagedChatGenerator.FindDrafter(big, file, new CircleAI.Inference.Managed.ManagedGeneratorOptions { DrafterPath = "" }));
            }
            using (var file = GgufFile.Open(small))
                Assert.Null(CircleAI.Inference.Managed.ManagedChatGenerator.FindDrafter(small, file, new CircleAI.Inference.Managed.ManagedGeneratorOptions()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Plain decoding: the prompt, then `count` tokens drawn by a fresh `draw`, each fed back but the last.
    private static List<int> Plain(Qwen35Model m, Func<Func<float[], List<int>, int>> draw, int count)
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
        return generated;
    }

    // The same through the speculator.
    private static (List<int> Tokens, Speculator Speculator) Speculative(
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
        return (generated, speculator);
    }

    // Proposes plain decoding's own next tokens, wrong at one draft of every check.
    private sealed class Scripted(IReadOnlyList<int> truth, int vocabulary, int wrongAt) : IDraftSource
    {
        public void Observe(ReadOnlySpan<int> tokens, int position, IReadOnlyList<float[]> hidden) { }

        public int Draft(int token, int position, Span<int> drafts)
        {
            for (var i = 0; i < drafts.Length; i++)
            {
                var at = position + 1 + i;
                var right = at < truth.Count ? truth[at] : 0;
                drafts[i] = i == wrongAt ? (right + 1) % vocabulary : right;
            }
            return drafts.Length;
        }

        public void Forget() { }
    }

    private static Func<Func<float[], List<int>, int>> Greedy => () => (logits, _) => ArgMax(logits);

    private static Func<Func<float[], List<int>, int>> Seeded(int seed) => () =>
    {
        var sampler = new TokenSampler(SamplingSettings.Direct, seed);
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

    private static void WithModel(byte[] bytes, Action<Qwen35Model> use) =>
        GgufBuilder.WithFile(bytes, path =>
        {
            using var file = GgufFile.Open(path);
            using var pool = new WorkerPool(2);
            use(Qwen35Model.Load(file, pool));
            return 0;
        });

    private static void WithModels(byte[] trunkBytes, byte[] smallBytes, Action<Qwen35Model, Qwen35Model> use) =>
        GgufBuilder.WithFile(trunkBytes, trunkPath => GgufBuilder.WithFile(smallBytes, smallPath =>
        {
            using var trunkFile = GgufFile.Open(trunkPath);
            using var smallFile = GgufFile.Open(smallPath);
            using var pool = new WorkerPool(2);
            use(Qwen35Model.Load(trunkFile, pool), Qwen35Model.Load(smallFile, pool));
            return 0;
        }));
}
