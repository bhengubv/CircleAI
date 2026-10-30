// PrismHadamardTests.cs
//
// The rotation Bonsai 2's weights are folded with. Five claims:
//   1. The block transform is the normalised Sylvester-Walsh-Hadamard matrix, and its own inverse.
//   2. A folded weight's input is turned H(s * x); an embedding row is turned back s * (H z);
//      ssm_out regroups its heads first when the file says so.
//   3. Weights of one width share one transform, so an activation is turned once.
//   4. Every metadata the fork refuses is refused.
//   5. The real 2B: version 2, block 512, 150 folded weights plus the tied head, signs for its
//      two widths (opt-in: says SKIPPED without the model).

using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Rotation;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public sealed class PrismHadamardTests(ITestOutputHelper output)
{
    private static float[] Random(int n, int seed)
    {
        var rng = new Random(seed);
        var x = new float[n];
        for (var i = 0; i < n; i++) x[i] = (float)(rng.NextDouble() * 2 - 1);
        return x;
    }

    // H[i][j] = (-1)^popcount(i & j) / sqrt(n), applied as a matrix - the definition, not the butterflies.
    private static float[] Dense(float[] v)
    {
        var n = v.Length;
        var result = new float[n];
        for (var i = 0; i < n; i++)
        {
            double s = 0;
            for (var j = 0; j < n; j++) s += (System.Numerics.BitOperations.PopCount((uint)(i & j)) & 1) == 1 ? -v[j] : v[j];
            result[i] = (float)(s / Math.Sqrt(n));
        }
        return result;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(512)]
    public void The_block_transform_is_the_normalised_Sylvester_matrix(int n)
    {
        var x = Random(n, n);
        var fast = x.ToArray();
        PrismHadamard.Transform(fast);
        var dense = Dense(x);
        for (var i = 0; i < n; i++) Assert.InRange(MathF.Abs(fast[i] - dense[i]), 0f, 1e-5f);
    }

    [Fact]
    public void Scaling_comes_first_and_the_butterflies_follow_in_widening_order()
    {
        // [1 2 3 4] / 2 then the butterflies: every intermediate is exact, so the floats are too.
        float[] x = [1, 2, 3, 4];
        PrismHadamard.Transform(x);
        Assert.Equal(new[] { 5f, -1f, -2f, 0f }, x);
    }

    [Fact]
    public void The_block_transform_is_its_own_inverse()
    {
        var x = Random(512, 3);
        var y = x.ToArray();
        PrismHadamard.Transform(y);
        PrismHadamard.Transform(y);
        for (var i = 0; i < x.Length; i++) Assert.InRange(MathF.Abs(y[i] - x[i]), 0f, 1e-6f);
        Assert.Throws<ArgumentException>(() => PrismHadamard.Transform(new float[6]));
    }

    private static Action<BinaryWriter> Text(string value) => w => GgufBuilder.Text(w, value);

    private static Action<BinaryWriter> U32(uint value) => w => w.Write(value);

    private static Action<BinaryWriter> StringArray(string[] values) => w =>
    {
        w.Write((uint)GgufType.String);
        w.Write((ulong)values.Length);
        foreach (var s in values) GgufBuilder.Text(w, s);
    };

    private static Action<BinaryWriter> Int32Array(int[] values) => w =>
    {
        w.Write((uint)GgufType.Int32);
        w.Write((ulong)values.Length);
        foreach (var v in values) w.Write(v);
    };

    // A tiny qwen35-shaped file: blocks of 4, signs for widths 8 and 16, the head tied. A key in
    // `replace` takes the place of the default one (a file may not hold a key twice).
    private static GgufBuilder Folded(Dictionary<string, (GgufType Type, Action<BinaryWriter> Write)>? replace = null,
        string[]? weights = null, int[]? signValues = null, bool grouped = false, bool withOutput = false, string? architecture = null)
    {
        var keys = new List<(string Key, GgufType Type, Action<BinaryWriter> Write)>
        {
            ("general.architecture", GgufType.String, Text(architecture ?? "qwen35")),
            ("qwen35.ssm.time_step_rank", GgufType.UInt32, U32(4)),
            ("qwen35.ssm.group_count", GgufType.UInt32, U32(2)),
            ("prism.hadamard.version", GgufType.UInt32, U32(2)),
            ("prism.hadamard.tied_output", GgufType.Bool, w => w.Write((byte)1)),
            ("prism.hadamard.block_size", GgufType.UInt32, U32(4)),
            ("prism.hadamard.transform", GgufType.String, Text("normalized-sylvester-walsh-hadamard")),
            ("prism.hadamard.axis", GgufType.String, Text("input-last-dimension")),
            ("prism.hadamard.sign_mode", GgufType.String, Text("explicit")),
            ("prism.hadamard.weight_names", GgufType.Array, StringArray(weights ?? ["blk.0.attn_qkv.weight", "blk.0.attn_gate.weight", "blk.0.ffn_down.weight", "blk.0.ssm_out.weight"])),
            ("prism.hadamard.sign_widths", GgufType.Array, Int32Array([8, 16])),
            ("prism.hadamard.sign_values", GgufType.Array, Int32Array(signValues ?? Enumerable.Range(0, 24).Select(i => i % 3 == 0 ? -1 : 1).ToArray())),
            ("prism.hadamard.inverse_weight_names", GgufType.Array, StringArray(["token_embd.weight"])),
        };
        if (grouped) keys.Add(("prism.hadamard.gdn_v_grouped", GgufType.Bool, w => w.Write((byte)1)));
        foreach (var (key, (type, write)) in replace ?? [])
        {
            var at = keys.FindIndex(k => k.Key == key);
            if (at >= 0) keys[at] = (key, type, write);
            else keys.Add((key, type, write));
        }

        var b = new GgufBuilder();
        foreach (var (key, type, write) in keys) b.Key(key, type, write);
        b.Tensor("token_embd.weight", [8, 5], (uint)GgmlType.F32, new byte[8 * 5 * 4])
         .Tensor("blk.0.attn_qkv.weight", [8, 6], (uint)GgmlType.F32, new byte[8 * 6 * 4])
         .Tensor("blk.0.attn_gate.weight", [8, 2], (uint)GgmlType.F32, new byte[8 * 2 * 4])
         .Tensor("blk.0.ffn_down.weight", [16, 8], (uint)GgmlType.F32, new byte[16 * 8 * 4])
         .Tensor("blk.0.ssm_out.weight", [8, 8], (uint)GgmlType.F32, new byte[8 * 8 * 4]);
        if (withOutput) b.Tensor("output.weight", [8, 5], (uint)GgmlType.F32, new byte[8 * 5 * 4]);
        return b;
    }

    private static float[] Signs(int width, int offset) =>
        Enumerable.Range(offset, width).Select(i => i % 3 == 0 ? -1f : 1f).ToArray();

    private static float[] Expected(float[] x, float[] signs)
    {
        var y = x.Select((v, i) => v * signs[i]).ToArray();
        for (var b = 0; b < y.Length; b += 4) PrismHadamard.Transform(y.AsSpan(b, 4));
        return y;
    }

    [Fact]
    public void A_folded_weight_takes_signs_then_H_and_an_embedding_row_turns_back()
    {
        GgufBuilder.Read(Folded().Build(), file =>
        {
            var hadamard = PrismHadamard.FromGguf(file)!;
            Assert.Equal(2, hadamard.Version);
            Assert.True(hadamard.TiedOutput);
            Assert.True(hadamard.InvertsEmbedding);

            var x = Random(8, 1);
            var turned = new float[8];
            hadamard.InputOf("blk.0.attn_qkv.weight")!.Apply(x, turned);
            Assert.Equal(Expected(x, Signs(8, 0)), turned);

            var wide = Random(16, 2);
            var wideTurned = new float[16];
            hadamard.InputOf("blk.0.ffn_down.weight")!.Apply(wide, wideTurned);
            Assert.Equal(Expected(wide, Signs(16, 8)), wideTurned); // the second sign vector, laid after the first

            hadamard.InvertLookup(turned);
            for (var i = 0; i < 8; i++) Assert.InRange(MathF.Abs(turned[i] - x[i]), 0f, 1e-6f);
            Assert.Null(hadamard.InputOf("blk.0.ssm_alpha.weight"));
        });
    }

    [Fact]
    public void Weights_of_one_width_share_one_transform_and_the_tied_head_is_one_of_them()
    {
        GgufBuilder.Read(Folded().Build(), file =>
        {
            var hadamard = PrismHadamard.FromGguf(file)!;
            var qkv = hadamard.InputOf("blk.0.attn_qkv.weight");
            Assert.Same(qkv, hadamard.InputOf("blk.0.attn_gate.weight"));
            Assert.Same(qkv, hadamard.InputOf("token_embd.weight")); // the tied output head
            Assert.Same(qkv, hadamard.InputOf("blk.0.ssm_out.weight")); // no regrouping asked for
            Assert.NotSame(qkv, hadamard.InputOf("blk.0.ffn_down.weight"));
            Assert.Equal(5, hadamard.FoldedWeights.Count);
        });
    }

    [Fact]
    public void Ssm_out_regroups_its_heads_first_when_the_file_says_so()
    {
        GgufBuilder.Read(Folded(grouped: true).Build(), file =>
        {
            var hadamard = PrismHadamard.FromGguf(file)!;
            var input = hadamard.InputOf("blk.0.ssm_out.weight")!;
            Assert.True(input.Regroups);
            Assert.NotSame(input, hadamard.InputOf("blk.0.attn_qkv.weight"));

            // n_v = 4 value heads of hd = 2 over n_k = 2 groups: tiled [hd, nk, rep] -> grouped [hd, rep, nk].
            float[] x = [0, 1, 2, 3, 4, 5, 6, 7];
            float[] regrouped = [0, 1, 4, 5, 2, 3, 6, 7];
            var turned = new float[8];
            input.Apply(x, turned);
            Assert.Equal(Expected(regrouped, Signs(8, 0)), turned);
            Assert.Throws<ArgumentException>(() => input.Apply(turned, turned));
        });
    }

    [Fact]
    public void A_file_without_the_metadata_has_no_rotation()
    {
        GgufBuilder.Read(new GgufBuilder().String("general.architecture", "qwen35").Build(),
            file => Assert.Null(PrismHadamard.FromGguf(file)));
    }

    private static readonly Dictionary<string, Func<byte[]>> RefusedFolding = new()
    {
        { "version 3", () => Folded(new() { ["prism.hadamard.version"] = (GgufType.UInt32, U32(3)) }).Build() },
        { "version 1 with a tied head", () => Folded(new() { ["prism.hadamard.version"] = (GgufType.UInt32, U32(1)) }).Build() },
        { "version 2 without a tied head", () => Folded(new() { ["prism.hadamard.tied_output"] = (GgufType.Bool, w => w.Write((byte)0)) }).Build() },
        { "an architecture not verified", () => Folded(architecture: "gemma3").Build() },
        { "a block of 6", () => Folded(new() { ["prism.hadamard.block_size"] = (GgufType.UInt32, U32(6)) }).Build() },
        { "a block that does not divide a width", () => Folded(new() { ["prism.hadamard.block_size"] = (GgufType.UInt32, U32(16)) }).Build() },
        { "another transform", () => Folded(new() { ["prism.hadamard.transform"] = (GgufType.String, Text("random-orthogonal")) }).Build() },
        { "another axis", () => Folded(new() { ["prism.hadamard.axis"] = (GgufType.String, Text("output")) }).Build() },
        { "another sign mode", () => Folded(new() { ["prism.hadamard.sign_mode"] = (GgufType.String, Text("seeded")) }).Build() },
        { "explicit signs without widths", () => Folded(new() { ["prism.hadamard.sign_widths"] = (GgufType.Array, Int32Array([])) }).Build() },
        { "an inverse that is not the embedding", () => Folded(new() { ["prism.hadamard.inverse_weight_names"] = (GgufType.Array, StringArray(["blk.0.attn_qkv.weight"])) }).Build() },
        { "a tied head without a turned embedding", () => Folded(new() { ["prism.hadamard.inverse_weight_names"] = (GgufType.Array, StringArray([])) }).Build() },
        { "no sign vector for a width", () => Folded(new() { ["prism.hadamard.sign_widths"] = (GgufType.Array, Int32Array([8])) }, signValues: Enumerable.Repeat(1, 8).ToArray()).Build() },
        { "no folded weights", () => Folded(weights: []).Build() },
        { "a weight off the verified path", () => Folded(weights: ["blk.0.ssm_alpha.weight"]).Build() },
        { "the embedding listed as a folded weight", () => Folded(weights: ["token_embd.weight"]).Build() },
        { "a weight listed twice", () => Folded(weights: ["blk.0.attn_qkv.weight", "blk.0.attn_qkv.weight"]).Build() },
        { "a sign that is not +-1", () => Folded(signValues: Enumerable.Repeat(2, 24).ToArray()).Build() },
        { "signs that do not fill their widths", () => Folded(signValues: Enumerable.Repeat(1, 23).ToArray()).Build() },
        { "a tied head beside an output weight", () => Folded(withOutput: true).Build() },
        { "a folded weight missing from the file", () => Folded(weights: ["blk.7.attn_qkv.weight"]).Build() },
    };

    public static TheoryData<string> Refusals
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var why in RefusedFolding.Keys) data.Add(why);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void What_the_fork_refuses_is_refused(string why)
    {
        GgufBuilder.Read(RefusedFolding[why](), file =>
        {
            var error = Assert.Throws<InvalidDataException>(() => PrismHadamard.FromGguf(file));
            output.WriteLine($"{why}: {error.Message}");
        });
    }

    [Fact]
    public void The_2B_folds_150_weights_and_its_tied_head()
    {
        var path = Bonsai2Model.TwoB();
        if (path is null)
        {
            output.WriteLine(Bonsai2Model.Missing);
            return;
        }

        using var file = GgufFile.Open(path);
        var hadamard = PrismHadamard.FromGguf(file)!;
        Assert.Equal(2, hadamard.Version);
        Assert.Equal(512, hadamard.BlockSize);
        Assert.True(hadamard.TiedOutput);
        Assert.True(hadamard.InvertsEmbedding);
        Assert.Equal(151, hadamard.FoldedWeights.Count);

        // Every ternary weight is folded and nothing kept at full precision is; two widths, two transforms.
        var ternary = file.Tensors.Where(t => t.Type == GgmlType.PTQ1_0).Select(t => t.Name).ToHashSet();
        Assert.Equal(ternary.OrderBy(n => n), hadamard.FoldedWeights.OrderBy(n => n));
        var transforms = ternary.Select(hadamard.InputOf).Distinct().ToList();
        Assert.Equal(new[] { 2048, 6144 }, transforms.Select(t => t!.Width).Order());
        Assert.All(transforms, t => Assert.False(t!.Regroups));

        var row = Random(2048, 9);
        var turned = new float[2048];
        hadamard.InputOf("token_embd.weight")!.Apply(row, turned);
        hadamard.InvertLookup(turned);
        for (var i = 0; i < row.Length; i++) Assert.InRange(MathF.Abs(turned[i] - row[i]), 0f, 1e-5f);
        output.WriteLine($"{hadamard.FoldedWeights.Count} folded weights in {transforms.Count} transforms (widths 2048 and 6144), blocks of {hadamard.BlockSize}.");
    }
}
