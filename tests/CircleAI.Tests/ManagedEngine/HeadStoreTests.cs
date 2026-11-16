// HeadStoreTests.cs
//
// The head's store by script (step 1 of the streaming engine): every row of the head in it once and as
// the model holds it; a turn's surface scoring its tokens exactly as the whole head does and nothing
// else; a store cut from another model refused; tokens placed in the right writing system; the flash
// profile measuring a file and choosing a recipe; page faults read out of /proc's own format.

using System.Text;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Runtime;
using CircleAI.Inference.Managed.Store;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public sealed class HeadStoreTests(ITestOutputHelper output)
{
    // The synthetic model's 32 tokens, given scripts by their number: the first two special, then Latin,
    // Cyrillic, Chinese and neutral in turn.
    private static Script ScriptOf(int token) => token < 2
        ? Script.Special
        : (token % 4) switch { 0 => Script.Latin, 1 => Script.Cyrillic, 2 => Script.Cjk, _ => Script.Neutral };

    private static readonly Script[] InLatinSurface = [Script.Special, Script.Neutral, Script.Latin];

    [Theory]
    [InlineData(GgmlType.PTQ1_0)]
    [InlineData(GgmlType.PQ2_0)]
    public void Every_row_of_the_head_is_in_the_store_once_and_as_the_model_holds_it(GgmlType packing)
    {
        WithModel(packing, 7, (file, directory) =>
        {
            var store = HeadStore.Write(file, Path.Combine(directory, "model.head"), ScriptOf, chunkRows: 3);

            Assert.Equal(SyntheticQwen35.Vocabulary, store.Rows);
            Assert.Equal(0, store.Verify(file));
            Assert.Equal(new[] { Script.Special, Script.Neutral, Script.Latin, Script.Cyrillic, Script.Cjk }, store.Groups.Select(g => g.Script));
            foreach (var group in store.Groups)
            {
                Assert.Equal(Enumerable.Range(0, SyntheticQwen35.Vocabulary).Count(t => ScriptOf(t) == group.Script), group.Rows);
                Assert.Equal((group.Rows + 2) / 3, group.Chunks);
            }
            Assert.All(store.Chunks, c => Assert.Equal(0, c.Offset % FlashFile.Page));
            Assert.All(store.Chunks, c => Assert.Equal(0, c.Bytes % FlashFile.Page));
            Assert.False(File.Exists(Path.Combine(directory, "model.head.partial")));

            // Open finds the same store again.
            var again = HeadStore.Open(file, store.FilePath);
            Assert.NotNull(again);
            Assert.Equal(store.Chunks, again.Chunks);
            Assert.Equal(store.Groups, again.Groups);
        });
    }

    [Theory]
    [InlineData(GgmlType.PTQ1_0, false)]
    [InlineData(GgmlType.PTQ1_0, true)]
    [InlineData(GgmlType.PQ2_0, false)]
    [InlineData(GgmlType.PQ2_0, true)]
    public void A_surface_scores_its_tokens_exactly_as_the_whole_head_and_nothing_else(GgmlType packing, bool pooled)
    {
        WithModel(packing, 11, (file, directory) =>
        {
            var store = HeadStore.Write(file, Path.Combine(directory, "model.head"), ScriptOf, chunkRows: 3);
            using var pool = pooled ? new WorkerPool(2) : null;
            var whole = LanguageModel.Load(file, pool);
            var part = LanguageModel.Load(file, pool);
            if (whole.Paired || part.Paired)
            {
                output.WriteLine("SKIPPED: the paired kernel is on (another test turned it on), and a paired model scores its whole head.");
                return;
            }

            using var surface = store.Load(store.Surface([Script.Latin]), channels: 3);
            Assert.Equal(Enumerable.Range(0, SyntheticQwen35.Vocabulary).Count(t => InLatinSurface.Contains(ScriptOf(t))), surface.Rows);
            part.Head = surface;

            var a = whole.NewState(16);
            var b = part.NewState(16);
            var all = new float[SyntheticQwen35.Vocabulary];
            var some = new float[SyntheticQwen35.Vocabulary];
            foreach (var token in new[] { 4, 9, 30, 2, 17 })
            {
                whole.Forward(token, a, all);
                part.Forward(token, b, some);
                for (var t = 0; t < SyntheticQwen35.Vocabulary; t++)
                {
                    if (InLatinSurface.Contains(ScriptOf(t))) Assert.Equal(BitConverter.SingleToInt32Bits(all[t]), BitConverter.SingleToInt32Bits(some[t]));
                    else Assert.Equal(float.NegativeInfinity, some[t]);
                }
            }

            // Let go, the head is the whole table again.
            part.Head = null;
            whole.Forward(5, a, all);
            part.Forward(5, b, some);
            Assert.Equal(all, some);
        });
    }

    [Fact]
    public void A_batch_that_ends_the_prompt_scores_its_last_token_on_the_surface_too()
    {
        WithModel(GgmlType.PTQ1_0, 13, (file, directory) =>
        {
            var store = HeadStore.Write(file, Path.Combine(directory, "model.head"), ScriptOf, chunkRows: 4);
            var whole = LanguageModel.Load(file);
            var part = LanguageModel.Load(file);
            if (whole.Paired || part.Paired)
            {
                output.WriteLine("SKIPPED: the paired kernel is on.");
                return;
            }
            using var surface = store.Load(store.Surface([Script.Latin]), channels: 2);
            part.Head = surface;

            var tokens = new[] { 3, 8, 12, 20, 6 };
            var all = new float[SyntheticQwen35.Vocabulary];
            var some = new float[SyntheticQwen35.Vocabulary];
            whole.ForwardBatch(tokens, whole.NewState(16), [null, null, null, null, all]);
            part.ForwardBatch(tokens, part.NewState(16), [null, null, null, null, some]);
            for (var t = 0; t < SyntheticQwen35.Vocabulary; t++)
            {
                if (InLatinSurface.Contains(ScriptOf(t))) Assert.Equal(BitConverter.SingleToInt32Bits(all[t]), BitConverter.SingleToInt32Bits(some[t]));
                else Assert.Equal(float.NegativeInfinity, some[t]);
            }
        });
    }

    [Fact]
    public void The_paired_shape_holds_every_row_once_in_whole_tiles_and_as_the_vector_kernel_packs_it()
    {
        WithModel(GgmlType.PTQ1_0, 17, (file, directory) =>
        {
            var store = HeadStore.Write(file, Path.Combine(directory, "model.head"), ScriptOf, chunkRows: 16, layout: HeadLayout.Paired);

            Assert.Equal(HeadLayout.Paired, store.Layout);
            Assert.Equal(0, store.Verify(file));
            Assert.All(store.Chunks, c => Assert.Equal(0, c.Rows % 16));
            Assert.Equal(store.Groups.Sum(g => g.Rows), SyntheticQwen35.Vocabulary);

            // Opened in its own shape only: a store of one shape is never read as the other.
            Assert.NotNull(HeadStore.Open(file, store.FilePath, HeadLayout.Paired));
            Assert.Null(HeadStore.Open(file, store.FilePath, HeadLayout.Rows));
            Assert.Throws<ArgumentException>(() => HeadStore.Write(file, Path.Combine(directory, "odd.head"), ScriptOf, chunkRows: 10, layout: HeadLayout.Paired));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_model_that_keeps_no_copy_of_its_head_scores_a_paired_surface_exactly_as_the_paired_whole_head(bool pooled)
    {
        WithModel(GgmlType.PTQ1_0, 19, (file, directory) =>
        {
            var store = HeadStore.Write(file, Path.Combine(directory, "model.head"), ScriptOf, chunkRows: 16, layout: HeadLayout.Paired);
            using var pool = pooled ? new WorkerPool(2) : null;
            var whole = LanguageModel.Load(file, pool, headFromStore: false, pairedOverride: true);
            var served = LanguageModel.Load(file, pool, headFromStore: true, pairedOverride: true);
            Assert.True(served.HeadFromStore);

            using var surface = store.Load(store.Surface([Script.Latin]), channels: 2);
            var a = whole.NewState(16);
            var b = served.NewState(16);
            var all = new float[SyntheticQwen35.Vocabulary];
            var some = new float[SyntheticQwen35.Vocabulary];

            // With no rows to score, it says so rather than read a head it does not keep; a token without logits still runs.
            Assert.Throws<InvalidOperationException>(() => served.Forward(4, served.NewState(16), some));
            served.Forward(4, served.NewState(16));

            served.Head = surface;
            foreach (var token in new[] { 4, 9, 30, 2, 17, 25 })
            {
                whole.Forward(token, a, all);
                served.Forward(token, b, some);
                for (var t = 0; t < SyntheticQwen35.Vocabulary; t++)
                {
                    if (InLatinSurface.Contains(ScriptOf(t))) Assert.Equal(BitConverter.SingleToInt32Bits(all[t]), BitConverter.SingleToInt32Bits(some[t]));
                    else Assert.Equal(float.NegativeInfinity, some[t]);
                }
            }

            // A batch that ends a prompt scores its last token on the surface too.
            var tokens = new[] { 3, 8, 12, 20, 6, 11, 14, 27 };
            whole.ForwardBatch(tokens, whole.NewState(16), [null, null, null, null, null, null, null, all]);
            served.ForwardBatch(tokens, served.NewState(16), [null, null, null, null, null, null, null, some]);
            for (var t = 0; t < SyntheticQwen35.Vocabulary; t++)
            {
                if (InLatinSurface.Contains(ScriptOf(t))) Assert.Equal(BitConverter.SingleToInt32Bits(all[t]), BitConverter.SingleToInt32Bits(some[t]));
                else Assert.Equal(float.NegativeInfinity, some[t]);
            }
        });
    }

    [Fact]
    public void A_surface_of_every_group_scores_every_token_as_the_whole_head()
    {
        WithModel(GgmlType.PTQ1_0, 23, (file, directory) =>
        {
            var store = HeadStore.Write(file, Path.Combine(directory, "model.head"), ScriptOf, chunkRows: 16, layout: HeadLayout.Paired);
            var whole = LanguageModel.Load(file, null, headFromStore: false, pairedOverride: true);
            var served = LanguageModel.Load(file, null, headFromStore: true, pairedOverride: true);
            using var surface = store.Load(store.Groups, channels: 3);
            Assert.Equal(SyntheticQwen35.Vocabulary, surface.Rows);
            served.Head = surface;
            var all = new float[SyntheticQwen35.Vocabulary];
            var some = new float[SyntheticQwen35.Vocabulary];
            whole.Forward(7, whole.NewState(8), all);
            served.Forward(7, served.NewState(8), some);
            Assert.Equal(all.Select(BitConverter.SingleToInt32Bits), some.Select(BitConverter.SingleToInt32Bits));
        });
    }

    [Fact]
    public void A_store_cut_from_another_model_is_not_opened()
    {
        var directory = Directory.CreateTempSubdirectory("head-store-").FullName;
        try
        {
            var path = Path.Combine(directory, "model.head");
            GgufBuilder.WithFile(SyntheticQwen35.Build(GgmlType.PTQ1_0, seed: 7), first =>
            {
                using var file = GgufFile.Open(first);
                HeadStore.Write(file, path, ScriptOf, chunkRows: 3);
                return 0;
            });
            GgufBuilder.WithFile(SyntheticQwen35.Build(GgmlType.PQ2_0, seed: 7), second =>
            {
                using var file = GgufFile.Open(second);
                Assert.Null(HeadStore.Open(file, path));
                Assert.Null(HeadStore.Open(file, Path.Combine(directory, "nothing.head")));
                return 0;
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("hello", Script.Latin)]
    [InlineData(" the", Script.Latin)]
    [InlineData("Sawubona", Script.Latin)]
    [InlineData("café", Script.Latin)]
    [InlineData("’s", Script.Latin)]
    [InlineData("Привет", Script.Cyrillic)]
    [InlineData("ζωή", Script.Greek)]
    [InlineData("مرحبا", Script.Arabic)]
    [InlineData("שלום", Script.Hebrew)]
    [InlineData("नमस्ते", Script.Indic)]
    [InlineData("สวัสดี", Script.Thai)]
    [InlineData("你好", Script.Cjk)]
    [InlineData("こんにちは", Script.Cjk)]
    [InlineData("안녕", Script.Hangul)]
    [InlineData("123", Script.Neutral)]
    [InlineData(", ", Script.Neutral)]
    [InlineData("!", Script.Neutral)]
    [InlineData("😀", Script.Neutral)]
    [InlineData("hi你", Script.Other)]
    public void A_token_is_placed_in_the_script_it_writes(string text, Script expected) =>
        Assert.Equal(expected, TokenScripts.Of(Encoding.UTF8.GetBytes(text), special: false));

    [Theory]
    [InlineData(new byte[] { 0xE4 }, Script.Neutral)]             // one byte: every byte token spells anything
    [InlineData(new byte[] { 0xE4, 0xBD }, Script.Cjk)]           // the first two bytes of a Chinese character
    [InlineData(new byte[] { 0xD0, 0x9F, 0xD1 }, Script.Cyrillic)] // "П" and the start of another Cyrillic letter
    [InlineData(new byte[] { 0xF0, 0x9F }, Script.Neutral)]       // the start of an emoji
    [InlineData(new byte[] { 0xBD, 0xA0 }, Script.Fragment)]      // the tail of a character
    public void A_token_cut_inside_a_character_is_placed_by_what_it_can_still_become(byte[] piece, Script expected) =>
        Assert.Equal(expected, TokenScripts.Of(piece, special: false));

    [Fact]
    public void A_special_token_is_special_whatever_it_writes() =>
        Assert.Equal(Script.Special, TokenScripts.Of("<|im_start|>"u8, special: true));

    [Fact]
    public void The_flash_profile_measures_a_file_and_chooses_a_recipe_it_keeps()
    {
        var directory = Directory.CreateTempSubdirectory("flash-profile-").FullName;
        try
        {
            var path = Path.Combine(directory, "probe.bin");
            var bytes = new byte[8 * 1024 * 1024];
            new Random(5).NextBytes(bytes);
            File.WriteAllBytes(path, bytes);

            var profile = FlashProfile.Measure(path, directory, TimeSpan.FromMilliseconds(5));
            output.WriteLine(profile.ToString());

            Assert.Contains(profile.ChunkBytes, FlashProfile.Sizes);
            Assert.InRange(profile.Channels, 1, 8);
            Assert.Equal(FlashProfile.Sizes.Count * profile.Scattered.Select(p => p.Channels).Distinct().Count(), profile.Scattered.Count);
            Assert.All(profile.Scattered, p => Assert.True(p.MegabytesPerSecond > 0, $"{p.Bytes} bytes on {p.Channels} read nothing"));

            var kept = FlashProfile.Remembered(directory);
            Assert.NotNull(kept);
            Assert.Equal(profile.ChunkBytes, kept.ChunkBytes);
            Assert.Equal(profile.Channels, kept.Channels);
            Assert.Equal(profile.Scattered.Count, kept.Scattered.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // A phone gives a process the cores it likes. On the P30 on 2026-11-13 the inference process had three, and "the
    // flash could not be measured (IndexOutOfRangeException)": the scattered tests ran on one and two readers, the
    // straight one on three, and the third had no buffer. Whatever the process is given, the flash is measured.
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void The_flash_is_measured_however_many_cores_the_phone_gives_the_process(int cores)
    {
        var directory = Directory.CreateTempSubdirectory("flash-profile-").FullName;
        try
        {
            var path = Path.Combine(directory, "probe.bin");
            var bytes = new byte[5 * 1024 * 1024];
            new Random(6).NextBytes(bytes);
            File.WriteAllBytes(path, bytes);

            var profile = FlashProfile.Measure(path, directory, TimeSpan.FromMilliseconds(5), cores);

            Assert.True(profile.Straight1 > 0 && profile.Straight4 > 0, $"with {cores} cores the straight reads read nothing");
            Assert.InRange(profile.Channels, 1, Math.Min(cores, 8));
            Assert.All(profile.Scattered, p => Assert.InRange(p.Channels, 1, cores));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public unsafe void A_flash_file_reads_whole_pages_where_they_are()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flash-file-{Guid.NewGuid():N}.bin");
        var bytes = new byte[3 * FlashFile.Page];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i / FlashFile.Page * 50 + i % 7);
        File.WriteAllBytes(path, bytes);
        var buffer = FlashFile.Allocate(2 * FlashFile.Page);
        try
        {
            using var file = FlashFile.Open(path);
            Assert.Equal(2 * FlashFile.Page, file.Read(buffer, 2 * FlashFile.Page, FlashFile.Page));
            Assert.True(new ReadOnlySpan<byte>(buffer, 2 * FlashFile.Page).SequenceEqual(bytes.AsSpan(FlashFile.Page, 2 * FlashFile.Page)));
            Assert.Equal(FlashFile.Page, file.Read(buffer, 2 * FlashFile.Page, 2 * FlashFile.Page)); // the file ends after one
            file.Drop(0, file.Length);
        }
        finally
        {
            FlashFile.Free(buffer);
            File.Delete(path);
        }
    }

    [Fact]
    public void Page_faults_are_read_from_procs_own_line_whatever_the_process_is_called()
    {
        var line = "1234 (my (odd) name) S 1 2 3 4 5 6 7 8 9 10 11 12 13\n"u8;
        Assert.Equal<(long, long)?>((7L, 9L), PageFaults.Parse(line));
        Assert.Null(PageFaults.Parse("no brackets here"u8));
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid()) Assert.Null(PageFaults.Now());
    }

    [Fact]
    public void The_timing_line_says_where_pages_came_from_only_when_it_knows()
    {
        var known = new ForwardTimes(3, TimeSpan.FromMilliseconds(30), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 2, 40);
        var unknown = new ForwardTimes(3, TimeSpan.FromMilliseconds(30), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
        Assert.Contains("pages faulted in: 2 from flash, 40 from RAM", known.ToString());
        Assert.DoesNotContain("faulted", unknown.ToString());
    }

    // A synthetic model in a file, and a folder for its store, both gone after.
    private static void WithModel(GgmlType packing, int seed, Action<GgufFile, string> use)
    {
        var directory = Directory.CreateTempSubdirectory("head-store-").FullName;
        try
        {
            GgufBuilder.WithFile(SyntheticQwen35.Build(packing, seed), path =>
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
