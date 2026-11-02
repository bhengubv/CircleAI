// ManagedVisionTests.cs
//
// Seeing on the C# engine, against the pinned fork. fixtures/bonsai2/2B/vision.json holds what the
// fork's own multimodal code (tools/mtmd, built beside the shipped llama.dll by bonsai-ref) made of
// vision-shapes.png - a red disc and a blue square, 100x60 - with the 2B and its Q8_0 tower:
//   - the prompt as mtmd tokenizes it: the text ids, then the picture's 4x3 tokens and the
//     decoder position [t, y, x] of each, then the text after;
//   - the tower's output tokens, and how far they move at 2 and 4 threads and with the tower's
//     flash attention off - the fork's own spread;
//   - the language model on those tokens: 32 greedy steps with the top 20 logits, and its spread.
// Checked here:
//   - the C# tower makes the fork's tokens from the same bytes, within the fork's own spread;
//   - the C# model, fed the fork's tokens at the fork's positions, predicts the fork's logits;
//   - through IChatGenerator, the conversation becomes the fork's ids around the picture, the
//     picture is described as the fork describes it, a next turn reuses the picture already read,
//     and a model with no tower beside it refuses a picture instead of answering blind;
//   - a photo (BONSAI2_PHOTO, else a Windows lock-screen picture) is described end to end.
//
// Needs the 2B and its tower on this machine (says SKIPPED otherwise). The kernels are
// unoptimised in Debug: run these in Release.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CircleAI.Inference;
using CircleAI.Inference.Managed;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Runtime;
using CircleAI.Inference.Managed.Vision;
using CircleAI.Vision;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

/// <summary>One 2B that sees, for the whole class.</summary>
public sealed class VisionGeneratorFixture : IDisposable
{
    public VisionGeneratorFixture()
    {
        Path = Bonsai2Model.TwoB();
        TowerPath = Bonsai2Model.Tower();
        if (Path is not null && TowerPath is not null)
            Generator = ManagedChatGenerator.Load(Path, new ManagedGeneratorOptions { ContextLength = 1024, PrimedStartDirectory = "", VisionTowerPath = TowerPath });
    }

    public string? Path { get; }

    public string? TowerPath { get; }

    public ManagedChatGenerator? Generator { get; }

    public void Dispose() => Generator?.Dispose();
}

public sealed class ManagedVisionTests(VisionGeneratorFixture fixture, ITestOutputHelper output) : IClassFixture<VisionGeneratorFixture>
{
    private static GenerationOptions Greedy(int maxTokens, TurnTimingRecorder? timing = null) =>
        new() { Temperature = 0, MaxTokens = maxTokens, Budget = PowerBudget.None, Timing = timing };

    private ManagedChatGenerator? Generator()
    {
        if (fixture.Generator is null) output.WriteLine(Bonsai2Model.MissingTower);
        return fixture.Generator;
    }

    // ---- the fixture ----

    private sealed record Reference(JsonDocument Document, int[] Before, int[] After, int Columns, int Rows, int NPast, int NPos,
        int[][] Positions, float[] Tokens, int Width, float TowerSpread, float LogitSpread) : IDisposable
    {
        public string Question => Document.RootElement.GetProperty("question").GetString()!;

        public void Dispose() => Document.Dispose();
    }

