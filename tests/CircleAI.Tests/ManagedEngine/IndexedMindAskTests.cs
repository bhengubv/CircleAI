// IndexedMindAskTests.cs
//
// WHAT WAS ASKED OF THE INDEXED MIND, AS TESTS (2026-11-09). Written from its prompt, design and plan (CircleOS
// docs/INDEXED_MIND_PROMPT.md, INDEXED_MIND_DESIGN.md and INDEXED_MIND_PLAN.md) and the owner's own words, not from the
// engine's code. Each test quotes the ask it checks and measures it with what the engine reports: the bytes each tier read,
// the pieces each tier served, the layers a word ran, the logits. A test fails where the build does not do what was asked.
//
// What exists is the base they measure through, in the owner's steps (2026-11-15: "1) Download llm, 2) split, organise by
// thoughts, 3) create CORE index. Select coherent portions from 3 into RAM for 4) warm index"): Watch (ThoughtWatch), the
// core index - step 3 (ThoughtIndex), step 2's pieces (CachedIndex) and tiled layers (TileStore), step 4 and how it
// reaches down (IndexedFeedForward), the layers' stream (LayerStream), the head's stream (HeadStream) and the flash
// profile (FlashProfile). A source of flash reads the build adds must report through them, or these tests cannot see it.
//
// A threshold marked ASSUMED is not in the documents, which judge by the exam on the phone. Measured on the phone and not
// here: the exam's pass rate, joules per passed question, end of speech to first spoken word, skin never over 41C, no app
// killed, the profile's time and repeatability, and the heat budget's pauses.

using CircleAI.Inference.Managed;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Store;
using Xunit;

namespace CircleAI.Tests;

public sealed class IndexedMindAskTests
{
    private const int Layers = 4;
    private const int Ff = SyntheticQwen35.FeedForward; // 384 neurons a layer
    private const int Quarter = 96;                       // a quarter of them, six 16-neuron tiles

    // The words Watch reads, and the words the tests then ask about.
    private static readonly int[] Watched = [3, 8, 12, 20, 6, 11, 14, 27, 5, 9, 30, 2, 17, 25, 4, 22, 13, 7, 29, 16, 10, 31, 19, 26];
    private static readonly int[] Asked = [5, 9, 30, 2, 17, 21, 8, 12, 24, 15];

