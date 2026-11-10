// HeadStreamTests.cs
//
// MAKING ROOM FOR THE DRAFTER BESIDE THE STREAMED 27B ON THE P30, 2026-11-09. The 27B's worker held 870 MB: its ring of
// layers, its head's surface (198 MB of Latin rows), its live state and a primed start's copy of it (151 MB each). A head
// read from flash as each pass scores it holds two windows; a state read back from flash into itself holds no copy.
//
// A head read as it is scored gives the surface's logits to the bit, pass after pass, while its windows turn over; a
// checked batch reads it once, not once a position; a head in one window is read once and held. A state read into the
// one it replaces goes on as a fresh read does, and keeps none of the copies it had.

using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Runtime;
using CircleAI.Inference.Managed.Store;
using Xunit;

namespace CircleAI.Tests;

public sealed class HeadStreamTests
{
    // The synthetic model's 32 tokens by number, as HeadStoreTests places them.
    private static Script ScriptOf(int token) => token < 2
        ? Script.Special
        : (token % 4) switch { 0 => Script.Latin, 1 => Script.Cyrillic, 2 => Script.Cjk, _ => Script.Neutral };

    private static readonly Script[] InLatinSurface = [Script.Special, Script.Neutral, Script.Latin];

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void A_head_read_as_it_is_scored_gives_the_surfaces_logits_to_the_bit_pass_after_pass(bool paired, bool pooled)
    {
        WithStore(paired, 29, (file, store) =>
        {
            using var pool = pooled ? new WorkerPool(2) : null;
            var held = LanguageModel.Load(file, pool, headFromStore: false, pairedOverride: paired);
            var read = LanguageModel.Load(file, pool, headFromStore: false, pairedOverride: paired);
            var groups = store.Surface([Script.Latin]);
            using var surface = store.Load(groups, channels: 2);
            using var stream = store.Stream(groups, channels: 2, readBytes: FlashFile.Page, windowBytes: FlashFile.Page);
            Assert.True(stream.Windows >= 3, $"{stream.Windows} windows: too few to turn over");
            Assert.Equal(surface.Rows, stream.Rows);
            held.Head = surface;
            read.Head = stream;

            var a = held.NewState(32);
            var b = read.NewState(32);
            var fromHeld = new float[SyntheticQwen35.Vocabulary];
            var fromRead = new float[SyntheticQwen35.Vocabulary];
            foreach (var token in new[] { 4, 9, 30, 2, 17, 25, 8, 12 })
            {
                held.Forward(token, a, fromHeld);
                read.Forward(token, b, fromRead);
                Assert.Equal(fromHeld.Select(BitConverter.SingleToInt32Bits), fromRead.Select(BitConverter.SingleToInt32Bits));
            }
            for (var t = 0; t < SyntheticQwen35.Vocabulary; t++)
            {
                if (!InLatinSurface.Contains(ScriptOf(t))) Assert.Equal(float.NegativeInfinity, fromRead[t]);
            }

            // A checked batch: every position scored on each window in hand, each position's logits its own.
            int[] tokens = [3, 8, 12, 20, 6];
            var heldLogits = tokens.Select(_ => new float[SyntheticQwen35.Vocabulary]).ToArray();
            var readLogits = tokens.Select(_ => new float[SyntheticQwen35.Vocabulary]).ToArray();
            held.ForwardBatch(tokens, a, heldLogits);
            read.ForwardBatch(tokens, b, readLogits);
            for (var i = 0; i < tokens.Length; i++)
                Assert.Equal(heldLogits[i].Select(BitConverter.SingleToInt32Bits), readLogits[i].Select(BitConverter.SingleToInt32Bits));
        });
    }

    [Fact]
    public void A_head_held_whole_scores_a_checked_batch_as_each_position_alone()
    {
        WithStore(paired: true, 31, (file, store) =>
        {
            var batch = LanguageModel.Load(file, null, headFromStore: true, pairedOverride: true);
            var alone = LanguageModel.Load(file, null, headFromStore: true, pairedOverride: true);
            using var surface = store.Load(store.Surface([Script.Latin]), channels: 2);
            batch.Head = surface;
            alone.Head = surface;

            int[] tokens = [3, 8, 12, 20, 6, 11];
            var together = tokens.Select(_ => new float[SyntheticQwen35.Vocabulary]).ToArray();
            batch.ForwardBatch(tokens, batch.NewState(16), together);
            var state = alone.NewState(16);
            var one = new float[SyntheticQwen35.Vocabulary];
            for (var i = 0; i < tokens.Length; i++)
            {
                alone.Forward(tokens[i], state, one);
                Assert.Equal(one.Select(BitConverter.SingleToInt32Bits), together[i].Select(BitConverter.SingleToInt32Bits));
            }
        });
    }

