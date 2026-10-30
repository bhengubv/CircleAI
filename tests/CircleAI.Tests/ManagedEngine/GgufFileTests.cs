// GgufFileTests.cs
//
// The GGUF reader. Five claims:
//   1. Every value type reads back as written; scalars render as the fork renders them
//      (C's %f for floats, ties to even).
//   2. Tensors are views of the file, found where the fork lays them out.
//   3. A damaged file is refused with InvalidDataException at every cut - never read past.
//   4. What the fork refuses (bad magic, arrays of arrays, misplaced tensors...) is refused.
//   5. The real 2B reads as the pinned fork read it (fixtures/bonsai2/2B/meta.json). That one
//      needs the 429 MB model on this machine and says SKIPPED when it is not there.

using System.Text;
using System.Text.Json;
using CircleAI.Inference.Managed.Gguf;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public sealed class GgufFileTests(ITestOutputHelper output)
{
    // Writes GGUF bytes field by field as the format lays them out, so the reader is tested
    // against the layout rather than against itself.
    private sealed class Builder
    {
        private readonly MemoryStream _keys = new();
        private readonly List<(string Name, long[] Shape, uint Type, byte[] Data)> _tensors = [];
        private int _keyCount;

        public uint Magic { get; set; } = GgufFile.Magic;
        public uint Version { get; set; } = 3;
        public int Alignment { get; set; } = GgufFile.DefaultAlignment; // how the data is laid out

        public Builder Key(string key, GgufType type, Action<BinaryWriter> value)
        {
            var w = new BinaryWriter(_keys, Encoding.UTF8, leaveOpen: true);
            Text(w, key);
            w.Write((uint)type);
            value(w);
            w.Flush();
            _keyCount++;
            return this;
        }

        public Builder Tensor(string name, long[] shape, uint type, byte[] data)
        {
            _tensors.Add((name, shape, type, data));
            return this;
        }

        // offsets: where each tensor claims to start; by default where the fork lays them out.
        public byte[] Build(Func<int, long, long>? offsets = null)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(Magic);
            w.Write(Version);
            w.Write((ulong)_tensors.Count);
            w.Write((ulong)_keyCount);
            w.Write(_keys.ToArray());

            var running = 0L;
            for (var i = 0; i < _tensors.Count; i++)
            {
                var (name, shape, type, data) = _tensors[i];
                Text(w, name);
                w.Write((uint)shape.Length);
                foreach (var d in shape) w.Write((ulong)d);
                w.Write(type);
                w.Write((ulong)(offsets?.Invoke(i, running) ?? running));
                running = Align(running + data.Length);
            }
            Pad(w);
            foreach (var t in _tensors)
            {
                w.Write(t.Data);
                Pad(w);
            }
            w.Flush();
            return ms.ToArray();
        }

        public static void Text(BinaryWriter w, string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            w.Write((ulong)bytes.Length);
            w.Write(bytes);
        }

        private long Align(long n) => (n + Alignment - 1) / Alignment * Alignment;

        private void Pad(BinaryWriter w)
        {
            w.Flush();
            while (w.BaseStream.Length % Alignment != 0) w.Write((byte)0);
        }
    }

    private static readonly string[] Words = ["Ġhello", "isiZulu: ẓ", "😀", ""];

    // Every value type, a custom alignment, and one tensor of each type this engine sizes.
    private static Builder Everything()
    {
        var b = new Builder { Alignment = 64 }
            .Key("u8", GgufType.UInt8, w => w.Write((byte)200))
            .Key("i8", GgufType.Int8, w => w.Write((sbyte)-5))
            .Key("u16", GgufType.UInt16, w => w.Write((ushort)65535))
            .Key("i16", GgufType.Int16, w => w.Write((short)-300))
            .Key("u32", GgufType.UInt32, w => w.Write(4_000_000_000u))
            .Key("i32", GgufType.Int32, w => w.Write(-7))
            .Key("f32", GgufType.Float32, w => w.Write(0.95f))
            .Key("eps", GgufType.Float32, w => w.Write(1e-6f))
            .Key("yes", GgufType.Bool, w => w.Write((byte)1))
            .Key("no", GgufType.Bool, w => w.Write((byte)0))
            .Key("text", GgufType.String, w => Builder.Text(w, "qwen35"))
            .Key("u64", GgufType.UInt64, w => w.Write(18_000_000_000_000_000_000UL))
            .Key("i64", GgufType.Int64, w => w.Write(-9_000_000_000L))
            .Key("f64", GgufType.Float64, w => w.Write(1e7))
            .Key("general.alignment", GgufType.UInt32, w => w.Write(64u))
            .Key("ints", GgufType.Array, w =>
            {
                w.Write((uint)GgufType.Int32);
                w.Write(3UL);
                w.Write(11);
                w.Write(-11);
                w.Write(10);
            })
            .Key("words", GgufType.Array, w =>
            {
                w.Write((uint)GgufType.String);
                w.Write((ulong)Words.Length);
                foreach (var s in Words) Builder.Text(w, s);
            })
            .Key("floats", GgufType.Array, w =>
            {
                w.Write((uint)GgufType.Float32);
                w.Write(2UL);
                w.Write(1.5f);
                w.Write(-0.25f);
            })
            .Key("flags", GgufType.Array, w =>
            {
                w.Write((uint)GgufType.Bool);
                w.Write(2UL);
                w.Write((byte)1);
                w.Write((byte)0);
            });

        var floats = new byte[8 * 4];
        for (var i = 0; i < 8; i++) BitConverter.TryWriteBytes(floats.AsSpan(i * 4), i * 0.5f);
        b.Tensor("norm", [4, 2], (uint)GgmlType.F32, floats);
        b.Tensor("ternary", [256, 3], (uint)GgmlType.PTQ1_0, Pattern(2 * 3 * 28, 1));
        b.Tensor("q8", [64], (uint)GgmlType.Q8_0, Pattern(2 * 34, 2));
        return b;
    }

    private static byte[] Pattern(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static T WithFile<T>(byte[] bytes, Func<string, T> use)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gguf-test-{Guid.NewGuid():N}.gguf");
        File.WriteAllBytes(path, bytes);
        try
        {
            return use(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void Read(byte[] bytes, Action<GgufFile> check) => WithFile(bytes, path =>
    {
        using var file = GgufFile.Open(path);
        check(file);
        return 0;
    });

    private static InvalidDataException Refused(byte[] bytes) =>
        WithFile(bytes, path => Assert.Throws<InvalidDataException>(() => GgufFile.Open(path).Dispose()));

    [Fact]
    public void Every_value_type_reads_back_as_written()
    {
        Read(Everything().Build(), file =>
        {
            Assert.Equal(3u, file.Version);
            Assert.Equal(
                new[]
                {
                    "u8", "i8", "u16", "i16", "u32", "i32", "f32", "eps", "yes", "no", "text", "u64", "i64", "f64",
                    "general.alignment", "ints", "words", "floats", "flags",
                },
                file.Keys);

            Assert.Equal(200, file["u8"].AsInt64());
            Assert.Equal(-5, file["i8"].AsInt64());
            Assert.Equal(65535, file["u16"].AsInt64());
            Assert.Equal(-300, file["i16"].AsInt64());
            Assert.Equal(4_000_000_000, file["u32"].AsInt64());
            Assert.Equal(-7, file["i32"].AsInt64());
            Assert.Equal(0.95f, (float)file["f32"].AsDouble());
            Assert.True(file["yes"].AsBool());
            Assert.False(file["no"].AsBool());
            Assert.Equal("qwen35", file["text"].AsString());
            Assert.Throws<OverflowException>(() => file["u64"].AsInt64());
            Assert.Equal(-9_000_000_000, file["i64"].AsInt64());
            Assert.Equal(1e7, file["f64"].AsDouble());
            Assert.Throws<InvalidOperationException>(() => file["text"].AsInt64());
            Assert.Throws<KeyNotFoundException>(() => file["missing"]);
            Assert.False(file.TryGetValue("missing", out _));

            // As the fork's gguf_kv_to_str writes them.
            Assert.Equal("200", file["u8"].Render());
            Assert.Equal("-5", file["i8"].Render());
            Assert.Equal("0.950000", file["f32"].Render());
            Assert.Equal("0.000001", file["eps"].Render());
            Assert.Equal("true", file["yes"].Render());
            Assert.Equal("18000000000000000000", file["u64"].Render());
            Assert.Equal("10000000.000000", file["f64"].Render());

            var ints = file["ints"].AsArray();
            Assert.Equal(GgufType.Int32, ints.ElementType);
            Assert.Equal(new[] { 11, -11, 10 }, ints.ToInt32Array());
            var words = file["words"].AsArray();
            Assert.Equal(Words, words.ToStringArray());
            Assert.Equal(Encoding.UTF8.GetBytes(Words[1]), words.GetUtf8(1).ToArray());
            Assert.Throws<ArgumentOutOfRangeException>(() => words.GetString(Words.Length));
            Assert.Throws<InvalidOperationException>(() => words.GetInt64(0));
            Assert.Equal(-0.25, file["floats"].AsArray().GetDouble(1));
            Assert.True(file["flags"].AsArray().GetBool(0));
            Assert.False(file["flags"].AsArray().GetBool(1));
        });
    }

    [Theory]
    [InlineData(0.0078125, "0.007812")]            // 2^-7: a tie, kept even
    [InlineData(0.0234375, "0.023438")]            // 3 * 2^-7: a tie, rounded up to even
    [InlineData(2.5e-7, "0.000000")]
    [InlineData(-0.0, "-0.000000")]
    [InlineData(-1.5, "-1.500000")]
    [InlineData(1e30, "1000000000000000019884624838656.000000")]
    public void Floats_render_as_C_printf_does(double value, string expected)
    {
        var bytes = new Builder().Key("x", GgufType.Float64, w => w.Write(value)).Build();
        Read(bytes, file => Assert.Equal(expected, file["x"].Render()));
    }

    [Fact]
    public void Tensors_are_views_of_the_file_where_the_fork_lays_them_out()
    {
        var bytes = Everything().Build();
        Read(bytes, file =>
        {
            Assert.Equal(64, file.Alignment);
            Assert.Equal(0, file.DataOffset % 64);
            Assert.Equal(new[] { "norm", "ternary", "q8" }, file.Tensors.Select(t => t.Name));

            var norm = file.Tensor("norm")!;
            Assert.Equal(GgmlType.F32, norm.Type);
            Assert.Equal(4, norm.RowLength);
            Assert.Equal(2, norm.Rows);
            Assert.Equal(32, norm.ByteCount);
            Assert.Equal(file.DataOffset, norm.Offset);
            Assert.Equal(new[] { 0f, 0.5f, 1f, 1.5f, 2f, 2.5f, 3f, 3.5f }, file.Floats(norm).ToArray());

            var ternary = file.Tensor("ternary")!;
            Assert.Equal(GgmlType.PTQ1_0, ternary.Type);
            Assert.Equal(768, ternary.Elements);
            Assert.Equal(3, ternary.Rows);
            Assert.Equal(2 * 3 * 28, ternary.ByteCount);
            Assert.Equal(file.DataOffset + 64, ternary.Offset); // 32 bytes, padded to 64
            Assert.Equal(Pattern(2 * 3 * 28, 1), file.Bytes(ternary).ToArray());
            Assert.Equal(bytes.AsSpan((int)ternary.Offset, (int)ternary.ByteCount).ToArray(), file.Bytes(ternary).ToArray());

            var q8 = file.Tensor("q8")!;
            Assert.Equal(68, q8.ByteCount);
            Assert.Equal(Pattern(2 * 34, 2), file.Bytes(q8).ToArray());
            Assert.Throws<InvalidOperationException>(() => file.Floats(q8));
            Assert.Null(file.Tensor("missing"));
        });
    }

    [Fact]
    public void Without_general_alignment_the_data_section_aligns_to_32()
    {
        var bytes = new Builder()
            .Key("text", GgufType.String, w => Builder.Text(w, "abc"))
            .Tensor("a", [3], (uint)GgmlType.F32, new byte[12])
            .Tensor("b", [2], (uint)GgmlType.F16, new byte[4])
            .Build();
        Read(bytes, file =>
        {
            Assert.Equal(32, file.Alignment);
            Assert.Equal(0, file.DataOffset % 32);
            Assert.Equal(file.DataOffset + 32, file.Tensor("b")!.Offset);
        });
    }

    [Fact]
    public void A_tensor_of_a_type_this_engine_does_not_size_is_listed_but_not_read()
    {
        var bytes = new Builder()
            .Tensor("known", [8], (uint)GgmlType.F32, new byte[32])
            .Tensor("q4_0", [32], 2, new byte[18])
            .Build();
        Read(bytes, file =>
        {
            var unknown = file.Tensor("q4_0")!;
            Assert.Equal(-1, unknown.ByteCount);
            Assert.Throws<NotSupportedException>(() => file.Bytes(unknown));
            Assert.Equal(32, file.Bytes(file.Tensor("known")!).Length);
        });
    }

    [Fact]
    public void A_damaged_file_is_refused_at_every_cut_never_read_past()
    {
        var bytes = Everything().Build();
        var lastEnd = LastTensorEnd(bytes);

        // Every byte of the first 160, then every fifth; past the last tensor only padding
        // is lost and the file is still whole.
        var cuts = Enumerable.Range(0, (int)lastEnd).Where(c => c < 160 || c % 5 == 0).ToList();
        foreach (var cut in cuts)
        {
            var damaged = bytes.AsSpan(0, cut).ToArray();
            var error = Record.Exception(() => WithFile(damaged, path =>
            {
                GgufFile.Open(path).Dispose();
                return 0;
            }));
            Assert.True(error is InvalidDataException, $"a file cut at byte {cut} of {bytes.Length} gave {error?.GetType().Name ?? "no error"}");
        }

        Read(bytes.AsSpan(0, (int)lastEnd).ToArray(), file => Assert.Equal(3, file.Tensors.Count));

        static long LastTensorEnd(byte[] whole) => WithFile(whole, path =>
        {
            using var file = GgufFile.Open(path);
            var last = file.Tensors[^1];
            return last.Offset + last.ByteCount;
        });
    }

    private static readonly Dictionary<string, Func<byte[]>> RefusedFiles = new()
    {
        { "bad magic", () => new Builder { Magic = 0x46554748 }.Build() },
        { "version 1", () => new Builder { Version = 1 }.Build() },
        { "version 4", () => new Builder { Version = 4 }.Build() },
        { "an array of arrays", () => new Builder().Key("nested", GgufType.Array, w =>
            {
                w.Write((uint)GgufType.Array);
                w.Write(1UL);
                w.Write((uint)GgufType.Int32);
                w.Write(0UL);
            }).Build() },
        { "an undefined value type", () => new Builder().Key("odd", (GgufType)13, w => w.Write(0)).Build() },
        { "a key twice", () => new Builder()
            .Key("k", GgufType.UInt8, w => w.Write((byte)1))
            .Key("k", GgufType.UInt8, w => w.Write((byte)2)).Build() },
        { "an alignment of 48", () => new Builder().Key("general.alignment", GgufType.UInt32, w => w.Write(48u)).Build() },
        { "an alignment that is not a u32", () => new Builder().Key("general.alignment", GgufType.Int32, w => w.Write(32)).Build() },
        { "a tensor name twice", () => new Builder()
            .Tensor("t", [8], (uint)GgmlType.F32, new byte[32])
            .Tensor("t", [8], (uint)GgmlType.F32, new byte[32]).Build() },
        { "no dimensions", () => new Builder().Tensor("t", [], (uint)GgmlType.F32, new byte[4]).Build() },
        { "five dimensions", () => new Builder().Tensor("t", [1, 1, 1, 1, 1], (uint)GgmlType.F32, new byte[4]).Build() },
        { "a row that is not whole blocks", () => new Builder().Tensor("t", [100], (uint)GgmlType.PTQ1_0, new byte[28]).Build() },
        { "a misaligned tensor", () => new Builder()
            .Tensor("a", [8], (uint)GgmlType.F32, new byte[32])
            .Tensor("b", [8], (uint)GgmlType.F32, new byte[32]).Build((i, at) => i == 1 ? at + 4 : at) },
        { "a gap before a tensor", () => new Builder() // b claims c's place: in bounds, but not where a ended
            .Tensor("a", [8], (uint)GgmlType.F32, new byte[32])
            .Tensor("b", [8], (uint)GgmlType.F32, new byte[32])
            .Tensor("c", [8], (uint)GgmlType.F32, new byte[32]).Build((i, at) => i == 1 ? at + 32 : at) },
        { "a tensor past the end", () => new Builder()
            .Tensor("a", [8], (uint)GgmlType.F32, new byte[32]).Build((i, at) => at + 4096) },
        { "too short to be a file", () => new byte[10] },
    };

    public static TheoryData<string> Refusals
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var why in RefusedFiles.Keys) data.Add(why);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void What_the_fork_refuses_is_refused(string why)
    {
        var error = Refused(RefusedFiles[why]());
        output.WriteLine($"{why}: {error.Message}");
    }

    [Fact]
    public void Views_end_when_the_file_is_disposed()
    {
        WithFile(Everything().Build(), path =>
        {
            var file = GgufFile.Open(path);
            var tensor = file.Tensor("ternary")!;
            var words = file["words"].AsArray();
            file.Dispose();
            file.Dispose(); // twice is harmless

            Assert.Throws<ObjectDisposedException>(() => file.Bytes(tensor).Length);
            Assert.Throws<ObjectDisposedException>(() => words.GetString(0));
            return 0;
        });
    }

    [Fact]
    public void The_2B_reads_as_the_pinned_fork_read_it()
    {
        // NOT SkippableFact - one opt-in test does not justify a package. It says so out loud.
        var path = Environment.GetEnvironmentVariable("BONSAI2_2B_GGUF");
        if (string.IsNullOrEmpty(path))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CircleAI", "models", "Ternary-Bonsai-2-2B", "Ternary-Bonsai-2-2B-PTQ1_0.gguf");
        if (!File.Exists(path))
        {
            output.WriteLine($"SKIPPED: {path} is not here; set BONSAI2_2B_GGUF to the 2B PTQ1_0 file to run this.");
            return;
        }

        using var fixture = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "bonsai2", "2B", "meta.json")));
        var root = fixture.RootElement;
        using var file = GgufFile.Open(path);
        Assert.Equal(429_183_456, file.Length); // the file the fixture was made from (sha256 36061ad4...)

        // The fork keeps every key that is not an array, rendered as a string.
        var expected = root.GetProperty("keys").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        var scalars = file.Keys.Where(k => file[k].Type != GgufType.Array).ToList();
        Assert.Equal(expected.Keys.OrderBy(k => k, StringComparer.Ordinal), scalars.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var key in scalars) Assert.Equal(expected[key], file[key].Render());

        Assert.Equal(root.GetProperty("n_vocab").GetInt32(), file["tokenizer.ggml.tokens"].AsArray().Count);
        Assert.Equal(320, file.Tensors.Count);
        Assert.Equal(151, file.Tensors.Count(t => t.Type == GgmlType.PTQ1_0));
        Assert.Equal(169, file.Tensors.Count(t => t.Type == GgmlType.F32));
        Assert.Equal(new[] { 2048L, 248320L }, file.Tensor("token_embd.weight")!.Dimensions);
        var lastEnd = file.Tensors.Max(t => t.Offset + t.ByteCount);
        Assert.InRange(file.Length - lastEnd, 0, file.Alignment - 1); // nothing after the data but padding
        output.WriteLine($"{scalars.Count} keys and {file.Tensors.Count} tensors match; data at byte {file.DataOffset}.");
    }
}