    // DESIGN, "Index every part of the model, not one part": "Feed-forward layers act as key-value memories ... Find the
    // matching keys through the index ... and fetch only their values." The knowledge layers are ffn_gate, ffn_up and
    // ffn_down. An index that lists every neuron serves the whole knowledge layer, the same from every step - the bytes are
    // the same in step 4, in step 2, and gathered from step 1, so the logits are the same to the bit. ffn_down's neurons are columns,
    // so serving them by neuron re-quantises them transposed (lossy); it matches the full read on the answer (DESIGN: "same
    // words as a full read is the wrong test"; the measure is the exam), not to the bit.
    [Fact]
    public void An_index_of_every_neuron_serves_the_knowledge_layer_the_same_from_every_tier_and_answers_as_the_model()
    {
        WithModel(201, (file, directory) =>
        {
            var full = Logits(Full(file), Asked);
            var core = Core(file, directory, groups: 2, list: Ff);
            var pieces = CachedIndex.Build(core, file, Path.Combine(directory, "model.core"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
            List<float[]>? firstTier = null;
            foreach (var (tier, split, warm) in new[] { ("step 4 in RAM", pieces, long.MaxValue), ("step 2", pieces, 0L), ("step 1 through step 2", (CachedIndex?)null, 0L) })
            {
                using var serving = new IndexedFeedForward(core, split, file, warm, channels: 2, readBytes: FlashFile.Page);
                var got = Measured($"the index alone, served from {tier},", () => Logits(FromTheIndex(file, serving), Asked));
                if (firstTier is null)
                    firstTier = got;
                else
                    for (var w = 0; w < Asked.Length; w++)
                        Assert.True(Bits(firstTier[w]).SequenceEqual(Bits(got[w])), $"served from {tier}: word {w} differs from the first tier's - the tiers serve the same bytes");
                var same = 0;
                for (var w = 0; w < Asked.Length; w++) if (Top(full[w]) == Top(got[w])) same++;
                Assert.True(same * 10 >= Asked.Length * 9, $"served from {tier}: the index's word matched the full read {same} of {Asked.Length}");
            }
        });
    }

    // DESIGN, the warm index: "Warm index, in RAM: only what the thought in hand is using. Answers come from here, which is
    // how response time comes down." With every piece a word uses in step 4, the word reads nothing from flash.
    [Fact]
    public void A_word_whose_pieces_are_warm_reads_nothing_from_flash()
    {
        WithModel(203, (file, directory) =>
        {
            var core = Core(file, directory, groups: 4, list: Quarter);
            var pieces = CachedIndex.Build(core, file, Path.Combine(directory, "model.core"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
            using var serving = new IndexedFeedForward(core, pieces, file, warmBudget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
            Assert.True(serving.WarmPieces == pieces.Count, $"{serving.WarmPieces} of step 2's {pieces.Count} pieces warm with room for all");
            var indexed = FromTheIndex(file, serving);
            var state = indexed.NewState(32);
            foreach (var token in Asked)
            {
                Measured("a warm word from the index alone", () => indexed.Forward(token, state));
                var counts = serving.TakeCounts();
                Assert.True(counts.Bytes == 0 && counts.Warm == Layers,
                    $"word {token}: {counts.Warm} pieces from step 4, {counts.Ahead + counts.Asked} from step 2 ({counts.Bytes} bytes)");
            }
        });
    }

    // PROMPT, the concept: "Running the model becomes a search, not a full read." PLAN, step 5: "fewer bytes read per word
    // than the full read". With nothing warm, a word's knowledge layers are read as its pieces, one a layer, and that is
    // fewer bytes than every neuron of ffn_gate, ffn_up and ffn_down.
    [Fact]
    public void With_nothing_warm_a_word_reads_its_pieces_and_fewer_bytes_than_a_full_read()
    {
        WithModel(205, (file, directory) =>
        {
            var core = Core(file, directory, groups: 4, list: Quarter);
            var pieces = CachedIndex.Build(core, file, Path.Combine(directory, "model.core"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
            using var serving = new IndexedFeedForward(core, pieces, file, warmBudget: 0, channels: 2, readBytes: FlashFile.Page);
            var indexed = FromTheIndex(file, serving);
            var fullRead = KnowledgeBytes(file);
            var state = indexed.NewState(32);
            foreach (var token in Asked)
            {
                Measured("a word from the index alone with nothing warm", () => indexed.Forward(token, state));
                var counts = serving.TakeCounts();
                var served = counts.Warm + counts.Ahead + counts.Asked + counts.Again;
                Assert.True(served == Layers, $"word {token}: {served} pieces for {Layers} layers");
                Assert.True(counts.Bytes < fullRead, $"word {token}: {counts.Bytes} bytes read; a full read of the knowledge layers is {fullRead}");
            }
        });
    }

    // DESIGN, the tiers: "Core index, on flash: complete and lasting." Store: "The pieces go on the SSD, with those that fire
    // together side by side." PLAN, step 5: "A word's thought finds its group and only that list is read." The core is the
    // complete piece store on flash; one read brings the group's piece - its neurons' gate, up and down - not the layer around it.
    [Fact]
    public void A_read_from_the_core_brings_only_the_groups_piece_not_its_whole_layer()
    {
        WithModel(207, (file, directory) =>
        {
            const int list = 32;
            var core = Core(file, directory, groups: 4, list: list);
            var coreStore = CachedIndex.Build(core, file, Path.Combine(directory, "core.store"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
            Assert.Equal(Layers * core.Groups, coreStore.Count); // complete: every group's piece is on flash
            using var serving = new IndexedFeedForward(core, coreStore, file, warmBudget: 0, channels: 2, readBytes: FlashFile.Page);
            var piece = FlashFile.Pages(CachedIndex.PieceBytes(list, SyntheticQwen35.Embedding));
            var wholeLayer = file.Tensor("blk.0.ffn_gate.weight")!.ByteCount + file.Tensor("blk.0.ffn_up.weight")!.ByteCount + file.Tensor("blk.0.ffn_down.weight")!.ByteCount;
            var rng = new Random(207);
            long pieces = 0, bytes = 0;
            for (var round = 0; round < 3; round++)
            {
                for (var layer = 0; layer < Layers; layer++)
                {
                    serving.Piece(layer, Thought(rng));
                    var counts = serving.TakeCounts();
                    pieces += counts.Asked;
                    bytes += counts.AskedBytes;
                }
            }
            Assert.Equal(3L * Layers, pieces);
            Assert.True(bytes / pieces == piece, $"a core read brought {bytes / pieces} bytes; the group's piece is {piece}, its whole knowledge layer {wholeLayer}");
            Assert.True(piece < wholeLayer, $"the piece {piece} is not smaller than the whole knowledge layer {wholeLayer}");
        });
    }

    // THE OWNER: "RAM IS THE TURBO NOT THE ENGINE". PROMPT: "RAM is working memory and the turbo". What RAM holds changes how
    // soon a word comes, never which word: the same logits, to the bit, with nothing warm, half warm and everything warm.
    [Fact]
    public void RAM_is_the_turbo_the_answers_are_the_same_whatever_RAM_holds()
    {
        WithModel(209, (file, directory) =>
        {
            var core = Core(file, directory, groups: 4, list: Quarter);
            var cached = CachedIndex.Build(core, file, Path.Combine(directory, "model.cached"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
            List<float[]>? first = null;
            foreach (var warm in new[] { 0L, CachedBytes(cached, core) / 2, long.MaxValue })
            {
                using var serving = new IndexedFeedForward(core, cached, file, warm, channels: 2, readBytes: FlashFile.Page);
                if (warm == 0) Assert.True(serving.WarmBytes == 0, $"{serving.WarmBytes} bytes warm with no RAM given");
                var got = Measured($"the index alone with {warm} bytes warm", () => Logits(FromTheIndex(file, serving), Asked));
                first ??= got;
                for (var w = 0; w < Asked.Length; w++)
                    Assert.True(Bits(first[w]).SequenceEqual(Bits(got[w])), $"{warm} bytes warm: word {w} differs from nothing warm");
            }
        });
    }

    // THE OWNER, 2026-11-15: "Select coherent portions from 3 into RAM for 4) warm index. Only 4 ever reaches into 3, only 3
    // reaches into 2 and only 2 reaches into 1" - which took the place of the cached index the owner had asked for before
    // ("a core index on SSD / Flash from which a portion is stored as cached index"), no tier now sitting between step 4 and
    // step 2. Step 4 is filled from step 2's pieces alone, within its budget; with no step 2 it holds nothing.
    [Fact]
    public void Step_4_takes_its_warm_pieces_from_step_2_within_its_budget()
    {
        WithModel(211, (file, directory) =>
        {
            var core = Core(file, directory, groups: 4, list: Quarter);
            using (var none = new IndexedFeedForward(core, null, file, warmBudget: long.MaxValue, channels: 2, readBytes: FlashFile.Page))
                Assert.True(none.WarmPieces == 0, $"{none.WarmPieces} pieces warm with no step 2 to take them from");

            var everything = CachedIndex.Build(core, file, Path.Combine(directory, "model.core"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
            var piece = CachedBytes(everything, core) / everything.Count;
            using var three = new IndexedFeedForward(core, everything, file, warmBudget: 3 * piece, channels: 2, readBytes: FlashFile.Page);
            Assert.True(three.WarmPieces is > 0 and <= 3 && three.WarmBytes <= 3 * piece,
                $"a budget of {3 * piece} bytes holds {three.WarmPieces} pieces, {three.WarmBytes} bytes");
            Assert.True(Pieces(everything, core).Count(p => three.HoldsWarm(p.Layer, p.Group)) == three.WarmPieces, "step 4 holds a piece step 2 does not");
        });
    }

    // PROMPT, step 5: "the next ones fetched ahead of need, the most-used kept hot in RAM." A piece asked for again and again,
    // while RAM has room, comes from RAM, not from flash each time.
    [Fact]
    public void A_piece_used_again_and_again_is_kept_hot_in_RAM()
    {
        WithModel(213, (file, directory) =>
        {
            var core = Core(file, directory, groups: 4, list: Quarter);
            var cached = CachedIndex.Build(core, file, Path.Combine(directory, "model.cached"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
            using var serving = new IndexedFeedForward(core, cached, file, warmBudget: CachedBytes(cached, core) / 2, channels: 2, readBytes: FlashFile.Page);
            var rng = new Random(213);
            float[]? cold = null;
            for (var tries = 0; tries < 200 && cold is null; tries++)
            {
                var thought = Thought(rng);
                serving.Piece(0, thought);
                if (serving.TakeCounts().Warm == 0) cold = thought;
            }
            Assert.True(cold is not null, "setup: every thought tried found its piece warm already");
            for (var i = 0; i < 20; i++) serving.Piece(0, cold!);
            serving.TakeCounts();
            serving.Piece(0, cold!);
            var counts = serving.TakeCounts();
            Assert.True(counts.Warm == 1, $"asked for 21 times, the piece still came from step 2 ({counts.Ahead + counts.Asked} reads), not step 4 in RAM");
        });
    }

    // DESIGN, "Index every part": "Output step. Scoring the whole vocabulary is a top-matches search, so it need not touch
    // every row." PLAN, step 6: "the output step as a top-matches search". The head reads fewer bytes a word than every row of
    // its surface, and finds the word the full scoring finds (ASSUMED: nine words in ten).
    [Fact]
    public void The_output_step_reads_fewer_rows_than_its_whole_surface()
    {
        WithModel(215, (file, directory) =>
        {
            // Three scripts, every one in a Latin speaker's surface, so the surface is the whole vocabulary in three chunks:
            // more windows than the stream's two buffers, which would otherwise hold the whole head in RAM.
            static Script ScriptOf(int token) => token < 2 ? Script.Special : token % 2 == 0 ? Script.Latin : Script.Neutral;
            var store = HeadStore.Write(file, Path.Combine(directory, "model.head"), ScriptOf, chunkRows: 16, layout: HeadLayout.Paired);
            using var stream = store.Stream(store.Surface([Script.Latin]), channels: 2, readBytes: FlashFile.Page, windowBytes: FlashFile.Page);
            Assert.True(stream.Windows >= 3 && stream.Rows == SyntheticQwen35.Vocabulary,
                $"setup: {stream.Windows} windows over {stream.Rows} rows - too few to turn over, so the head would be held in RAM");
            var searched = LanguageModel.Load(file, null, headFromStore: true, pairedOverride: true);
            searched.Head = stream;

            // The head's search index: a sketch of every token's row in RAM, and each token's chunk - read from a surface of
            // its own, so building it does not count against the scoring's reads.
            using (var map = store.Load(store.Surface([Script.Latin]), channels: 2))
                searched.HeadSearch = HeadSearch.Build(file, map, dim: 24);
            Assert.Equal(stream.ChunkCount, searched.HeadSearch!.ChunkCount);
            stream.Prefetch = false; // a search reads scattered candidates, so it does not read the next window ahead

            var full = Full(file);
            var a = full.NewState(32);
            var b = searched.NewState(32);
            var x = new float[SyntheticQwen35.Vocabulary];
            var y = new float[SyntheticQwen35.Vocabulary];
            var same = 0;
            // The stream prefetches its first window in the constructor, and that background read adds its bytes to the
            // counter whenever it lands - which can be either side of the reset below. Join it first (reading chunk 0
            // finishes the in-flight read) so its page is counted and then zeroed; otherwise, under parallel load the
            // read lands after the reset and its bytes are charged to the first word, whose scoring then reads the whole
            // surface. Seen as a flaky "word 5: read == SurfaceBytes" (token 5 is the first word) only under suite load.
            stream.Chunk(0, out _, out _, out _);
            stream.TakeCounts();
            foreach (var token in Asked)
            {
                full.Forward(token, a, x);
                searched.Forward(token, b, y);
                var (read, _) = stream.TakeCounts();
                Assert.True(read < stream.SurfaceBytes, $"word {token}: the head read {read} bytes; every row of its surface is {stream.SurfaceBytes}");
                if (Top(x) == Top(y)) same++;
            }
            Assert.True(same * 10 >= Asked.Length * 9, $"the search found the full scoring's word {same} times in {Asked.Length}");
        });
    }

    // DESIGN, "Index every part": "Attention. Already a search over past words. Indexed, a conversation's memory can sit on
    // the SSD and grow far past RAM." A state given RAM for 16 words, its keys and values spilling to flash, carries on past
    // them; the spilled keys and values are the same fp16 read back, so its every logit is the full memory's, to the bit.
    [Fact]
    public void A_conversations_memory_grows_past_what_RAM_holds_for_it()
    {
        WithModel(217, (file, directory) =>
        {
            var model = Full(file);
            using var inRam = model.NewState(16, Path.Combine(directory, "spill"));
            var all = model.NewState(64);
            var words = Watched.Concat(Asked).Take(28).ToArray();
            var x = new float[SyntheticQwen35.Vocabulary];
            var y = new float[SyntheticQwen35.Vocabulary];
            for (var i = 0; i < words.Length; i++)
            {
                model.Forward(words[i], all, x);
                var at = i;
                Measured($"a conversation past the 16 words RAM holds, at word {at + 1},", () => model.Forward(words[at], inRam, y));
                Assert.True(Bits(x).SequenceEqual(Bits(y)), $"at word {at + 1}, the spilled memory's logits differ from the full memory's");
            }
            Assert.True(inRam.Position == words.Length && inRam.Position > inRam.Capacity,
                $"the conversation held {inRam.Position} words with RAM for {inRam.Capacity}");
        });
    }

    // DESIGN, "Index every part": "Layers. Index which layers an easy word can skip." PLAN, step 6: "Which layers an easy word
    // can skip". Layer 2 of this model adds nothing, so the skip index marks it; an easy word run with that index skips it -
    // fewer layers run (the layers a word ran are the ones Watch saw) - and the answer is unchanged, because the layer adds nothing.
    [Fact]
    public void An_easy_word_runs_fewer_layers_than_the_model_has()
    {
        WithModel(219, (file, directory) =>
        {
            var easy = Enumerable.Repeat(9, 12).ToArray();
            var skip = LayerSkip.Build(Full(file), easy);
            Assert.True(skip[2], "the layer that adds nothing was not marked skippable");
            var skipped = skip.Count(s => s);

            var model = Full(file);
            model.Skip = skip;
            long ran;
            using (var watch = new ThoughtWatch(Path.Combine(directory, "easy.watch"), Layers, SyntheticQwen35.Embedding, Ff, kept: 16))
            {
                model.Watch = watch;
                var state = model.NewState(32);
                foreach (var token in easy) model.Forward(token, state);
                model.Watch = null;
                ran = watch.Records;
            }
            Assert.Equal((long)(Layers - skipped) * easy.Length, ran);
            Assert.True(ran < (long)Layers * easy.Length, $"{easy.Length} easy words ran {ran} layer-runs of {Layers * easy.Length}");

            // Lossless: skipping a layer that adds nothing leaves every logit as the full run's.
            var full = Full(file);
            var withSkip = Full(file);
            withSkip.Skip = skip;
            var a = full.NewState(32);
            var s = withSkip.NewState(32);
            var x = new float[SyntheticQwen35.Vocabulary];
            var y = new float[SyntheticQwen35.Vocabulary];
            foreach (var token in easy)
            {
                full.Forward(token, a, x);
                withSkip.Forward(token, s, y);
                Assert.True(Bits(x).SequenceEqual(Bits(y)), $"skipping the dead layer changed the logits for word {token}");
            }
        }, quietLayer: 2);
    }

    // DESIGN, "One model": "A device runs one model ... Words per pass come from the model's own draft head when its file has
    // one, never from a second model." PLAN: "No second model drafts for it". Neither a second model named for it nor one
    // lying beside it in the catalogue is taken to draft.
    [Fact]
    public void No_second_model_drafts_for_the_one_model()
    {
        var root = Directory.CreateTempSubdirectory("one-model-").FullName;
        try
        {
            var trunkPath = Path.Combine(root, "big", "big.gguf");
            var smallPath = Path.Combine(root, "small", "small.gguf");
            Directory.CreateDirectory(Path.GetDirectoryName(trunkPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(smallPath)!);
            File.WriteAllBytes(trunkPath, SyntheticQwen35.Build(GgmlType.PTQ1_0, 221, layers: Layers));
            File.WriteAllBytes(smallPath, SyntheticQwen35.Build(GgmlType.PTQ1_0, 223, layers: 2));
            string? named, beside;
            using (var trunk = GgufFile.Open(trunkPath))
            {
                named = ManagedChatGenerator.FindDrafter(trunkPath, trunk, new ManagedGeneratorOptions { DrafterPath = smallPath, StreamedDrafts = 4 });
                beside = ManagedChatGenerator.FindDrafter(trunkPath, trunk, new ManagedGeneratorOptions { StreamedDrafts = 4 });
            }
            Assert.True(named is null && beside is null,
                $"a second model drafts for the one model: {named ?? "none"} when one is named, {beside ?? "none"} from beside it");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // DESIGN, "Models and voices it must carry": "Split knows each format's block layout and leaves any format it does not
    // know whole in the core; each layout is proven byte-exact by verify." PROMPT, step 8: "every special format they need,
    // such as PTQ1_0 and PQ2_0." The vector kernel's tiles are PTQ1_0's, so Split tiles a PTQ1_0 model byte-exact - and a
    // PTQ1_0 model that also carries another format (its Q8_0 draft head) still tiles its trunk. A PQ2_0 model it does not
    // tile: it leaves it whole in the core, where the model file's PQ2_0 blocks are intact and byte-exact, and declining
    // stops nothing.
    [Fact]
    public void Split_tiles_ptq1_byte_exact_and_leaves_pq2_whole_in_the_core()
    {
        var found = new List<string>();
        foreach (var (name, mtp) in new[] { ("PTQ1_0", false), ("PTQ1_0 with a Q8_0 draft head", true) })
        {
            WithModel(225, (file, directory) =>
            {
                foreach (var set in new[] { TileSet.FeedForward, TileSet.Mixing })
                {
                    if (TileStore.Unreadable(file, set) is { } why)
                    {
                        found.Add($"{name}, {set.Name}: not tiled - {why}");
                        continue;
                    }
                    var store = TileStore.Write(file, Path.Combine(directory, $"model.{set.Name}"), set);
                    var different = store.Verify(file, channels: 2, readBytes: FlashFile.Page);
                    if (different != 0) found.Add($"{name}, {set.Name}: verify found {different} different");
                }
            }, packing: GgmlType.PTQ1_0, mtp: mtp);
        }
        Assert.True(found.Count == 0, string.Join("; ", found));

        // PQ2_0: left whole in the core, declining stops nothing, and its blocks are intact (128 weights to 34 bytes).
        WithModel(226, (file, _) =>
        {
            foreach (var set in new[] { TileSet.FeedForward, TileSet.Mixing })
            {
                Assert.NotNull(TileStore.Unreadable(file, set));
                foreach (var kind in set.Kinds)
                {
                    var tensor = file.Tensor($"blk.0.{kind}.weight");
                    if (tensor is null) continue;
                    Assert.Equal(GgmlType.PQ2_0, tensor.Type);
                    var blocks = tensor.RowLength / CircleAI.Inference.Managed.Kernels.Pq2.BlockWeights * tensor.Rows;
                    Assert.Equal(blocks * CircleAI.Inference.Managed.Kernels.Pq2.BlockBytes, tensor.ByteCount);
                }
            }
        }, packing: GgmlType.PQ2_0);
    }

    // DESIGN, Split: "It is cut into pieces by layer and neuron group, at the size where this device's flash reads scattered
    // pieces at nearly full speed, which the profile measures (256 KB or larger on AngelDust)." And: "Nothing about a device
    // is written into the code. The device measures itself". A profile that reads 16 KB at nearly full speed gets no piece
    // smaller than 16 KB.
    [Fact]
    public void Each_stored_piece_is_no_smaller_than_the_profile_reads_at_nearly_full_speed()
    {
        WithModel(227, (file, directory) =>
        {
            var profile = new FlashProfile(Direct: false, Straight1: 1700, Straight4: 1900,
                Scattered: [new FlashProfile.Point(4096, 8, 180), new FlashProfile.Point(16384, 8, 1600), new FlashProfile.Point(65536, 8, 1700)],
                ChunkBytes: 16384, Channels: 4, Fingerprint: "a test's flash");
            var core = Core(file, directory, groups: 4, list: 32);
            var cached = CachedIndex.Build(core, file, Path.Combine(directory, "model.cached"), budget: long.MaxValue, channels: profile.Channels, readBytes: profile.ChunkBytes);
            var small = Pieces(cached, core).Where(p => p.Bytes < profile.ChunkBytes).ToList();
            Assert.True(small.Count == 0,
                $"{small.Count} of {cached.Count} pieces are smaller than the profile's {profile.ChunkBytes} bytes, the largest of them {(small.Count == 0 ? 0 : small.Max(p => p.Bytes))}");
        });
    }

    // DESIGN, "The fixed head, read once": "The part of every prompt that does not change ... is read once ..., its memory
    // state saved to flash, and restored for each question, so an answer reads only the new words. On a hybrid model the
    // running state is restored exactly with it." PLAN, step 2: "The saved system-prompt head is reused correctly on a hybrid
    // model, or not at all." Restored from flash for two questions in turn, each answer is the one read from the start.
    [Fact]
    public void The_fixed_head_saved_to_flash_is_restored_exactly_for_each_question()
    {
        WithModel(229, (file, directory) =>
        {
            var model = Full(file);
            var head = Watched.Take(10).ToArray();
            var path = Path.Combine(directory, "head.state");
            var saved = model.NewState(64);
            model.ForwardBatch(head, saved, new float[head.Length][]);
            using (var writer = new BinaryWriter(File.Create(path))) saved.Write(writer, model.Config);
            foreach (var question in new[] { new[] { 5, 9, 30, 2, 17 }, new[] { 21, 8, 12, 24, 15 } })
            {
                ModelState restored;
                using (var reader = new BinaryReader(File.OpenRead(path))) restored = ModelState.Read(reader, model.Config);
                Assert.True(restored.Position == head.Length, $"the restored head is at word {restored.Position}, not {head.Length}");
                var fresh = model.NewState(64);
                model.ForwardBatch(head, fresh, new float[head.Length][]);
                var x = new float[SyntheticQwen35.Vocabulary];
                var y = new float[SyntheticQwen35.Vocabulary];
                foreach (var token in question)
                {
                    model.Forward(token, fresh, x);
                    model.Forward(token, restored, y);
                    Assert.True(Bits(x).SequenceEqual(Bits(y)), $"word {token} after the restored head differs from the head read from the start");
                }
            }
        });
    }

    // DESIGN, "Index every part": "Neurons fire for every word, a few strongly and the rest faintly, and similar thoughts light
    // up similar neurons. So the index groups the thoughts ... A word's thought finds its group in RAM". A thought nudged a
    // little finds the group it found before (ASSUMED: nine in ten).
    [Fact]
    public void A_thought_nudged_a_little_finds_the_same_group()
    {
        WithModel(231, (file, directory) =>
        {
            var watch = Watch(file, directory, Watched, kept: Quarter);
            var core = ThoughtIndex.Build(watch, Path.Combine(directory, "model.index"), groups: 4, listNeurons: Quarter);
            var rng = new Random(231);
            int same = 0, all = 0;
            foreach (var (layer, sketch, _) in watch.Each())
            {
                var nudged = sketch.Select(v => v + 0.05f * (float)(rng.NextDouble() * 2 - 1) / MathF.Sqrt(sketch.Length)).ToArray();
                var norm = MathF.Sqrt(nudged.Sum(v => v * v));
                for (var i = 0; i < nudged.Length; i++) nudged[i] /= norm;
                all++;
                if (core.Nearest(layer, nudged) == core.Nearest(layer, sketch)) same++;
            }
            Assert.True(same * 10 >= all * 9, $"a nudged thought found its group {same} times in {all}");
        });
    }

    // DESIGN, what is kept warm: "the portion likely to be needed (this person, their topics, recent conversations)"; Revise:
    // "the index is brought up to date each night". After a day on one topic the night revises step 2's heat, and the next
    // morning step 4 holds the groups that day used, as many as its budget has room for.
    [Fact]
    public void After_a_day_on_one_topic_the_next_mornings_step_4_holds_its_groups()
    {
        WithModel(233, (file, directory) =>
        {
            var core = Core(file, directory, groups: 4, list: Quarter);
            var pieces = CachedIndex.Build(core, file, Path.Combine(directory, "model.core"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
            var budget = CachedBytes(pieces, core) / 2;
            var roomInRam = (int)(budget / pieces.PieceStride);

            // How many of `groups` step 4 holds when it is filled from `from` with half of step 2's bytes.
            int Held(CachedIndex from, HashSet<(int, int)> groups)
            {
                using var warm = new IndexedFeedForward(core, from, file, warmBudget: budget, channels: 2, readBytes: FlashFile.Page);
                return Pieces(from, core).Count(p => groups.Contains((p.Layer, p.Group)) && warm.HoldsWarm(p.Layer, p.Group));
            }

            // The day: one word said again and again, picked so the morning's step 4 does not already hold its groups - a day
            // the morning had covered would measure nothing.
            int[]? topic = null;
            HashSet<(int, int)> dayGroups = [];
            int wanted = 0, heldAtMorning = 0;
            foreach (var word in new[] { 31, 27, 19, 7, 29, 13, 22, 4 })
            {
                int[] said = [.. Enumerable.Repeat(word, 12)];
                var groups = new HashSet<(int, int)>();
                foreach (var (layer, sketch, _) in Watch(file, directory, said, kept: Quarter, name: $"day-{word}.watch").Each())
                    groups.Add((layer, core.Nearest(layer, sketch)));
                var room = Math.Min(groups.Count, roomInRam);
                var held = Held(pieces, groups);
                if (held >= room) continue;
                (topic, dayGroups, wanted, heldAtMorning) = (said, groups, room, held);
                break;
            }
            Assert.True(topic is not null, "setup: the morning's step 4 already holds the groups of every day tried");
            int[] topicB = topic!;

            // The day: the word asked of the model through the index, three times over; the index keeps what the day used.
            var model = LanguageModel.Load(file, null, headFromStore: false, ffnFromStore: true, pairedOverride: true, mixFromStore: true);
            long[] dayUse;
            using (var serving = new IndexedFeedForward(core, pieces, file, warmBudget: 0, channels: 2, readBytes: FlashFile.Page))
            using (var stream = new LayerStream(TileStore.OverModel(file, TileSet.Mixing), TileStore.OverModel(file, TileSet.FeedForward, SplitByThoughts.DownKinds),
                channels: 2, readBytes: FlashFile.Page, warmBudget: 0, slots: 2))
            {
                model.Index = serving;
                model.Stream = stream;
                for (var round = 0; round < 3; round++)
                {
                    var state = model.NewState(32);
                    foreach (var token in topicB) model.Forward(token, state);
                }
                model.Index = null;
                model.Stream = null;
                dayUse = serving.TakeUsage();
            }
            Assert.True(dayGroups.All(g => dayUse[g.Item1 * core.Groups + g.Item2] > 0), "the index did not record the day's groups as used");

            // The night: step 2's heat revised from the day's use.
            var night = CachedIndex.Revise(pieces, dayUse);
            var heldAtNight = Held(night, dayGroups);
            Assert.True(heldAtNight >= wanted,
                $"after a day of word {topicB[0]} the next morning's step 4 holds {heldAtNight} of the {dayGroups.Count} groups the day used, with room for {wanted}; the morning's held {heldAtMorning}");
        });
    }

    // The model with every weight in RAM: the full read the others are measured against.
    private static LanguageModel Full(GgufFile file) => LanguageModel.Load(file, null, headFromStore: false, pairedOverride: true);

    // The model with its knowledge layers from the index alone: no copy of ffn_gate, ffn_up or ffn_down in RAM, and no stream.
    private static LanguageModel FromTheIndex(GgufFile file, IndexedFeedForward serving)
    {
        var model = LanguageModel.Load(file, null, headFromStore: false, ffnFromStore: true, pairedOverride: true, mixFromStore: false);
        model.Index = serving;
        return model;
    }

    // Watch over the words, each record keeping its strongest neurons.
    private static WatchRecords Watch(GgufFile file, string directory, int[] words, int kept, string name = "model.watch")
    {
        var model = Full(file);
        var path = Path.Combine(directory, name);
        using (var watch = new ThoughtWatch(path, Layers, SyntheticQwen35.Embedding, Ff, kept))
        {
            model.Watch = watch;
            model.ForwardBatch(words, model.NewState(64), new float[words.Length][]);
            model.Watch = null;
        }
        return ThoughtWatch.Read(path);
    }

    private static ThoughtIndex Core(GgufFile file, string directory, int groups, int list) =>
        ThoughtIndex.Build(Watch(file, directory, Watched, kept: list), Path.Combine(directory, "model.index"), groups, list);

    private static List<float[]> Logits(LanguageModel model, int[] words)
    {
        var state = model.NewState(32);
        var all = new List<float[]>();
        foreach (var token in words)
        {
            var logits = new float[SyntheticQwen35.Vocabulary];
            model.Forward(token, state, logits);
            all.Add(logits);
        }
        return all;
    }

    // Every ffn_gate, ffn_up and ffn_down byte in the model's file: what a full read of the knowledge layers reads a word.
    private static long KnowledgeBytes(GgufFile file) => file.Tensors
        .Where(t => t.Name.Contains(".ffn_gate.", StringComparison.Ordinal) || t.Name.Contains(".ffn_up.", StringComparison.Ordinal) || t.Name.Contains(".ffn_down.", StringComparison.Ordinal))
        .Sum(t => t.ByteCount);

    private static IEnumerable<(int Layer, int Group, long Bytes)> Pieces(CachedIndex cached, ThoughtIndex core)
    {
        for (var layer = 0; layer < core.Layers; layer++)
        {
            for (var group = 0; group < core.Groups; group++)
            {
                if (cached.Entry(layer, group) is { } entry) yield return (layer, group, entry.Bytes);
            }
        }
    }

    private static long CachedBytes(CachedIndex cached, ThoughtIndex core) => Pieces(cached, core).Sum(p => p.Bytes);

    private static IEnumerable<int> Bits(float[] values) => values.Select(BitConverter.SingleToInt32Bits);

    private static int Top(float[] logits)
    {
        var best = 0;
        for (var i = 1; i < logits.Length; i++)
        {
            if (logits[i] > logits[best]) best = i;
        }
        return best;
    }

    private static float[] Thought(Random rng)
    {
        var thought = new float[SyntheticQwen35.Embedding];
        for (var i = 0; i < thought.Length; i++) thought[i] = (float)(rng.NextDouble() * 2 - 1);
        return thought;
    }

    // What the engine could not do, as a failure that says what was being asked, not as a crash.
    private static T Measured<T>(string what, Func<T> run)
    {
        try
        {
            return run();
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            Assert.Fail($"{what} could not compute: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private static void Measured(string what, Action run) => Measured(what, () =>
    {
        run();
        return 0;
    });

    private static void WithModel(int seed, Action<GgufFile, string> use, GgmlType packing = GgmlType.PTQ1_0, bool mtp = false, int quietLayer = -1)
    {
        var directory = Directory.CreateTempSubdirectory("indexed-mind-asks-").FullName;
        try
        {
            GgufBuilder.WithFile(SyntheticQwen35.Build(packing, seed, mtp: mtp, layers: Layers, quietLayer: quietLayer), path =>
            {
                using var file = GgufFile.Open(path);
                use(file, directory);
                return 0;
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