    [Fact]
    public void A_checked_batch_reads_the_streamed_head_once_not_once_a_position()
    {
        WithStore(paired: true, 37, (file, store) =>
        {
            var model = LanguageModel.Load(file, null, headFromStore: true, pairedOverride: true);
            using var stream = store.Stream(store.Surface([Script.Latin]), channels: 2, readBytes: FlashFile.Page, windowBytes: FlashFile.Page);
            model.Head = stream;

            int[] tokens = [3, 8, 12, 20, 6];
            model.ForwardBatch(tokens, model.NewState(16), tokens.Select(_ => new float[SyntheticQwen35.Vocabulary]).ToArray());
            var (read, _) = stream.TakeCounts();

            // One pass over the surface, and at most the next pass's first window on its way - not five passes.
            Assert.InRange(read, stream.SurfaceBytes, 2 * stream.SurfaceBytes);
        });
    }

    [Fact]
    public void A_head_in_one_window_is_read_once_and_held()
    {
        WithStore(paired: true, 41, (file, store) =>
        {
            var model = LanguageModel.Load(file, null, headFromStore: true, pairedOverride: true);
            using var stream = store.Stream(store.Surface([Script.Latin]), channels: 2, readBytes: 1 << 20);
            Assert.Equal(1, stream.Windows);
            Assert.True(stream.Bytes <= 2 * stream.SurfaceBytes);
            model.Head = stream;

            var state = model.NewState(16);
            var logits = new float[SyntheticQwen35.Vocabulary];
            foreach (var token in new[] { 4, 9, 30, 2 }) model.Forward(token, state, logits);
            Assert.Equal(stream.SurfaceBytes, stream.TakeCounts().Read);
        });
    }

    [Fact]
    public void A_state_read_into_the_one_it_replaces_goes_on_as_a_fresh_read_and_keeps_no_copies()
    {
        WithStore(paired: true, 43, (file, _) =>
        {
            var model = LanguageModel.Load(file, null, headFromStore: false, pairedOverride: true);
            var live = model.NewState(16);
            foreach (var token in new[] { 5, 9, 14, 22 }) model.Forward(token, live);
            using var written = new MemoryStream();
            using (var writer = new BinaryWriter(written, System.Text.Encoding.UTF8, leaveOpen: true)) live.Write(writer, model.Config);

            // The state it goes into holds another conversation, a primed start and a mark.
            var into = model.NewState(16);
            foreach (var token in new[] { 7, 3 }) model.Forward(token, into);
            into.MarkPrimed();
            model.Forward(11, into);
            into.Mark();

            written.Position = 0;
            using var intoReader = new BinaryReader(written, System.Text.Encoding.UTF8, leaveOpen: true);
            var read = ModelState.Read(intoReader, model.Config, into);
            Assert.Same(into, read);
            Assert.Equal(4, read.Position);
            Assert.Equal(-1, read.PrimedPosition);
            Assert.Equal(-1, read.MarkedPosition);

            written.Position = 0;
            using var freshReader = new BinaryReader(written, System.Text.Encoding.UTF8, leaveOpen: true);
            var fresh = ModelState.Read(freshReader, model.Config);
            Assert.NotSame(into, fresh);
            var fromInto = new float[SyntheticQwen35.Vocabulary];
            var fromFresh = new float[SyntheticQwen35.Vocabulary];
            foreach (var token in new[] { 13, 26 })
            {
                model.Forward(token, read, fromInto);
                model.Forward(token, fresh, fromFresh);
                Assert.Equal(fromFresh.Select(BitConverter.SingleToInt32Bits), fromInto.Select(BitConverter.SingleToInt32Bits));
            }

            // Of another capacity, it is read into a state of its own.
            written.Position = 0;
            using var otherReader = new BinaryReader(written, System.Text.Encoding.UTF8, leaveOpen: true);
            var other = model.NewState(32);
            Assert.NotSame(other, ModelState.Read(otherReader, model.Config, other));

            // Its copies let go: nothing to go back to, until a point is kept again.
            read.MarkPrimed();
            read.ForgetCopies();
            Assert.Equal(-1, read.PrimedPosition);
            Assert.Throws<InvalidOperationException>(() => read.RewindToPrimed());
            read.MarkPrimed();
            Assert.Equal(read.Position, read.PrimedPosition);
            read.RewindToPrimed();
        });
    }

    private static void WithStore(bool paired, int seed, Action<GgufFile, HeadStore> use)
    {
        var directory = Directory.CreateTempSubdirectory("head-stream-").FullName;
        try
        {
            GgufBuilder.WithFile(SyntheticQwen35.Build(GgmlType.PTQ1_0, seed), path =>
            {
                using var file = GgufFile.Open(path);
                var store = paired
                    ? HeadStore.Write(file, Path.Combine(directory, "model.head"), ScriptOf, chunkRows: 16, layout: HeadLayout.Paired)
                    : HeadStore.Write(file, Path.Combine(directory, "model.head"), ScriptOf, chunkRows: 3);
                use(file, store);
                return 0;
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