    private static Reference Load()
    {
        var doc = Bonsai2Model.Fixture("vision.json");
        var root = doc.RootElement;
        var chunks = root.GetProperty("chunks").EnumerateArray().ToList();
        Assert.Equal(3, chunks.Count);
        var image = chunks[1].GetProperty("image");
        var tokens = MemoryMarshal.Cast<byte, float>(Convert.FromBase64String(root.GetProperty("embeddings_f32_base64").GetString()!).AsSpan()).ToArray();
        var towerSpread = root.GetProperty("tower_spread_vs_1_thread").EnumerateObject().Max(p => p.Value.GetSingle());
        var logitSpread = root.GetProperty("spread_vs_1_thread").EnumerateObject().SelectMany(p => p.Value.EnumerateArray()).Max(v => v.GetSingle());
        return new Reference(doc,
            Ids(chunks[0].GetProperty("text")), Ids(chunks[2].GetProperty("text")),
            image.GetProperty("nx").GetInt32(), image.GetProperty("ny").GetInt32(),
            image.GetProperty("n_past").GetInt32(), image.GetProperty("n_pos").GetInt32(),
            image.GetProperty("positions").EnumerateArray().Select(p => p.EnumerateArray().Select(v => v.GetInt32()).ToArray()).ToArray(),
            tokens, root.GetProperty("n_embd").GetInt32(), towerSpread, logitSpread);

        static int[] Ids(JsonElement e) => e.EnumerateArray().Select(v => v.GetInt32()).ToArray();
    }

    private static byte[] Shapes() => File.ReadAllBytes(Bonsai2Model.FixturePath("vision-shapes.png"));

    // ---- the tower ----

    [Fact]
    public void The_tower_makes_the_forks_tokens_from_the_same_picture()
    {
        var towerPath = Bonsai2Model.Tower();
        if (towerPath is null)
        {
            output.WriteLine(Bonsai2Model.MissingTower);
            return;
        }

        using var reference = Load();
        using var file = GgufFile.Open(towerPath);
        using var pool = new WorkerPool(Math.Max(1, Environment.ProcessorCount));
        var tower = VisionTower.Load(file, pool);
        Assert.Equal((24, 1024, 16, 16, 2048, 48), (tower.Config.Layers, tower.Config.Embedding, tower.Config.Heads, tower.Config.PatchSize, tower.Config.Projection, tower.Config.PositionGrid));

        var picture = ImageDecoder.Decode(Shapes());
        var tokens = tower.Encode(picture, VisionConfig.MaxTokens * tower.Config.TokenArea);
        Assert.Equal((reference.Columns, reference.Rows, reference.Width), (tokens.Columns, tokens.Rows, tokens.Width));
        Assert.Equal(reference.Tokens.Length, tokens.Values.Length);

        var worst = 0f;
        var worstAt = 0;
        double scale = 0, total = 0;
        for (var i = 0; i < tokens.Values.Length; i++)
        {
            var diff = MathF.Abs(tokens.Values[i] - reference.Tokens[i]);
            if (diff > worst) (worst, worstAt) = (diff, i);
            scale = Math.Max(scale, Math.Abs(reference.Tokens[i]));
            total += diff;
        }
        output.WriteLine($"{tokens.Columns}x{tokens.Rows} tokens in {tower.LastEncode.TotalMilliseconds:F0} ms on {pool.Threads} threads; |C# - fork| largest {worst:G4} at token {worstAt / tokens.Width}, value {worstAt % tokens.Width}, mean {total / tokens.Values.Length:G4} (values up to {scale:G4}); the fork's own spread {reference.TowerSpread:G4}");
        Assert.True(worst <= reference.TowerSpread, $"a token value differs by {worst}, more than the fork's own spread {reference.TowerSpread}");
    }

    // ---- the language model on the fork's tokens ----

