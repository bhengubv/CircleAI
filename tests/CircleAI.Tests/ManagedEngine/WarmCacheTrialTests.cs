// WarmCacheTrialTests.cs
//
// THE WARM-CACHE TRIAL (the owner, 2026-11-18): start warm, run the categories, and watch each cache tier get better or
// worse across runs. Here on the synthetic model, which proves the machinery - each tier is measured per question, the
// night's revision re-orders what was used - so a real model and real prompts swap straight in.
//
// Two questions it answers with numbers, not opinion:
//   1. Does the warm cache earn its keep? Cold (no RAM) against warm (RAM holds half the pieces).
//   2. Does the night's most-used revision earn its keep? Revising by use each night, against never revising, over four
//      nights. The warm set is fixed for a round (IndexedFeedForward does not evict mid-round), so the only policy knob
//      today is this nightly choice - a runtime working set (where least-stale would live) does not exist yet.
//
// The questions are OpenAI's seven categories ("How People Use ChatGPT", NBER 2025), as token-sets the synthetic model
// can run. What each measures, from IndexedFeedForward.TakeCounts: pieces from step 4 (RAM), step 2 (the flash pieces),
// step 1 (the model's own file); the bytes read.

using System.Diagnostics;
using CircleAI.Inference.Managed;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Kernels;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Store;
using CircleAI.Inference.Managed.Text;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public sealed class WarmCacheTrialTests(ITestOutputHelper output)
{
    private const int Layers = 4;
    private const int Rounds = 4;

    private static readonly int[] Watched =
        [3, 8, 12, 20, 6, 11, 14, 27, 5, 9, 30, 2, 17, 25, 4, 22, 13, 7, 29, 16, 10, 31, 19, 26, 23, 18, 28, 15, 21, 24];

    // OpenAI's seven categories, as token-sets on the synthetic model (a real model + real prompts swap in).
    private static readonly int[][] Categories =
    [
        [3, 8, 12, 20, 6, 8, 12, 6, 3, 20],         // practical-guidance
        [24, 15, 21, 9, 5, 15, 24, 5, 9, 21],       // writing
        [17, 25, 4, 22, 13, 25, 17, 13, 4, 22],     // seeking-information
        [7, 29, 16, 10, 31, 29, 7, 31, 10, 16],     // technical-help
        [2, 19, 26, 14, 11, 19, 2, 11, 14, 26],     // pictures-media
        [30, 27, 6, 9, 23, 27, 30, 23, 9, 6],       // self-expression
        [28, 18, 13, 22, 15, 18, 28, 15, 22, 13],   // other
    ];

    // A body of real text the index is built from, covering the categories' topics.
    private const string Corpus =
        "To change a flat tyre, loosen the wheel nuts, raise the car on the jack, remove the flat, fit the spare, and tighten the nuts in a star pattern. " +
        "If you feel feverish, rest, drink water, and see a doctor if it does not pass in a day or two. " +
        "Good writing is clear and short. Cut weak adverbs, prefer plain words, and read the sentence aloud to hear where it stumbles. " +
        "Mount Everest is the tallest mountain above sea level, about eight thousand eight hundred and forty-nine metres high, on the border of Nepal and Tibet. " +
        "A slow Python loop often rebuilds a list on every pass; move the work outside the loop, use a comprehension, or call a library written in native code. " +
        "The sun set over the sea in bands of orange and violet, the water turning to beaten copper as the light went down. " +
        "It is normal to feel anxious before something new; breathe slowly, name the worry out loud, and take one small step. " +
        "Here is a short joke: why did the programmer quit his job? Because he did not get arrays.";

    // Real questions, one in each of OpenAI's seven categories.
    private static readonly string[] RealQuestions =
    [
        "How do I change a flat tyre on my car?",
        "Please make this sentence clearer and shorter.",
        "What is the tallest mountain in the world and how high is it?",
        "Why is my Python loop so slow, and how do I speed it up?",
        "Describe a sunset over the ocean in vivid words.",
        "I feel anxious today and I am not sure why.",
        "Tell me a short joke about programming.",
    ];

    private readonly record struct Round(long Ram, long Step2, long Step1, long Bytes, TimeSpan Elapsed, long[] Usage)
    {
        public long Total => Ram + Step2 + Step1;
        public string Line => $"RAM {Ram,4} ({(Total > 0 ? 100.0 * Ram / Total : 0),3:F0}%)  step2 {Step2,4}  step1 {Step1,3}  {Bytes / 1024.0,7:F0} KB  {Elapsed.TotalMilliseconds / Categories.Length,7:F2} ms/question";
    }

    [Fact]
    public void The_warm_cache_and_the_nightly_revise_are_measured_over_rounds()
    {
        var directory = Directory.CreateTempSubdirectory("warm-cache-trial-").FullName;
        try
        {
            GgufBuilder.WithFile(SyntheticQwen35.Build(GgmlType.PTQ1_0, 301, layers: Layers), path =>
            {
                using var file = GgufFile.Open(path);
                var core = Core(file, directory, groups: 8, list: 96);
                var pieces = CachedIndex.Build(core, file, Path.Combine(directory, "trial.core"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
                var budget = CachedBytes(pieces, core) / 2;   // RAM holds half the pieces

                // 1. Does the warm cache earn its keep? Cold against warm, the first night.
                var cold = Run(file, core, pieces, warmBudget: 0);
                var warm = Run(file, core, pieces, warmBudget: budget);
                output.WriteLine($"cold (no RAM):   {cold.Line}");
                output.WriteLine($"warm (half RAM): {warm.Line}");
                output.WriteLine("");

                // 2. Does the night's most-used revision earn its keep? Revising each night against never revising.
                output.WriteLine("revising by most-used each night:");
                var revised = pieces;
                var byUse = new long[Rounds];
                for (var round = 0; round < Rounds; round++)
                {
                    var r = Run(file, core, revised, budget);
                    byUse[round] = r.Ram;
                    output.WriteLine($"  night {round + 1}: {r.Line}");
                    revised = CachedIndex.Revise(revised, r.Usage);
                }

                output.WriteLine("never revising (the first night's order kept):");
                var never = new long[Rounds];
                for (var round = 0; round < Rounds; round++)
                {
                    var r = Run(file, core, pieces, budget);
                    never[round] = r.Ram;
                    output.WriteLine($"  night {round + 1}: {r.Line}");
                }

                output.WriteLine("");
                output.WriteLine($"warm cache:  cold {cold.Ram} → warm {warm.Ram} RAM hits ({(warm.Total > 0 ? 100.0 * warm.Ram / warm.Total : 0):F0}% of fetches from RAM, {(cold.Bytes - warm.Bytes) / 1024.0:F0} KB less read)");
                output.WriteLine($"revise:      night 1 {byUse[0]} → night {Rounds} {byUse[^1]} RAM hits ({byUse[^1] - byUse[0]:+#;-#;0})");
                output.WriteLine($"never:       night 1 {never[0]} → night {Rounds} {never[^1]} RAM hits ({never[^1] - never[0]:+#;-#;0})");

                // The trial measures; it does not mandate a direction. It passes when the machinery ran - the warm cache
                // served more from RAM than cold - and leaves whether the nightly revision helped to the numbers above.
                Assert.True(warm.Ram > cold.Ram, $"the warm cache served no more from RAM ({warm.Ram}) than cold ({cold.Ram})");
                return 0;
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private Round Run(GgufFile file, ThoughtIndex core, CachedIndex cached, long warmBudget)
    {
        long ram = 0, step2 = 0, step1 = 0, bytes = 0;
        long[] usage;
        TimeSpan elapsed;
        using (var serving = new IndexedFeedForward(core, cached, file, warmBudget, channels: 2, readBytes: FlashFile.Page))
        {
            var model = LanguageModel.Load(file, null, headFromStore: false, ffnFromStore: true, pairedOverride: true, mixFromStore: false);
            model.Index = serving;
            var sw = Stopwatch.StartNew();   // the questions alone, not the load or the warm start
            foreach (var category in Categories)
            {
                var state = model.NewState(32);
                foreach (var token in category) model.Forward(token, state);
                var c = serving.TakeCounts();
                ram += c.Warm;
                step2 += c.Ahead + c.Asked;
                step1 += c.FromModel;
                bytes += c.Bytes;
            }
            elapsed = sw.Elapsed;
            usage = serving.TakeUsage();
        }
        return new Round(ram, step2, step1, bytes, elapsed, usage);
    }

    // The same trial on the real Ternary-Bonsai-2 model, read from the path in CIRCLEAI_TRIAL_MODEL. Skipped when the
    // variable is not set, so the suite does not need a 430 MB model file to pass.
    [Fact]
    public void The_real_bonsai_2_cache_is_measured()
    {
        var modelPath = Environment.GetEnvironmentVariable("CIRCLEAI_TRIAL_MODEL");
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            output.WriteLine("no real model: set CIRCLEAI_TRIAL_MODEL to a .gguf path to run this trial");
            return;
        }

        var directory = Directory.CreateTempSubdirectory("real-cache-trial-").FullName;
        try
        {
            using var file = GgufFile.Open(modelPath);
            var tokenizer = ByteLevelBpe.FromGguf(file);
            var reader = LanguageModel.Load(file, null, headFromStore: false, pairedOverride: true);
            var cfg = reader.Config;
            var list = Math.Max(16, cfg.FeedForward / 4);   // each thought lists a quarter of the layer's neurons
            output.WriteLine($"model {Path.GetFileName(modelPath)}: {cfg.Layers} layers, {cfg.FeedForward} neurons a layer, vocabulary {cfg.Vocabulary}");

            var corpus = tokenizer.Encode(Corpus, parseSpecial: false).ToArray();
            var questions = RealQuestions.Select(q => tokenizer.Encode(q, parseSpecial: false).ToArray()).ToArray();
            output.WriteLine($"index from {corpus.Length} words of text; {questions.Length} questions of {questions.Sum(q => q.Length)} words in all");

            var watchPath = Path.Combine(directory, "real.watch");
            using (var watch = new ThoughtWatch(watchPath, cfg.Layers, cfg.Embedding, cfg.FeedForward, kept: list))
            {
                reader.Watch = watch;
                reader.ForwardBatch(corpus, reader.NewState(corpus.Length + 8), new float[corpus.Length][]);
                reader.Watch = null;
            }
            var core = ThoughtIndex.Build(ThoughtWatch.Read(watchPath), Path.Combine(directory, "real.index"), groups: 8, listNeurons: list);
            var pieces = CachedIndex.Build(core, file, Path.Combine(directory, "real.core"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
            var budget = CachedBytes(pieces, core) / 2;

            var cold = RealRun(file, core, pieces, warmBudget: 0, questions);
            var warm = RealRun(file, core, pieces, warmBudget: budget, questions);
            foreach (var (name, r) in new[] { ("cold (no RAM)  ", cold), ("warm (half RAM)", warm) })
            {
                var total = r.Ram + r.Step2 + r.Step1;
                output.WriteLine($"{name}: RAM {r.Ram,5} ({(total > 0 ? 100.0 * r.Ram / total : 0),3:F0}%)  step2 {r.Step2,5}  step1 {r.Step1,4}  {r.Bytes / 1024.0,8:F0} KB  {r.Elapsed.TotalMilliseconds / questions.Length,7:F1} ms/question");
            }
            output.WriteLine($"warm cache: {(cold.Bytes - warm.Bytes) / 1024.0:F0} KB less read, {cold.Elapsed.TotalMilliseconds / questions.Length:F1} ms/question cold vs {warm.Elapsed.TotalMilliseconds / questions.Length:F1} ms warm");

            Assert.True(warm.Ram >= cold.Ram, $"the warm cache served no more from RAM ({warm.Ram}) than cold ({cold.Ram})");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private (long Ram, long Step2, long Step1, long Bytes, TimeSpan Elapsed) RealRun(GgufFile file, ThoughtIndex core, CachedIndex cached, long warmBudget, int[][] questions)
    {
        long ram = 0, step2 = 0, step1 = 0, bytes = 0;
        TimeSpan elapsed;
        using (var serving = new IndexedFeedForward(core, cached, file, warmBudget, channels: 2, readBytes: FlashFile.Page))
        {
            var model = LanguageModel.Load(file, null, headFromStore: false, ffnFromStore: true, pairedOverride: true, mixFromStore: false);
            model.Index = serving;
            var sw = Stopwatch.StartNew();
            foreach (var q in questions)
            {
                var state = model.NewState(q.Length + 8);
                foreach (var token in q) model.Forward(token, state);
                var c = serving.TakeCounts();
                ram += c.Warm;
                step2 += c.Ahead + c.Asked;
                step1 += c.FromModel;
                bytes += c.Bytes;
            }
            elapsed = sw.Elapsed;
        }
        return (ram, step2, step1, bytes, elapsed);
    }

    // THE WHOLE THING, END TO END: the real model, its index built and serving a quarter of the neurons, generating a
    // real answer to a real question. Shows the answer (so we see if it is sense or nonsense) and the time it took.
    [Fact]
    public void The_real_bonsai_2_answers_a_question()
    {
        var modelPath = Environment.GetEnvironmentVariable("CIRCLEAI_TRIAL_MODEL");
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            output.WriteLine("no real model: set CIRCLEAI_TRIAL_MODEL to a .gguf path to run this");
            return;
        }

        var directory = Directory.CreateTempSubdirectory("real-answer-").FullName;
        try
        {
            using var file = GgufFile.Open(modelPath);
            var tokenizer = ByteLevelBpe.FromGguf(file);
            var reader = LanguageModel.Load(file, null, headFromStore: false, pairedOverride: true);
            var cfg = reader.Config;
            var corpus = tokenizer.Encode(Corpus, parseSpecial: false).ToArray();
            var question = "What is the tallest mountain in the world?";
            var prompt = tokenizer.Encode($"<|im_start|>user\n{question}<|im_end|>\n<|im_start|>assistant\n", parseSpecial: true).ToArray();

            var full = Generate(reader, prompt, cfg.Vocabulary, 30);
            var fullText = Detokenize(tokenizer, full.Answer).ReplaceLineEndings(" ");
            output.WriteLine($"Q: {question}");
            output.WriteLine($"full model, every neuron: {fullText}");
            output.WriteLine($"full {full.Total.TotalMilliseconds / (prompt.Length + full.Answer.Count):F0} ms a word");

            // Push the slice up from the broken quarter: does the answer start forming words as the index keeps more of
            // each layer's neurons, and how far does its answer drift from the full model's? 1/3 then 1/2.
            foreach (var denom in new[] { 3, 2 })
            {
                var list = Math.Max(16, cfg.FeedForward / denom);
                var sub = Directory.CreateTempSubdirectory($"frac{denom}-").FullName;
                try
                {
                    var watchPath = Path.Combine(sub, "answer.watch");
                    using (var watch = new ThoughtWatch(watchPath, cfg.Layers, cfg.Embedding, cfg.FeedForward, kept: list))
                    {
                        reader.Watch = watch;
                        reader.ForwardBatch(corpus, reader.NewState(corpus.Length + 8), new float[corpus.Length][]);
                        reader.Watch = null;
                    }
                    var core = ThoughtIndex.Build(ThoughtWatch.Read(watchPath), Path.Combine(sub, "answer.index"), groups: 8, listNeurons: list);
                    var pieces = CachedIndex.Build(core, file, Path.Combine(sub, "answer.core"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
                    using var serving = new IndexedFeedForward(core, pieces, file, CachedBytes(pieces, core) / 2, channels: 2, readBytes: FlashFile.Page);
                    var model = LanguageModel.Load(file, null, headFromStore: false, ffnFromStore: true, pairedOverride: true, mixFromStore: false);
                    model.Index = serving;

                    var served = Generate(model, prompt, cfg.Vocabulary, 30);
                    var servedText = Detokenize(tokenizer, served.Answer).ReplaceLineEndings(" ");
                    output.WriteLine($"index {list} of {cfg.FeedForward} neurons (1/{denom}): {servedText}");
                    output.WriteLine($"  {served.Total.TotalMilliseconds / (prompt.Length + served.Answer.Count):F0} ms a word, same as full: {servedText == fullText}");
                    Assert.True(served.Answer.Count > 0, $"nothing generated at 1/{denom}");
                }
                finally
                {
                    Directory.Delete(sub, recursive: true);
                }
            }
            Assert.True(full.Answer.Count > 0, "full model generated nothing");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static int Argmax(float[] v)
    {
        var best = 0;
        for (var i = 1; i < v.Length; i++)
            if (v[i] > v[best]) best = i;
        return best;
    }

    private static string Detokenize(ByteLevelBpe tokenizer, IEnumerable<int> tokens)
    {
        using var bytes = new MemoryStream();
        foreach (var token in tokens)
        {
            var piece = tokenizer.Piece(token);
            bytes.Write(piece);
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static (List<int> Answer, TimeSpan Prefill, TimeSpan Total) Generate(LanguageModel model, int[] prompt, int vocabulary, int maxTokens)
    {
        var state = model.NewState(prompt.Length + maxTokens + 8);
        var logits = new float[vocabulary];
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < prompt.Length; i++) model.Forward(prompt[i], state, logits);
        var prefill = sw.Elapsed;
        var answer = new List<int>();
        for (var i = 0; i < maxTokens; i++)
        {
            var next = Argmax(logits);
            answer.Add(next);
            model.Forward(next, state, logits);
        }
        return (answer, prefill, sw.Elapsed);
    }

    // The x64 kernel's floor: what the lookup-table product does on this machine, since x64 has no fast ternary vector
    // kernel yet (the fast one is ARM only). This is the number a vector kernel for x64 - a Circle OS server or laptop -
    // would lift. Needs no model: the benchmark makes its own weights.
    [Fact]
    public void The_x64_kernel_floor_is_measured()
    {
        foreach (var line in KernelBench.Report())
            output.WriteLine(line);
    }

    // The repack tax on its own: the real model read in place from its own file, no index. Fast (no index build, no
    // served rounds), so the repack figure - the size of improvement one - comes back in a couple of minutes.
    [Fact]
    public void The_repack_tax_is_measured()
    {
        var modelPath = Environment.GetEnvironmentVariable("CIRCLEAI_TRIAL_MODEL");
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            output.WriteLine("no real model: set CIRCLEAI_TRIAL_MODEL to a .gguf path to run this");
            return;
        }
        using var file = GgufFile.Open(modelPath);
        var tokenizer = ByteLevelBpe.FromGguf(file);
        var questions = RealQuestions.Take(2).Select(q => tokenizer.Encode(q, parseSpecial: false).ToArray()).ToArray();

        var inPlace = InPlaceRun(file, questions);
        var perQuestion = inPlace.Elapsed.TotalMilliseconds / questions.Length;
        var repack = inPlace.Repack.TotalMilliseconds / questions.Length;
        var flash = inPlace.Flash.TotalMilliseconds / questions.Length;
        output.WriteLine($"read in place, every neuron, repacked each pass ({questions.Length} questions, {questions.Sum(q => q.Length)} words):");
        output.WriteLine($"  {perQuestion:F0} ms/question - flash read {flash:F0} ms, repack {repack:F0} ms ({(perQuestion > 0 ? 100 * repack / perQuestion : 0):F0}% of the time), {inPlace.Bytes / 1048576.0:F0} MB read");
        Assert.True(inPlace.Bytes > 0, "the in-place read brought no bytes");
    }

    // The feed-forward read in place from the model's own file - every neuron, repacked into the kernel's tiles on every
    // pass. The stream's own counters split the time into the flash read and the repack.
    private (TimeSpan Elapsed, TimeSpan Flash, TimeSpan Repack, long Bytes) InPlaceRun(GgufFile file, int[][] questions)
    {
        using var stream = new LayerStream(
            TileStore.OverModel(file, TileSet.Mixing),
            TileStore.OverModel(file, TileSet.FeedForward),
            channels: 2, readBytes: FlashFile.Page, warmBudget: 0, slots: 2);
        var model = LanguageModel.Load(file, null, headFromStore: false, ffnFromStore: true, pairedOverride: true, mixFromStore: true);
        model.Stream = stream;
        var sw = Stopwatch.StartNew();
        foreach (var q in questions)
        {
            var state = model.NewState(q.Length + 8);
            foreach (var token in q) model.Forward(token, state);
        }
        var elapsed = sw.Elapsed;
        var (_, read, _) = stream.TakeCounts();
        var (_, flashing, repacking) = stream.TakeParts();
        return (elapsed, flashing, repacking, read);
    }

    private static ThoughtIndex Core(GgufFile file, string directory, int groups, int list) =>
        ThoughtIndex.Build(Watch(file, directory, Watched, kept: list), Path.Combine(directory, "trial.index"), groups, list);

    private static WatchRecords Watch(GgufFile file, string directory, int[] words, int kept)
    {
        var model = LanguageModel.Load(file, null, headFromStore: false, pairedOverride: true);
        var path = Path.Combine(directory, "trial.watch");
        using (var watch = new ThoughtWatch(path, Layers, SyntheticQwen35.Embedding, SyntheticQwen35.FeedForward, kept))
        {
            model.Watch = watch;
            model.ForwardBatch(words, model.NewState(64), new float[words.Length][]);
            model.Watch = null;
        }
        return ThoughtWatch.Read(path);
    }

    private static long CachedBytes(CachedIndex cached, ThoughtIndex core)
    {
        long sum = 0;
        for (var layer = 0; layer < core.Layers; layer++)
            for (var group = 0; group < core.Groups; group++)
                if (cached.Entry(layer, group) is { } entry) sum += entry.Bytes;
        return sum;
    }
}