    [Fact]
    public void The_2B_reads_the_forks_picture_tokens_where_the_fork_places_them()
    {
        var path = Bonsai2Model.TwoB();
        if (path is null)
        {
            output.WriteLine(Bonsai2Model.Missing);
            return;
        }

        using var reference = Load();
        // The fork's placement, which ForwardImageToken repeats: (t, y, x) = (p, p + row, p + column)
        // from p where the picture starts, and the text after it at p + max(columns, rows).
        Assert.Equal(reference.Before.Length, reference.NPast);
        Assert.Equal(Math.Max(reference.Columns, reference.Rows), reference.NPos);
        for (var j = 0; j < reference.Columns * reference.Rows; j++)
        {
            var p = reference.NPast;
            Assert.Equal(new[] { p, p + j / reference.Columns, p + j % reference.Columns }, reference.Positions[j]);
        }

        using var file = GgufFile.Open(path);
        using var pool = new WorkerPool(Math.Max(1, Environment.ProcessorCount));
        var model = Qwen35Model.Load(file, pool);
        var logits = new float[model.Config.Vocabulary];
        var steps = reference.Document.RootElement.GetProperty("steps").EnumerateArray().ToList();
        var state = model.NewState(reference.Before.Length + reference.Tokens.Length / reference.Width + reference.After.Length + steps.Count + 1);

        foreach (var id in reference.Before) model.Forward(id, state);
        for (var j = 0; j < reference.Columns * reference.Rows; j++)
            model.ForwardImageToken(reference.Tokens.AsSpan(j * reference.Width, reference.Width), j, reference.Columns, reference.Rows, state);
        Assert.Equal(reference.NPast + reference.NPos, state.RopePosition);
        for (var i = 0; i < reference.After.Length; i++) model.Forward(reference.After[i], state, i == reference.After.Length - 1 ? logits : default);

        // The fork's own spread: its logits with the attention's other path (flash attention off),
        // fed the same picture tokens and the same text.
        var tolerance = reference.LogitSpread;
        var worst = 0f;
        var worstAt = "";
        var flips = new List<string>();
        for (var s = 0; s < steps.Count; s++)
        {
            var step = steps[s];
            var expected = step.GetProperty("token").GetInt32();
            foreach (var pair in step.GetProperty("top").EnumerateArray())
            {
                var diff = MathF.Abs(logits[pair[0].GetInt32()] - pair[1].GetSingle());
                if (diff > worst) (worst, worstAt) = (diff, $"step {s}, token {pair[0].GetInt32()}");
            }
            var top = ArgMax(logits);
            if (top != expected && step.GetProperty("gap").GetSingle() >= tolerance)
                flips.Add($"step {s}: C# {top}, fork {expected}");
            if (s + 1 < steps.Count) model.Forward(expected, state, logits);
        }

        output.WriteLine($"{steps.Count} steps; largest top-20 logit difference {worst:F5} ({worstAt}); the fork's own spread {reference.LogitSpread:G4}");
        Assert.True(worst <= tolerance, $"a top-20 logit differs by {worst} at {worstAt}");
        Assert.True(flips.Count == 0, string.Join("; ", flips));
    }

    [Fact]
    public void A_state_holding_a_picture_keeps_its_place_through_a_session()
    {
        var path = Bonsai2Model.TwoB();
        if (path is null)
        {
            output.WriteLine(Bonsai2Model.Missing);
            return;
        }

        using var reference = Load();
        using var file = GgufFile.Open(path);
        using var pool = new WorkerPool(Math.Max(1, Environment.ProcessorCount));
        var model = Qwen35Model.Load(file, pool);
        var live = model.NewState(128);
        foreach (var id in reference.Before) model.Forward(id, live);
        for (var j = 0; j < reference.Columns * reference.Rows; j++)
            model.ForwardImageToken(reference.Tokens.AsSpan(j * reference.Width, reference.Width), j, reference.Columns, reference.Rows, live);
        Assert.Equal(reference.Before.Length + reference.Columns * reference.Rows, live.Position);
        Assert.Equal(reference.NPast + reference.NPos, live.RopePosition);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true)) live.Write(writer, model.Config);
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var read = Qwen35State.Read(reader, model.Config);
        Assert.Equal((live.Position, live.RopePosition), (read.Position, read.RopePosition));

        var fromLive = new float[model.Config.Vocabulary];
        var fromRead = new float[model.Config.Vocabulary];
        model.Forward(reference.After[0], live, fromLive);
        model.Forward(reference.After[0], read, fromRead);
        Assert.Equal(fromLive, fromRead);
    }

    // ---- through IChatGenerator ----

    [Fact]
    public void The_conversation_becomes_the_forks_ids_around_the_picture()
    {
        var g = Generator();
        if (g is null) return;

        using var reference = Load();
        ChatMessage[] messages = [new("user", reference.Question) { ImageBytes = Shapes() }];
        Assert.True(g.CanSee);
        Assert.Equal("<|im_start|>user\n<|vision_start|><|image_pad|><|vision_end|>" + reference.Question + "<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n",
            g.RenderPrompt(messages));

        var prompt = g.BuildPrompt(messages);
        var count = reference.Columns * reference.Rows;
        Assert.Equal(reference.Before, prompt.Ids.Take(reference.Before.Length));
        Assert.Equal(reference.After, prompt.Ids.Skip(reference.Before.Length + count));
        var picture = prompt.Ids.Skip(reference.Before.Length).Take(count).ToArray();
        Assert.All(picture, id => Assert.True(id < 0, "a picture token's id collides with a word's"));
        Assert.Equal(count, picture.Distinct().Count());
        Assert.Equal(picture, g.BuildPrompt(messages).Ids.Skip(reference.Before.Length).Take(count)); // the same picture, the same ids
        Assert.NotEqual(picture, g.BuildPrompt([new("user", reference.Question) { ImageBytes = Other() }]).Ids.Skip(reference.Before.Length).Take(count));
    }

    [Fact]
    public async Task The_picture_is_described_as_the_fork_describes_it_and_a_next_turn_reuses_it()
    {
        var g = Generator();
        if (g is null) return;

        using var reference = Load();
        ChatMessage[] first = [new("user", reference.Question) { ImageBytes = Shapes() }];
        var timing = new TurnTimingRecorder();
        var reply = await g.GenerateResponseAsync(first, Greedy(32, timing));
        var fork = reference.Document.RootElement.GetProperty("continuation").GetString()!;
        output.WriteLine($"C#:   {reply.Text}");
        output.WriteLine($"fork: {fork}");
        output.WriteLine($"{reply.TokensIn} prompt tokens, prefill {timing.PrefillMs} ms, {reply.TokensOut} reply tokens in {timing.DecodeMs} ms");
        Assert.Equal(FinishReason.Stop, reply.FinishReason);

        // The fork's answer, as far as the fork was sure of it: up to the first step whose top two
        // logits are closer than its own spread, where a different token is no disagreement.
        var tolerance = reference.LogitSpread;
        var endOfTurn = g.Tokenizer.TryGetSpecial("<|im_end|>", out var end) ? end : -1;
        var sure = new List<byte>();
        var whole = false;
        foreach (var step in reference.Document.RootElement.GetProperty("steps").EnumerateArray())
        {
            if (step.GetProperty("gap").GetSingle() < tolerance) break;
            var token = step.GetProperty("token").GetInt32();
            if (token == endOfTurn)
            {
                whole = true;
                break;
            }
            sure.AddRange(g.Tokenizer.Piece(token, special: false).ToArray());
        }
        var expected = System.Text.Encoding.UTF8.GetString(sure.ToArray()).TrimStart();
        if (whole) Assert.Equal(expected, reply.Text);
        else Assert.StartsWith(expected, reply.Text);

        var next = new TurnTimingRecorder();
        var follow = await g.GenerateResponseAsync([.. first, new("assistant", reply.Text), new("user", "What colour is the square?")], Greedy(24, next));
        output.WriteLine($"next turn ({next.KvPath}, prefill {next.PrefillMs} ms): {follow.Text}");
        Assert.Equal("prefix-hit", next.KvPath);
        Assert.Contains("blue", follow.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_photo_is_described_end_to_end_through_IChatGenerator()
    {
        var g = Generator();
        if (g is null) return;
        var photo = Bonsai2Model.Photo();
        if (photo is null)
        {
            output.WriteLine("SKIPPED: no photo here; set BONSAI2_PHOTO to one.");
            return;
        }

        var bytes = await File.ReadAllBytesAsync(photo);
        var timing = new TurnTimingRecorder();
        var clock = Stopwatch.StartNew();
        var reply = await g.GenerateResponseAsync([new("user", "Describe this photo in one sentence.") { ImageBytes = bytes }], Greedy(64, timing));
        clock.Stop();
        output.WriteLine($"{photo} ({ImageBudget.Describe(bytes)}), seen within {g.ContextLength}-token context:");
        output.WriteLine($"  {reply.Text}");
        output.WriteLine($"  {reply.TokensIn} prompt tokens, prefill {timing.PrefillMs} ms: the picture {g.LastPicture.TotalMilliseconds:F0} ms (decoded, fitted, and the tower's {g.LastTower.TotalMilliseconds:F0} ms), the rest the language model; {reply.TokensOut} reply tokens in {timing.DecodeMs} ms; {clock.Elapsed.TotalSeconds:F1} s in all on {g.Threads} threads");
        Assert.Equal(FinishReason.Stop, reply.FinishReason);
        Assert.False(string.IsNullOrWhiteSpace(reply.Text));
        if (Environment.GetEnvironmentVariable("BONSAI2_PHOTO") is null)
        {
            // The default photo: a lake at sunset under snowy hills and pines.
            string[] seen = ["sun", "lake", "water", "mountain", "hill", "snow", "sky", "tree", "forest", "landscape", "reflect"];
            Assert.True(seen.Any(w => reply.Text.Contains(w, StringComparison.OrdinalIgnoreCase)), "the description names nothing in the photo: " + reply.Text);
        }
    }

    [Fact]
    public void A_model_with_no_tower_refuses_a_picture_instead_of_answering_blind()
    {
        var path = fixture.Path;
        if (path is null)
        {
            output.WriteLine(Bonsai2Model.Missing);
            return;
        }

        using var blind = ManagedChatGenerator.Load(path, new ManagedGeneratorOptions { ContextLength = 64, PrimedStartDirectory = "", VisionTowerPath = "" });
        Assert.False(blind.CanSee);
        Assert.Null(blind.VisionTowerPath);
        Assert.Throws<NotSupportedException>(() => blind.RenderPrompt([new("user", "What is this?") { ImageBytes = [1, 2, 3] }]));
        Assert.Throws<NotSupportedException>(() => blind.RenderPrompt([new("system", "Look.") { ImageBytes = Shapes() }, new("user", "Hi")]));
    }

    // ---- reached the way the app reaches a brain ----

    [Fact]
    public void The_one_brain_factory_builds_a_2B_that_finds_its_tower_and_sees()
    {
        var path = fixture.Path;
        var tower = fixture.TowerPath;
        if (path is null || tower is null)
        {
            output.WriteLine(Bonsai2Model.MissingTower);
            return;
        }
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(tower)), Path.GetDirectoryName(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine("SKIPPED: BONSAI2_2B_MMPROJ names a tower away from the 2B; the factory looks beside the model.");
            return;
        }

        // What every site that builds a brain calls, with nothing naming the tower: the row's
        // engine for a Bonsai 2 GGUF is this one, and it finds the tower beside the model.
        using var brain = Assert.IsType<ManagedChatGenerator>(BrainFactory.Default.Build(new BrainRequest(path) { ContextSize = 256, Threads = 2 }));
        Assert.True(brain.CanSee);
        Assert.Equal(Path.GetFullPath(tower), brain.VisionTowerPath, ignoreCase: true);

        using var reference = Load();
        var picture = brain.BuildPrompt([new("user", reference.Question) { ImageBytes = Shapes() }]).Images.Single();
        Assert.Equal((reference.Columns, reference.Rows), (picture.Columns, picture.Rows));
    }

    // ---- pictures whose header is not their size ----

    [Fact]
    public void Only_a_header_that_gives_the_first_frames_size_plans_a_picture_unread()
    {
        Assert.True(ManagedChatGenerator.HeaderGivesTheFrame(Shapes()));               // PNG: IHDR is the image
        Assert.True(ManagedChatGenerator.HeaderGivesTheFrame(ImageDecoder.WebPProbe)); // a still WebP (VP8L)
        Assert.False(ManagedChatGenerator.HeaderGivesTheFrame(Gif()));                 // a GIF's screen
        Assert.False(ManagedChatGenerator.HeaderGivesTheFrame("RIFF\0\0\0\0WEBPVP8X\0\0\0\0"u8)); // a WebP canvas
    }

    [Fact]
    public async Task A_GIF_is_seen_at_the_size_its_frame_decodes_to_not_the_size_of_its_screen()
    {
        var g = Generator();
        if (g is null) return;

        var gif = Gif();
        var decoded = ImageDecoder.Decode(gif);
        output.WriteLine($"the header says {ImageBudget.Measure(gif)}; it decodes to {decoded.Width}x{decoded.Height}");

        using var file = GgufFile.Open(fixture.TowerPath!);
        var config = VisionConfig.FromGguf(file);
        var (width, height) = config.Fit(decoded.Width, decoded.Height, ImageBudget.VisionMaxEdge * ImageBudget.VisionMaxEdge);
        ChatMessage[] messages = [new("user", "Which two colours are in this picture?") { ImageBytes = gif }];
        var picture = g.BuildPrompt(messages).Images.Single();
        Assert.Equal((width / config.TokenSide, height / config.TokenSide), (picture.Columns, picture.Rows));

        // And it runs: the tower's grid is the grid the prompt was built with.
        var reply = await g.GenerateResponseAsync(messages, Greedy(12));
        output.WriteLine($"{picture.Columns}x{picture.Rows} tokens: {reply.Text}");
        Assert.True(reply.TokensOut > 0);
    }

    // A GIF whose one frame - 40x24, red on the left, blue on the right - is drawn at (10, 20) on a
    // 200x120 screen: its header gives the screen, Windows decodes the frame. The LZW is written
    // plainly - a clear code before every pixel - so every code is 3 bits and the table never grows.
    private static byte[] Gif()
    {
        const int frameWidth = 40, frameHeight = 24;
        using var stream = new MemoryStream();
        using var gif = new BinaryWriter(stream);
        gif.Write("GIF89a"u8);
        gif.Write((ushort)200);
        gif.Write((ushort)120);
        gif.Write((byte)0x80); // a global table of two colours
        gif.Write((byte)0);    // the background is colour 0
        gif.Write((byte)0);    // square pixels
        gif.Write(new byte[] { 255, 0, 0, 0, 0, 255 });
        gif.Write((byte)0x2C); // the frame
        gif.Write((ushort)10);
        gif.Write((ushort)20);
        gif.Write((ushort)frameWidth);
        gif.Write((ushort)frameHeight);
        gif.Write((byte)0);    // no table of its own, not interlaced
        gif.Write((byte)2);    // the smallest LZW code size

        var data = new List<byte>();
        int pending = 0, bits = 0;
        for (var y = 0; y < frameHeight; y++)
        {
            for (var x = 0; x < frameWidth; x++)
            {
                Code(4);                            // clear
                Code(x < frameWidth / 2 ? 0 : 1);   // the pixel
            }
        }
        Code(5);                                    // the end
        if (bits > 0) data.Add((byte)pending);
        for (var i = 0; i < data.Count; i += 255)
        {
            var n = Math.Min(255, data.Count - i);
            gif.Write((byte)n);
            gif.Write(data.GetRange(i, n).ToArray());
        }
        gif.Write((byte)0);    // the frame's data ends
        gif.Write((byte)0x3B); // the file ends
        gif.Flush();
        return stream.ToArray();

        void Code(int code)
        {
            pending |= code << bits;
            bits += 3;
            while (bits >= 8)
            {
                data.Add((byte)pending);
                pending >>= 8;
                bits -= 8;
            }
        }
    }

    // A second picture: the same shapes, the square green.
    private static byte[] Other()
    {
        var image = ImageDecoder.Decode(Shapes());
        for (var y = 16; y <= 44; y++)
        for (var x = 60; x <= 88; x++) image.Set(x, y, 30, 200, 60);
        return image.EncodePng();
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
