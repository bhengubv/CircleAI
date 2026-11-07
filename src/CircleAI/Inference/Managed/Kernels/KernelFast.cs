// KernelFast.cs
//
// HOW FAST CAN A TERNARY PRODUCT GO ON THIS PHONE, IN C#? A PROBE, NOT THE ENGINE.
//
// The lookup-table kernel costs about 65 cycles a block on an A73 with everything in its cache, and
// four big cores get 1.9 GB/s of weights through it where memory gives 9. These kernels use the NEON
// instructions CoreCLR gives us, to find the number the phone can really reach:
//
//   direct  the file's own PTQ1_0 bytes, each trit decoded in vectors: 16 weights a step
//   bits2   the same weights held at two bits each, laid out so a shift and a mask give 16 weights
//           of one 32-wide activation block: what a repacked copy of the model would cost
//   batch4  bits2 for four tokens at once: the weights decoded once, as reading a prompt could
//
// Each sums a block's four 32-wide parts as exact integers and scales them as the reference does, so
// the answer is the scalar kernel's up to the order of the float additions, and the probe says so.
// Nothing on the path to an answer calls this. Reached only through the soak receiver's "fast" verb.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using CircleAI.Inference.Managed.Runtime;

namespace CircleAI.Inference.Managed.Kernels;

/// <summary>Times the fastest ternary products this device can run in C#.</summary>
public static unsafe class KernelFast
{
    private enum Kind { Direct, Bits2, Tile, Pair, Quad, Batch4 }

    private const int Cols = 2048;
    private const int Rows = 6144;
    private const int Blocks = Cols / Ptq1.BlockWeights;
    private const int BufferBytes = 128 << 20;
    private const long BlocksPerToken = 10_715_136;
    private const long HeadBlocks = 3_973_120;

    // One token's activations, in the form the kernels read: the quants, each 32-wide part's scale,
    // and each part's sum of quants (a code of 0, 1 or 2 is the weight plus one, so that sum comes off).
    private readonly struct Input
    {
        public readonly sbyte* X;
        public readonly float* Dx;
        public readonly int* Sx;
        public readonly float* Bias;
        public readonly sbyte* Spread;

        public Input(Q8Activations q)
        {
            // Two columns to sixteen bytes: eight copies of one, eight of the next.
            Spread = (sbyte*)NativeMemory.AlignedAlloc((nuint)(q.Length * 8), 64);
            for (var i = 0; i < q.Length; i++)
                for (var j = 0; j < 8; j++) Spread[i / 2 * 16 + i % 2 * 8 + j] = q.Quants[i];

            X = (sbyte*)NativeMemory.AlignedAlloc((nuint)q.Length, 64);
            Dx = (float*)NativeMemory.AlignedAlloc((nuint)(q.Scales.Length * sizeof(float)), 64);
            Sx = (int*)NativeMemory.AlignedAlloc((nuint)(q.Scales.Length * sizeof(int)), 64);
            Bias = (float*)NativeMemory.AlignedAlloc((nuint)(q.Scales.Length / 4 * sizeof(float)), 64);
            for (var i = 0; i < q.Length; i++) X[i] = q.Quants[i];
            for (var i = 0; i < q.Scales.Length; i++)
            {
                Dx[i] = q.Scales[i];
                var s = 0;
                for (var j = 0; j < 32; j++) s += q.Quants[i * 32 + j];
                Sx[i] = s;
            }
            for (var b = 0; b < q.Scales.Length / 4; b++)
            {
                Bias[b] = 0;
                for (var k = 0; k < 4; k++) Bias[b] += Dx[b * 4 + k] * Sx[b * 4 + k];
            }
        }
    }

    private static Q8Activations Activation(int seed)
    {
        var x = new float[Cols];
        var rng = new Random(seed);
        for (var i = 0; i < Cols; i++) x[i] = (float)(rng.NextDouble() * 2 - 1);
        var q = new Q8Activations(Cols);
        q.Quantize(x);
        return q;
    }

    /// <summary>Hands <paramref name="say"/> one line per measurement as it ends; the last says how long it all took.</summary>
    public static void Report(Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(say);
        if (!AdvSimd.Arm64.IsSupported)
        {
            say("fast: this runtime has no ARM vector instructions");
            return;
        }
        var total = Stopwatch.StartNew();
        var big = KernelLimits.BigCores();
        var q = Enumerable.Range(1, 4).Select(Activation).ToArray();
        var inputs = q.Select(a => new Input(a)).ToArray();

        foreach (var line in Check(q, inputs)) say(line);
        say(CheckEngine(2048));
        say(CheckEngine(6144));
        foreach (var line in Ops(big)) say(line);

        var buffer = (byte*)NativeMemory.AlignedAlloc(BufferBytes, 4096);
        var scaleCount = BufferBytes / Ptq1.BlockBytes + Rows * Blocks;
        var scales = (float*)NativeMemory.AlignedAlloc((nuint)(scaleCount * sizeof(float)), 4096);
        try
        {
            Random.Shared.NextBytes(new Span<byte>(buffer, BufferBytes));
            new Span<float>(scales, scaleCount).Fill(0.02f);
            foreach (var line in Ablation(buffer, scales, inputs[0], big)) say(line);
            foreach (var kind in new[] { Kind.Quad, Kind.Pair, Kind.Batch4 })
            {
                var name = kind.ToString().ToLowerInvariant();
                var one = Rate(kind, buffer, scales, inputs, big[..1], 1, 0.6);
                say(F($"fast, {name}, one big core, one matrix: {1e9 / one:F1} ns a block, {one * Stride(kind) / 1e9:F2} GB/s of weights"));
                for (var n = 1; n <= big.Length; n++)
                {
                    var rate = Rate(kind, buffer, scales, inputs, big[..n], (BufferBytes - (2 << 20)) / n, 1.0);
                    say(F($"fast, {name}, {n} big from memory: {rate / 1e6:F1} M blocks/s, {rate * Stride(kind) / 1e9:F2} GB/s of weights; a token's products {BlocksPerToken / rate * 1000:F0} ms, with the head {(BlocksPerToken + HeadBlocks) / rate * 1000:F0} ms"));
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
            NativeMemory.AlignedFree(scales);
        }
        say(F($"fast done in {total.Elapsed.TotalSeconds:F0} s"));
    }

    private static int Stride(Kind kind) => kind == Kind.Direct ? Ptq1.BlockBytes : 32;

    // ---- several tokens at once ----

    /// <summary>Whether the batched product equals the engine's product here, and how fast it goes.</summary>
    public static void Batches(Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(say);
        const int cols = 2048, rows = 6144, matrices = 24, threads = 4;
        var total = Stopwatch.StartNew();
        var rowBytes = cols / Ptq1.BlockWeights * Ptq1.BlockBytes;
        var bytes = new byte[rows * rowBytes];
        new Random(11).NextBytes(bytes);
        for (var b = 26; b < bytes.Length; b += Ptq1.BlockBytes) BitConverter.TryWriteBytes(bytes.AsSpan(b, 2), (Half)0.02f);

        using var pool = new WorkerPool(threads);
        var all = new Ptq1PairMatrix[matrices];
        for (var i = 0; i < matrices; i++) all[i] = new Ptq1PairMatrix(bytes, rows, cols, pool);

        var q = new Q8Activations[Ptq1PairBatch.Lanes];
        var inputs = new Ptq1PairInput[Ptq1PairBatch.Lanes];
        var lanes = new Ptq1PairBatch(cols, threads);
        var outputs = new float[Ptq1PairBatch.Lanes][];
        var one = new float[Ptq1PairBatch.Lanes][];
        for (var i = 0; i < q.Length; i++)
        {
            var rng = new Random(100 + i);
            var x = new float[cols];
            for (var c = 0; c < cols; c++) x[c] = (float)(rng.NextDouble() * 2 - 1);
            q[i] = new Q8Activations(cols);
            q[i].Quantize(x);
            inputs[i] = new Ptq1PairInput(cols);
            inputs[i].Set(q[i]);
            lanes.Set(i, q[i]);
            outputs[i] = new float[rows];
            one[i] = new float[rows];
            all[0].MatVec(inputs[i], one[i], pool);
        }

        all[0].MatVec(lanes, q.Length, outputs, pool);
        var differ = 0;
        for (var i = 0; i < q.Length; i++)
            for (var r = 0; r < rows; r++)
                if (BitConverter.SingleToInt32Bits(one[i][r]) != BitConverter.SingleToInt32Bits(outputs[i][r])) differ++;
        say(F($"batch, check: {differ} of {rows * q.Length} values differ from the engine's product"));

        var blocks = (double)matrices * rows * (cols / Ptq1.BlockWeights);
        using (pool.KeepSpinning())
        {
            void Time(Action pass, int tokens, string name)
            {
                pass();
                var runs = 0;
                var started = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(started).TotalSeconds < 2.5)
                {
                    pass();
                    runs++;
                }
                var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                say(F($"batch, {name}: a token's products {seconds / runs / tokens / blocks * BlocksPerToken * 1000:F1} ms"));
            }

            Time(() => { foreach (var m in all) m.MatVec(inputs[0], one[0], pool); }, 1, "one token (the engine's product)");
            Time(() => { foreach (var m in all) m.MatVec(lanes, q.Length, outputs, pool); }, q.Length, "eight lanes");
        }
        say(F($"batch done in {total.Elapsed.TotalSeconds:F0} s"));
    }

    // ---- is it right ----

    private static IEnumerable<string> Check(Q8Activations[] q, Input[] inputs)
    {
        const int rows = 64;
        var rng = new Random(7);
        var matrix = new byte[rows * Blocks * Ptq1.BlockBytes];
        rng.NextBytes(matrix);
        for (var b = 26; b < matrix.Length; b += Ptq1.BlockBytes) BitConverter.TryWriteBytes(matrix.AsSpan(b, 2), (Half)0.02f);
        var scales = Ptq1.BlockScales(matrix);
        var packed = new byte[rows * Blocks * 32];
        Repack(matrix, packed);

        var lines = new List<string>();
        var outputs = Enumerable.Range(0, 4).Select(_ => new float[rows]).ToArray();
        var want = new float[4][];
        for (var t = 0; t < 4; t++)
        {
            want[t] = new float[rows];
            for (var r = 0; r < rows; r++)
                want[t][r] = Ptq1.Dot(matrix.AsSpan(r * Blocks * Ptq1.BlockBytes, Blocks * Ptq1.BlockBytes), q[t], KernelPath.Scalar);
        }
        var top = want.SelectMany(w => w).Max(MathF.Abs);

        fixed (byte* m = matrix)
        fixed (byte* p = packed)
        fixed (float* s = scales)
        fixed (float* o0 = outputs[0])
        fixed (float* o1 = outputs[1])
        fixed (float* o2 = outputs[2])
        fixed (float* o3 = outputs[3])
        {
            DirectRows(m, s, inputs[0], o0, rows);
            lines.Add(F($"fast, check, direct: off by {Off(want[0], outputs[0]):G3} against values up to {top:G3}"));
            Bits2Rows(p, s, inputs[0], o0, rows);
            lines.Add(F($"fast, check, bits2: off by {Off(want[0], outputs[0]):G3}"));
            Batch4Rows(p, s, inputs[0], inputs[1], inputs[2], inputs[3], o0, o1, o2, o3, rows);
            lines.Add(F($"fast, check, batch4: off by {new[] { Off(want[0], outputs[0]), Off(want[1], outputs[1]), Off(want[2], outputs[2]), Off(want[3], outputs[3]) }.Max():G3}"));
        }

        var tiled = new byte[packed.Length];
        var tileScales = new float[scales.Length];
        Tile(matrix, scales, rows, tiled, tileScales);
        fixed (byte* p = tiled)
        fixed (float* s = tileScales)
        fixed (float* o0 = outputs[0])
        {
            TileRows(p, s, inputs[0], o0, rows / 16);
        }
        lines.Add(F($"fast, check, tile: off by {Off(want[0], outputs[0]):G3}"));

        var paired = new byte[packed.Length];
        Pair(matrix, rows, paired);
        fixed (byte* p = paired)
        fixed (float* s = tileScales)
        fixed (float* o0 = outputs[0])
        {
            PairRows(p, s, inputs[0], o0, rows / 16);
        }
        lines.Add(F($"fast, check, pair: off by {Off(want[0], outputs[0]):G3}"));

        var quads = new byte[packed.Length];
        var quadScales = new float[scales.Length];
        Quad(matrix, scales, rows, quads, quadScales);
        fixed (byte* p = quads)
        fixed (float* s = quadScales)
        fixed (float* o0 = outputs[0])
        {
            QuadRows(p, s, inputs[0], o0, rows / 32);
        }
        lines.Add(F($"fast, check, quad: off by {Off(want[0], outputs[0]):G3}"));
        return lines;
    }

    // The file's blocks for QuadRows: tile of 32 rows, block, eight columns, four 16-byte vectors of
    // eight rows each, laid out within a vector as for PairRows; a block's 32 scales together.
    private static void Quad(byte[] matrix, float[] scales, int rows, byte[] quads, float[] quadScales)
    {
        Span<sbyte> trits = stackalloc sbyte[Ptq1.BlockWeights];
        for (var r = 0; r < rows; r++)
        {
            var (tile, part, lane) = (r / 32, r % 32 / 8, r % 8);
            for (var b = 0; b < Blocks; b++)
            {
                Ptq1.DecodeScalar(matrix.AsSpan((r * Blocks + b) * Ptq1.BlockBytes, Ptq1.BlockBytes), trits);
                quadScales[(tile * Blocks + b) * 32 + r % 32] = scales[r * Blocks + b];
                for (var col = 0; col < Ptq1.BlockWeights; col++)
                {
                    var (group, field, upper) = (col / 8, col % 8 / 2, col % 2);
                    quads[((tile * Blocks + b) * 16 + group) * 64 + part * 16 + upper * 8 + lane] |= (byte)((trits[col] + 1) << (2 * field));
                }
            }
        }
    }

    // The file's blocks for PairRows: tile, block, eight columns, then two 16-byte vectors, rows 0-7 and
    // 8-15. In a vector a byte's four fields are four column pairs: low lanes the first column of each
    // pair, high lanes the second.
    private static void Pair(byte[] matrix, int rows, byte[] paired)
    {
        Span<sbyte> trits = stackalloc sbyte[Ptq1.BlockWeights];
        for (var r = 0; r < rows; r++)
        {
            var (tile, half, lane) = (r / 16, r % 16 / 8, r % 8);
            for (var b = 0; b < Blocks; b++)
            {
                Ptq1.DecodeScalar(matrix.AsSpan((r * Blocks + b) * Ptq1.BlockBytes, Ptq1.BlockBytes), trits);
                for (var col = 0; col < Ptq1.BlockWeights; col++)
                {
                    var (group, field, upper) = (col / 8, col % 8 / 2, col % 2);
                    paired[((tile * Blocks + b) * 16 + group) * 32 + half * 16 + upper * 8 + lane] |= (byte)((trits[col] + 1) << (2 * field));
                }
            }
        }
    }

    // The file's blocks as sixteen-row tiles: tile, block, four columns, sixteen rows, two bits a
    // weight - and the block scales the same way, a block's sixteen together.
    private static void Tile(byte[] matrix, float[] scales, int rows, byte[] tiled, float[] tileScales)
    {
        Span<sbyte> trits = stackalloc sbyte[Ptq1.BlockWeights];
        for (var r = 0; r < rows; r++)
        {
            var (tile, lane) = (r / 16, r % 16);
            for (var b = 0; b < Blocks; b++)
            {
                Ptq1.DecodeScalar(matrix.AsSpan((r * Blocks + b) * Ptq1.BlockBytes, Ptq1.BlockBytes), trits);
                tileScales[(tile * Blocks + b) * 16 + lane] = scales[r * Blocks + b];
                for (var col = 0; col < Ptq1.BlockWeights; col++)
                    tiled[(tile * Blocks + b) * 512 + col / 4 * 16 + lane] |= (byte)((trits[col] + 1) << (2 * (col % 4)));
            }
        }
    }

    private static float Off(float[] want, float[] got) => want.Zip(got, (a, b) => MathF.Abs(a - b)).Max();

    // The kernel the engine runs, on this phone's vectors, against the scalar reference.
    private static string CheckEngine(int cols)
    {
        const int rows = 64;
        var rng = new Random(cols);
        var rowBytes = cols / Ptq1.BlockWeights * Ptq1.BlockBytes;
        var matrix = new byte[rows * rowBytes];
        rng.NextBytes(matrix);
        for (var b = 26; b < matrix.Length; b += Ptq1.BlockBytes) BitConverter.TryWriteBytes(matrix.AsSpan(b, 2), (Half)0.02f);
        var x = new float[cols];
        for (var i = 0; i < cols; i++) x[i] = (float)(rng.NextDouble() * 2 - 1);
        var q = new Q8Activations(cols);
        q.Quantize(x);
        var input = new Ptq1PairInput(cols);
        input.Set(q);
        var got = new float[rows];
        new Ptq1PairMatrix(matrix, rows, cols).MatVec(input, got);

        float worst = 0, top = 0;
        for (var r = 0; r < rows; r++)
        {
            var want = Ptq1.Dot(matrix.AsSpan(r * rowBytes, rowBytes), q, KernelPath.Scalar);
            worst = MathF.Max(worst, MathF.Abs(want - got[r]));
            top = MathF.Max(top, MathF.Abs(want));
        }
        return F($"fast, check, the engine's kernel at width {cols}: off by {worst:G3} against values up to {top:G3}");
    }

    // The file's blocks as two bits a weight: byte i holds the codes of weights i, i+32, i+64, i+96.
    private static void Repack(byte[] matrix, byte[] packed)
    {
        Span<sbyte> trits = stackalloc sbyte[Ptq1.BlockWeights];
        var blocks = matrix.Length / Ptq1.BlockBytes;
        for (var b = 0; b < blocks; b++)
        {
            Ptq1.DecodeScalar(matrix.AsSpan(b * Ptq1.BlockBytes, Ptq1.BlockBytes), trits);
            for (var i = 0; i < 32; i++)
                packed[b * 32 + i] = (byte)((trits[i] + 1) | ((trits[i + 32] + 1) << 2) | ((trits[i + 64] + 1) << 4) | ((trits[i + 96] + 1) << 6));
        }
    }

    // ---- the rate ----

    // Blocks a second, each core walking its own matrices around `spanEach` bytes, after a warm-up.
    private static double Rate(Kind kind, byte* buffer, float* scales, Input[] inputs, int[] cores, long spanEach, double seconds)
    {
        var matrixBytes = (long)Rows * Blocks * Stride(kind);
        var perPass = (long)Rows * Blocks * (kind == Kind.Batch4 ? 4 : 1);
        var matrices = Math.Max(1, spanEach / matrixBytes);
        var done = new long[cores.Length];
        var taken = new double[cores.Length];
        var buf = (nint)buffer;
        var sc = (nint)scales;
        var slice = (long)BufferBytes / cores.Length;
        var scaleSlice = (long)BufferBytes / Ptq1.BlockBytes / cores.Length;
        KernelLimits.OnCores(cores, i =>
        {
            var o = (float*)NativeMemory.AlignedAlloc(4 * Rows * sizeof(float), 64);
            try
            {
                var p = (byte*)buf + i * slice;
                var s = (float*)sc + i * scaleSlice;
                long at = 0;
                var warm = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 3;
                while (Stopwatch.GetTimestamp() < warm)
                {
                    Pass(kind, p + at * matrixBytes, s + at * Rows * Blocks, inputs, o);
                    at = (at + 1) % matrices;
                }
                var started = Stopwatch.GetTimestamp();
                var until = started + (long)(seconds * Stopwatch.Frequency);
                long passes = 0;
                while (Stopwatch.GetTimestamp() < until)
                {
                    Pass(kind, p + at * matrixBytes, s + at * Rows * Blocks, inputs, o);
                    at = (at + 1) % matrices;
                    passes++;
                }
                done[i] = passes * perPass;
                taken[i] = Stopwatch.GetElapsedTime(started).TotalSeconds;
            }
            finally
            {
                NativeMemory.AlignedFree(o);
            }
        });
        return done.Sum() / taken.Max();
    }

    private static void Pass(Kind kind, byte* w, float* d, Input[] inputs, float* o)
    {
        switch (kind)
        {
            case Kind.Direct: DirectRows(w, d, inputs[0], o, Rows); break;
            case Kind.Bits2: Bits2Rows(w, d, inputs[0], o, Rows); break;
            case Kind.Tile: TileRows(w, d, inputs[0], o, Rows / 16); break;
            case Kind.Pair: PairRows(w, d, inputs[0], o, Rows / 16); break;
            case Kind.Quad: QuadRows(w, d, inputs[0], o, Rows / 32); break;
            default: Batch4Rows(w, d, inputs[0], inputs[1], inputs[2], inputs[3], o, o + Rows, o + 2 * Rows, o + 3 * Rows, Rows); break;
        }
    }

    // ---- what one instruction costs ----

    // Each loop runs eight independent chains of one instruction, so the time per instruction is the
    // chip's throughput for it: the numbers a kernel's instruction mix has to be chosen by.
    private const long OpPasses = 4_000_000;

    private static IEnumerable<string> Ops(int[] big)
    {
        var lines = new List<string>();
        var ghz = 1.82;
        void Time(string name, Func<long, int> run, int perPass = 8)
        {
            var result = new double[1];
            KernelLimits.OnCores(big[..1], _ =>
            {
                run(OpPasses / 8);
                var started = Stopwatch.GetTimestamp();
                run(OpPasses);
                result[0] = Stopwatch.GetElapsedTime(started).TotalSeconds;
            });
            lines.Add(F($"op {name}: {result[0] * ghz * 1e9 / (OpPasses * perPass):F2} cycles each"));
        }
        Time("and.16b", OpAnd);
        Time("ushr.16b", OpShift);
        Time("add.16b", OpAdd);
        Time("smlal", OpSmlal);
        Time("smlal2", OpSmlal2);
        Time("mla.16b", OpMla);
        Time("tbl one register", OpTbl);
        Time("tbl two registers", OpTbl2);
        Time("saddw", OpSaddw);
        Time("saddw2", OpSaddw2);
        Time("sadalp.16b", OpSadalp);
        Time("addp.8h", OpAddp);
        Time("fmla.4s", OpFmla);
        Time("and + smlal mixed", OpMixed, 16);
        Time("tbl + add mixed", OpTblAdd, 16);
        return lines;
    }

    private static readonly Vector128<byte> Ones = Vector128.Create((byte)0x55);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int OpAnd(long n)
    {
        var m = Ones;
        Vector128<byte> a = m, b = m, c = m, d = m, e = m, f = m, g = m, h = m;
        for (long i = 0; i < n; i++)
        {
            a = AdvSimd.And(a, m); b = AdvSimd.And(b, m); c = AdvSimd.And(c, m); d = AdvSimd.And(d, m);
            e = AdvSimd.And(e, m); f = AdvSimd.And(f, m); g = AdvSimd.And(g, m); h = AdvSimd.And(h, m);
        }
        return (a | b | c | d | e | f | g | h).GetElement(0);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int OpShift(long n)
    {
        var m = Ones;
        Vector128<byte> a = m, b = m, c = m, d = m, e = m, f = m, g = m, h = m;
        for (long i = 0; i < n; i++)
        {
            a = AdvSimd.ShiftRightLogical(a, 1); b = AdvSimd.ShiftRightLogical(b, 1); c = AdvSimd.ShiftRightLogical(c, 1); d = AdvSimd.ShiftRightLogical(d, 1);
            e = AdvSimd.ShiftRightLogical(e, 1); f = AdvSimd.ShiftRightLogical(f, 1); g = AdvSimd.ShiftRightLogical(g, 1); h = AdvSimd.ShiftRightLogical(h, 1);
        }
        return (a | b | c | d | e | f | g | h).GetElement(0);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int OpAdd(long n)
    {
        var m = Ones;
        Vector128<byte> a = m, b = m, c = m, d = m, e = m, f = m, g = m, h = m;
        for (long i = 0; i < n; i++)
        {
            a = AdvSimd.Add(a, m); b = AdvSimd.Add(b, m); c = AdvSimd.Add(c, m); d = AdvSimd.Add(d, m);
            e = AdvSimd.Add(e, m); f = AdvSimd.Add(f, m); g = AdvSimd.Add(g, m); h = AdvSimd.Add(h, m);
        }
        return (a | b | c | d | e | f | g | h).GetElement(0);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int OpSmlal(long n)
    {
        var m = Ones.AsSByte();
        var l = m.GetLower();
        Vector128<short> a = default, b = default, c = default, d = default, e = default, f = default, g = default, h = default;
        for (long i = 0; i < n; i++)
        {
            a = AdvSimd.MultiplyWideningLowerAndAdd(a, l, l); b = AdvSimd.MultiplyWideningLowerAndAdd(b, l, l);
            c = AdvSimd.MultiplyWideningLowerAndAdd(c, l, l); d = AdvSimd.MultiplyWideningLowerAndAdd(d, l, l);
            e = AdvSimd.MultiplyWideningLowerAndAdd(e, l, l); f = AdvSimd.MultiplyWideningLowerAndAdd(f, l, l);
            g = AdvSimd.MultiplyWideningLowerAndAdd(g, l, l); h = AdvSimd.MultiplyWideningLowerAndAdd(h, l, l);
        }
        return (a | b | c | d | e | f | g | h).GetElement(0);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int OpSmlal2(long n)
    {
        var m = Ones.AsSByte();
        Vector128<short> a = default, b = default, c = default, d = default, e = default, f = default, g = default, h = default;
        for (long i = 0; i < n; i++)
        {
            a = AdvSimd.MultiplyWideningUpperAndAdd(a, m, m); b = AdvSimd.MultiplyWideningUpperAndAdd(b, m, m);
            c = AdvSimd.MultiplyWideningUpperAndAdd(c, m, m); d = AdvSimd.MultiplyWideningUpperAndAdd(d, m, m);
            e = AdvSimd.MultiplyWideningUpperAndAdd(e, m, m); f = AdvSimd.MultiplyWideningUpperAndAdd(f, m, m);
            g = AdvSimd.MultiplyWideningUpperAndAdd(g, m, m); h = AdvSimd.MultiplyWideningUpperAndAdd(h, m, m);
        }
        return (a | b | c | d | e | f | g | h).GetElement(0);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int OpMla(long n)
    {
        var m = Ones.AsSByte();
        Vector128<sbyte> a = m, b = m, c = m, d = m, e = m, f = m, g = m, h = m;
        for (long i = 0; i < n; i++)
        {
            a = AdvSimd.MultiplyAdd(a, m, m); b = AdvSimd.MultiplyAdd(b, m, m); c = AdvSimd.MultiplyAdd(c, m, m); d = AdvSimd.MultiplyAdd(d, m, m);
            e = AdvSimd.MultiplyAdd(e, m, m); f = AdvSimd.MultiplyAdd(f, m, m); g = AdvSimd.MultiplyAdd(g, m, m); h = AdvSimd.MultiplyAdd(h, m, m);
        }
        return (a | b | c | d | e | f | g | h).GetElement(0);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int OpTbl(long n)
    {
        var t = Ones;
        var m = Vector128.Create((byte)3);
        Vector128<byte> a = m, b = m, c = m, d = m, e = m, f = m, g = m, h = m;
        for (long i = 0; i < n; i++)
        {
            a = AdvSimd.Arm64.VectorTableLookup(t, a); b = AdvSimd.Arm64.VectorTableLookup(t, b);
            c = AdvSimd.Arm64.VectorTableLookup(t, c); d = AdvSimd.Arm64.VectorTableLookup(t, d);
            e = AdvSimd.Arm64.VectorTableLookup(t, e); f = AdvSimd.Arm64.VectorTableLookup(t, f);
            g = AdvSimd.Arm64.VectorTableLookup(t, g); h = AdvSimd.Arm64.VectorTableLookup(t, h);
        }
        return (a | b | c | d | e | f | g | h).GetElement(0);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int OpTbl2(long n)
    {
        var t = (Ones, Ones);
        var m = Vector128.Create((byte)3);
        Vector128<byte> a = m, b = m, c = m, d = m, e = m, f = m, g = m, h = m;
        for (long i = 0; i < n; i++)
        {
            a = AdvSimd.Arm64.VectorTableLookup(t, a); b = AdvSimd.Arm64.VectorTableLookup(t, b);
            c = AdvSimd.Arm64.VectorTableLookup(t, c); d = AdvSimd.Arm64.VectorTableLookup(t, d);
            e = AdvSimd.Arm64.VectorTableLookup(t, e); f = AdvSimd.Arm64.VectorTableLookup(t, f);
            g = AdvSimd.Arm64.VectorTableLookup(t, g); h = AdvSimd.Arm64.VectorTableLookup(t, h);
        }
        return (a | b | c | d | e | f | g | h).GetElement(0);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int OpSaddw(long n)
    {
        var l = Ones.AsSByte().GetLower();
        Vector128<short> a = default, b = default, c = default, d = default, e = default, f = default, g = default, h = default;
        for (long i = 0; i < n; i++)
        {
            a = AdvSimd.AddWideningLower(a, l); b = AdvSimd.AddWideningLower(b, l); c = AdvSimd.AddWideningLower(c, l); d = AdvSimd.AddWideningLower(d, l);
            e = AdvSimd.AddWideningLower(e, l); f = AdvSimd.AddWideningLower(f, l); g = AdvSimd.AddWideningLower(g, l); h = AdvSimd.AddWideningLower(h, l);
        }
        return (a | b | c | d | e | f | g | h).GetElement(0);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int OpSaddw2(long n)
    {
        var m = Ones.AsSByte();
        Vector128<short> a = default, b = default, c = default, d = default, e = default, f = default, g = default, h = default;
        for (long i = 0; i < n; i++)
        {
            a = AdvSimd.AddWideningUpper(a, m); b = AdvSimd.AddWideningUpper(b, m); c = AdvSimd.AddWideningUpper(c, m); d = AdvSimd.AddWideningUpper(d, m);
            e = AdvSimd.AddWideningUpper(e, m); f = AdvSimd.AddWideningUpper(f, m); g = AdvSimd.AddWideningUpper(g, m); h = AdvSimd.AddWideningUpper(h, m);
        }
        return (a | b | c | d | e | f | g | h).GetElement(0);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int OpSadalp(long n)
    {
        var m = Ones.AsSByte();
        Vector128<short> a = default, b = default, c = default, d = default, e = default, f = default, g = default, h = default;
        for (long i = 0; i < n; i++)
        {
            a = AdvSimd.AddPairwiseWideningAndAdd(a, m); b = AdvSimd.AddPairwiseWideningAndAdd(b, m); c = AdvSimd.AddPairwiseWideningAndAdd(c, m); d = AdvSimd.AddPairwiseWideningAndAdd(d, m);
            e = AdvSimd.AddPairwiseWideningAndAdd(e, m); f = AdvSimd.AddPairwiseWideningAndAdd(f, m); g = AdvSimd.AddPairwiseWideningAndAdd(g, m); h = AdvSimd.AddPairwiseWideningAndAdd(h, m);
        }
        return (a | b | c | d | e | f | g | h).GetElement(0);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int OpAddp(long n)
    {
        var m = Ones.AsInt16();
        Vector128<short> a = m, b = m, c = m, d = m, e = m, f = m, g = m, h = m;
        for (long i = 0; i < n; i++)
        {
            a = AdvSimd.Arm64.AddPairwise(a, m); b = AdvSimd.Arm64.AddPairwise(b, m); c = AdvSimd.Arm64.AddPairwise(c, m); d = AdvSimd.Arm64.AddPairwise(d, m);
            e = AdvSimd.Arm64.AddPairwise(e, m); f = AdvSimd.Arm64.AddPairwise(f, m); g = AdvSimd.Arm64.AddPairwise(g, m); h = AdvSimd.Arm64.AddPairwise(h, m);
        }
        return (a | b | c | d | e | f | g | h).GetElement(0);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int OpFmla(long n)
    {
        var m = Vector128.Create(1.0001f);
        Vector128<float> a = m, b = m, c = m, d = m, e = m, f = m, g = m, h = m;
        for (long i = 0; i < n; i++)
        {
            a = AdvSimd.FusedMultiplyAdd(a, m, m); b = AdvSimd.FusedMultiplyAdd(b, m, m); c = AdvSimd.FusedMultiplyAdd(c, m, m); d = AdvSimd.FusedMultiplyAdd(d, m, m);
            e = AdvSimd.FusedMultiplyAdd(e, m, m); f = AdvSimd.FusedMultiplyAdd(f, m, m); g = AdvSimd.FusedMultiplyAdd(g, m, m); h = AdvSimd.FusedMultiplyAdd(h, m, m);
        }
        return (int)(a + b + c + d + e + f + g + h).GetElement(0);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int OpMixed(long n)
    {
        var m = Ones;
        var q = Ones.AsSByte();
        var l = q.GetLower();
        Vector128<byte> p = m, r = m, s = m, t = m, u = m, v = m, w = m, x = m;
        Vector128<short> a = default, b = default, c = default, d = default, e = default, f = default, g = default, h = default;
        for (long i = 0; i < n; i++)
        {
            p = AdvSimd.And(p, m); a = AdvSimd.MultiplyWideningLowerAndAdd(a, l, l);
            r = AdvSimd.And(r, m); b = AdvSimd.MultiplyWideningLowerAndAdd(b, l, l);
            s = AdvSimd.And(s, m); c = AdvSimd.MultiplyWideningLowerAndAdd(c, l, l);
            t = AdvSimd.And(t, m); d = AdvSimd.MultiplyWideningLowerAndAdd(d, l, l);
            u = AdvSimd.And(u, m); e = AdvSimd.MultiplyWideningLowerAndAdd(e, l, l);
            v = AdvSimd.And(v, m); f = AdvSimd.MultiplyWideningLowerAndAdd(f, l, l);
            w = AdvSimd.And(w, m); g = AdvSimd.MultiplyWideningLowerAndAdd(g, l, l);
            x = AdvSimd.And(x, m); h = AdvSimd.MultiplyWideningLowerAndAdd(h, l, l);
        }
        return (a | b | c | d | e | f | g | h).GetElement(0) + (p | r | s | t | u | v | w | x).GetElement(0);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int OpTblAdd(long n)
    {
        var t = Ones;
        var i0 = Vector128.Create((byte)3);
        var m = Ones;
        Vector128<byte> a = m, b = m, c = m, d = m, e = m, f = m, g = m, h = m;
        for (long i = 0; i < n; i++)
        {
            a = AdvSimd.Add(a, AdvSimd.Arm64.VectorTableLookup(t, i0));
            b = AdvSimd.Add(b, AdvSimd.Arm64.VectorTableLookup(t, i0));
            c = AdvSimd.Add(c, AdvSimd.Arm64.VectorTableLookup(t, i0));
            d = AdvSimd.Add(d, AdvSimd.Arm64.VectorTableLookup(t, i0));
            e = AdvSimd.Add(e, AdvSimd.Arm64.VectorTableLookup(t, i0));
            f = AdvSimd.Add(f, AdvSimd.Arm64.VectorTableLookup(t, i0));
            g = AdvSimd.Add(g, AdvSimd.Arm64.VectorTableLookup(t, i0));
            h = AdvSimd.Add(h, AdvSimd.Arm64.VectorTableLookup(t, i0));
        }
        return (a | b | c | d | e | f | g | h).GetElement(0);
    }

    // ---- what limits the tile kernel: the loop with one thing left out, timing only ----

    private static IEnumerable<string> Ablation(byte* buffer, float* scales, Input input, int[] big)
    {
        var lines = new List<string>();
        var clock = new double[1];
        var buf = (nint)buffer;
        var sc = (nint)scales;
        var inp = input;
        void Time(string name, int which)
        {
            var seconds = new double[1];
            var rounds = 0L;
            KernelLimits.OnCores(big[..1], _ =>
            {
                var o = (float*)NativeMemory.AlignedAlloc(Rows * sizeof(float), 64);
                try
                {
                    Variant(which, (byte*)buf, (float*)sc, inp, o);
                    var until = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
                    while (Stopwatch.GetTimestamp() < until) Variant(which, (byte*)buf, (float*)sc, inp, o);
                    var started = Stopwatch.GetTimestamp();
                    var stop = started + Stopwatch.Frequency;
                    while (Stopwatch.GetTimestamp() < stop)
                    {
                        Variant(which, (byte*)buf, (float*)sc, inp, o);
                        rounds++;
                    }
                    seconds[0] = Stopwatch.GetElapsedTime(started).TotalSeconds;
                    clock[0] = KernelLimits.ClockNowGhz();
                }
                finally
                {
                    NativeMemory.AlignedFree(o);
                }
            });
            var ns = seconds[0] * 1e9 / (rounds * (double)(Rows * Blocks));
            lines.Add(F($"ablation, {name}: {ns:F1} ns a block at {clock[0]:F2} GHz = {ns * clock[0]:F0} cycles"));
        }
        Time("whole loop", 0);
        Time("no broadcast of the activation", 1);
        Time("no unpacking of the weights", 2);
        Time("multiplies and the weight load only", 3);
        Time("multiplies only", 4);
        return lines;
    }

    private static void Variant(int which, byte* w, float* d, in Input input, float* o)
    {
        switch (which)
        {
            case 0: TileRows(w, d, input, o, Rows / 16); break;
            case 1: TileNoBroadcast(w, input, o); break;
            case 2: TileNoUnpack(w, input, o); break;
            case 3: TileMacLoad(w, input, o); break;
            default: TileMacOnly(w, input, o); break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void TileNoBroadcast(byte* weights, in Input input, float* output)
    {
        var mask = Vector128.Create((byte)3);
        var second = Vector128.Create((byte)0x0C);
        var third = Vector128.Create((byte)0x30);
        var p = weights;
        var e0 = AdvSimd.LoadAndReplicateToVector128(input.X);
        var e1 = AdvSimd.LoadAndReplicateToVector128(input.X + 1);
        var e2 = AdvSimd.LoadAndReplicateToVector128(input.X + 2);
        var e3 = AdvSimd.LoadAndReplicateToVector128(input.X + 3);
        var l0e = e0.GetLower(); var l1e = e1.GetLower(); var l2e = e2.GetLower(); var l3e = e3.GetLower();
        Vector128<short> l0 = default, h0 = default, l1 = default, h1 = default, l2 = default, h2 = default;
        for (var q = 0; q < Rows / 16 * Blocks * 32; q++, p += 16)
        {
            var v = AdvSimd.LoadVector128(p);
            var c = AdvSimd.ShiftRightLogical(v, 6).AsSByte();
            h0 = AdvSimd.MultiplyWideningUpperAndAdd(h0, c, e3);
            l0 = AdvSimd.MultiplyWideningLowerAndAdd(l0, c.GetLower(), l3e);
            c = AdvSimd.And(v, third).AsSByte();
            h2 = AdvSimd.MultiplyWideningUpperAndAdd(h2, c, e2);
            l2 = AdvSimd.MultiplyWideningLowerAndAdd(l2, c.GetLower(), l2e);
            c = AdvSimd.And(v, second).AsSByte();
            h1 = AdvSimd.MultiplyWideningUpperAndAdd(h1, c, e1);
            l1 = AdvSimd.MultiplyWideningLowerAndAdd(l1, c.GetLower(), l1e);
            var cLast = AdvSimd.And(v, mask).AsSByte();
            h0 = AdvSimd.MultiplyWideningUpperAndAdd(h0, cLast, e0);
            l0 = AdvSimd.MultiplyWideningLowerAndAdd(l0, cLast.GetLower(), l0e);
        }
        AdvSimd.Store((short*)output, l0 + h0 + l1 + h1 + l2 + h2);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void TileNoUnpack(byte* weights, in Input input, float* output)
    {
        var p = weights;
        var xb = input.X;
        Vector128<short> l0 = default, h0 = default, l1 = default, h1 = default, l2 = default, h2 = default;
        for (var q = 0; q < Rows / 16 * Blocks * 32; q++, p += 16)
        {
            var c = AdvSimd.LoadVector128(p).AsSByte();
            var cl = c.GetLower();
            var x = xb + (q & 255) * 4;
            var e = AdvSimd.LoadAndReplicateToVector128(x + 3);
            h0 = AdvSimd.MultiplyWideningUpperAndAdd(h0, c, e);
            l0 = AdvSimd.MultiplyWideningLowerAndAdd(l0, cl, e.GetLower());
            e = AdvSimd.LoadAndReplicateToVector128(x + 2);
            h2 = AdvSimd.MultiplyWideningUpperAndAdd(h2, c, e);
            l2 = AdvSimd.MultiplyWideningLowerAndAdd(l2, cl, e.GetLower());
            e = AdvSimd.LoadAndReplicateToVector128(x + 1);
            h1 = AdvSimd.MultiplyWideningUpperAndAdd(h1, c, e);
            l1 = AdvSimd.MultiplyWideningLowerAndAdd(l1, cl, e.GetLower());
            var eLast = AdvSimd.LoadAndReplicateToVector128(x);
            h0 = AdvSimd.MultiplyWideningUpperAndAdd(h0, c, eLast);
            l0 = AdvSimd.MultiplyWideningLowerAndAdd(l0, cl, eLast.GetLower());
        }
        AdvSimd.Store((short*)output, l0 + h0 + l1 + h1 + l2 + h2);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void TileMacLoad(byte* weights, in Input input, float* output)
    {
        var p = weights;
        var e = AdvSimd.LoadAndReplicateToVector128(input.X);
        var el = e.GetLower();
        Vector128<short> a0 = default, a1 = default, a2 = default, a3 = default, a4 = default, a5 = default, a6 = default, a7 = default;
        for (var q = 0; q < Rows / 16 * Blocks * 32; q++, p += 16)
        {
            var c = AdvSimd.LoadVector128(p).AsSByte();
            var cl = c.GetLower();
            a0 = AdvSimd.MultiplyWideningUpperAndAdd(a0, c, e);
            a1 = AdvSimd.MultiplyWideningLowerAndAdd(a1, cl, el);
            a2 = AdvSimd.MultiplyWideningUpperAndAdd(a2, c, e);
            a3 = AdvSimd.MultiplyWideningLowerAndAdd(a3, cl, el);
            a4 = AdvSimd.MultiplyWideningUpperAndAdd(a4, c, e);
            a5 = AdvSimd.MultiplyWideningLowerAndAdd(a5, cl, el);
            a6 = AdvSimd.MultiplyWideningUpperAndAdd(a6, c, e);
            a7 = AdvSimd.MultiplyWideningLowerAndAdd(a7, cl, el);
        }
        AdvSimd.Store((short*)output, a0 + a1 + a2 + a3 + a4 + a5 + a6 + a7);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void TileMacOnly(byte* weights, in Input input, float* output)
    {
        var c = AdvSimd.LoadVector128(weights).AsSByte();
        var cl = c.GetLower();
        var e = AdvSimd.LoadAndReplicateToVector128(input.X);
        var el = e.GetLower();
        Vector128<short> a0 = default, a1 = default, a2 = default, a3 = default, a4 = default, a5 = default, a6 = default, a7 = default;
        for (var q = 0; q < Rows / 16 * Blocks * 32; q++)
        {
            a0 = AdvSimd.MultiplyWideningUpperAndAdd(a0, c, e);
            a1 = AdvSimd.MultiplyWideningLowerAndAdd(a1, cl, el);
            a2 = AdvSimd.MultiplyWideningUpperAndAdd(a2, c, e);
            a3 = AdvSimd.MultiplyWideningLowerAndAdd(a3, cl, el);
            a4 = AdvSimd.MultiplyWideningUpperAndAdd(a4, c, e);
            a5 = AdvSimd.MultiplyWideningLowerAndAdd(a5, cl, el);
            a6 = AdvSimd.MultiplyWideningUpperAndAdd(a6, c, e);
            a7 = AdvSimd.MultiplyWideningLowerAndAdd(a7, cl, el);
        }
        AdvSimd.Store((short*)output, a0 + a1 + a2 + a3 + a4 + a5 + a6 + a7);
    }

    // ---- the kernels ----

    // One 32-wide part: codes c0 and c1 against its 32 quants, as eight 16-bit sums. The upper halves
    // go first: a lower half read last needs no copy of its register.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Part(Vector128<sbyte> c0, Vector128<sbyte> c1, Vector128<sbyte> x0, Vector128<sbyte> x1)
    {
        var s = AdvSimd.MultiplyWideningUpper(c0, x0);
        s = AdvSimd.MultiplyWideningLowerAndAdd(s, c0.GetLower(), x0.GetLower());
        s = AdvSimd.MultiplyWideningUpperAndAdd(s, c1, x1);
        return AdvSimd.MultiplyWideningLowerAndAdd(s, c1.GetLower(), x1.GetLower());
    }

    // A block's four parts, added across, scaled and added to acc. Every sum fits 16 bits (32 products
    // of at most 254), so three 16-bit pairwise adds and one widening add do what seven wider ones did.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> Finish(Vector128<short> s0, Vector128<short> s1, Vector128<short> s2, Vector128<short> s3,
        Vector128<int> sx, Vector128<float> dx, Vector128<float> dw, Vector128<float> acc)
    {
        var sums = AdvSimd.AddPairwiseWidening(AdvSimd.Arm64.AddPairwise(AdvSimd.Arm64.AddPairwise(s0, s1), AdvSimd.Arm64.AddPairwise(s2, s3)));
        var scaled = AdvSimd.Multiply(AdvSimd.ConvertToSingle(AdvSimd.Subtract(sums, sx)), dx);
        return AdvSimd.FusedMultiplyAdd(acc, scaled, dw);
    }

    // One token of a batch through one block whose codes are already out.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> Token(Vector128<sbyte> c0, Vector128<sbyte> e0, Vector128<sbyte> c1, Vector128<sbyte> e1,
        Vector128<sbyte> c2, Vector128<sbyte> e2, Vector128<sbyte> c3, Vector128<sbyte> e3, sbyte* x, int* sx, float* dx, Vector128<float> dw, Vector128<float> acc) =>
        Finish(Part(c0, e0, AdvSimd.LoadVector128(x), AdvSimd.LoadVector128(x + 16)),
            Part(c1, e1, AdvSimd.LoadVector128(x + 32), AdvSimd.LoadVector128(x + 48)),
            Part(c2, e2, AdvSimd.LoadVector128(x + 64), AdvSimd.LoadVector128(x + 80)),
            Part(c3, e3, AdvSimd.LoadVector128(x + 96), AdvSimd.LoadVector128(x + 112)),
            AdvSimd.LoadVector128(sx), AdvSimd.LoadVector128(dx), dw, acc);

    // Trit codes 0, 1, 2 from y, a byte already multiplied up to its trit: (y * 3) >> 8, as halving adds.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<sbyte> Code(Vector128<byte> y) =>
        AdvSimd.ShiftRightLogical(AdvSimd.FusedAddHalving(y, AdvSimd.ShiftRightLogical(y, 1)), 6).AsSByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector64<sbyte> Code(Vector64<byte> y) =>
        AdvSimd.ShiftRightLogical(AdvSimd.FusedAddHalving(y, AdvSimd.ShiftRightLogical(y, 1)), 6).AsSByte();

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void DirectRows(byte* weights, float* scales, in Input input, float* output, int rows)
    {
        var three = Vector128.Create((byte)3);
        var three8 = Vector64.Create((byte)3);
        var powers = Vector64.Create((byte)1, 1, 3, 3, 9, 9, 27, 27);
        var x = input.X;
        var dx = input.Dx;
        var sx = input.Sx;
        for (var r = 0; r < rows; r++)
        {
            var acc = Vector128<float>.Zero;
            var p = weights + (long)r * Blocks * Ptq1.BlockBytes;
            var dw = scales + (long)r * Blocks;
            for (var b = 0; b < Blocks; b++, p += Ptq1.BlockBytes)
            {
                var xb = x + b * Ptq1.BlockWeights;
                var y0 = AdvSimd.LoadVector128(p);
                var y1 = AdvSimd.Multiply(y0, three);
                var y2 = AdvSimd.Multiply(y1, three);
                var y3 = AdvSimd.Multiply(y2, three);
                var y4 = AdvSimd.Multiply(y3, three);
                var s0 = Part(Code(y0), Code(y1), AdvSimd.LoadVector128(xb), AdvSimd.LoadVector128(xb + 16));
                var s1 = Part(Code(y2), Code(y3), AdvSimd.LoadVector128(xb + 32), AdvSimd.LoadVector128(xb + 48));

                var z0 = AdvSimd.LoadVector64(p + 16);
                var z1 = AdvSimd.Multiply(z0, three8);
                var z2 = AdvSimd.Multiply(z1, three8);
                var z3 = AdvSimd.Multiply(z2, three8);
                var z4 = AdvSimd.Multiply(z3, three8);

                var c4 = Code(y4);
                var x4 = AdvSimd.LoadVector128(xb + 64);
                var s2 = AdvSimd.MultiplyWideningUpper(c4, x4);
                s2 = AdvSimd.MultiplyWideningLowerAndAdd(s2, c4.GetLower(), x4.GetLower());
                s2 = AdvSimd.MultiplyWideningLowerAndAdd(s2, Code(z0), AdvSimd.LoadVector64(xb + 80));
                s2 = AdvSimd.MultiplyWideningLowerAndAdd(s2, Code(z1), AdvSimd.LoadVector64(xb + 88));

                var h = AdvSimd.DuplicateToVector64(*(ushort*)(p + 24)).AsByte();
                var s3 = AdvSimd.MultiplyWideningLower(Code(z2), AdvSimd.LoadVector64(xb + 96));
                s3 = AdvSimd.MultiplyWideningLowerAndAdd(s3, Code(z3), AdvSimd.LoadVector64(xb + 104));
                s3 = AdvSimd.MultiplyWideningLowerAndAdd(s3, Code(z4), AdvSimd.LoadVector64(xb + 112));
                s3 = AdvSimd.MultiplyWideningLowerAndAdd(s3, Code(AdvSimd.Multiply(h, powers)), AdvSimd.LoadVector64(xb + 120));

                acc = Finish(s0, s1, s2, s3, AdvSimd.LoadVector128(sx + b * 4), AdvSimd.LoadVector128(dx + b * 4), AdvSimd.LoadAndReplicateToVector128(dw + b), acc);
            }
            output[r] = Vector128.Sum(acc);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Bits2Rows(byte* weights, float* scales, in Input input, float* output, int rows)
    {
        var mask = Vector128.Create((byte)3);
        var x = input.X;
        var dx = input.Dx;
        var sx = input.Sx;
        var p = weights;
        var dw = scales;
        for (var r = 0; r < rows; r++)
        {
            var acc = Vector128<float>.Zero;
            var xb = x;
            var db = dx;
            var sb = sx;
            var end = dw + Blocks;
            for (; dw < end; dw++, p += 32, xb += Ptq1.BlockWeights, db += 4, sb += 4)
            {
                var v = AdvSimd.LoadVector128(p);
                var w = AdvSimd.LoadVector128(p + 16);
                var s0 = Part(AdvSimd.And(v, mask).AsSByte(), AdvSimd.And(w, mask).AsSByte(), AdvSimd.LoadVector128(xb), AdvSimd.LoadVector128(xb + 16));
                var s1 = Part(AdvSimd.And(AdvSimd.ShiftRightLogical(v, 2), mask).AsSByte(), AdvSimd.And(AdvSimd.ShiftRightLogical(w, 2), mask).AsSByte(), AdvSimd.LoadVector128(xb + 32), AdvSimd.LoadVector128(xb + 48));
                var s2 = Part(AdvSimd.And(AdvSimd.ShiftRightLogical(v, 4), mask).AsSByte(), AdvSimd.And(AdvSimd.ShiftRightLogical(w, 4), mask).AsSByte(), AdvSimd.LoadVector128(xb + 64), AdvSimd.LoadVector128(xb + 80));
                var s3 = Part(AdvSimd.ShiftRightLogical(v, 6).AsSByte(), AdvSimd.ShiftRightLogical(w, 6).AsSByte(), AdvSimd.LoadVector128(xb + 96), AdvSimd.LoadVector128(xb + 112));
                acc = Finish(s0, s1, s2, s3, AdvSimd.LoadVector128(sb), AdvSimd.LoadVector128(db), AdvSimd.LoadAndReplicateToVector128(dw), acc);
            }
            output[r] = Vector128.Sum(acc);
        }
    }

    // SIXTEEN ROWS AT A TIME, A ROW TO A LANE. The chip does about one vector instruction a cycle and
    // reads its L1 at 8 bytes a cycle, so a row-by-row kernel pays twice over: 128 bytes of activations
    // reloaded for every block, and a sum across lanes at the end of it. With a row in each lane an
    // activation is one byte broadcast, the sum across lanes never happens, and a 32-wide part ends in
    // sixteen rows' sums at once. The bytes are laid out for it: tile, block, four columns, sixteen rows.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void TileRows(byte* weights, float* scales, in Input input, float* output, int tiles)
    {
        var mask = Vector128.Create((byte)3);
        var second = Vector128.Create((byte)0x0C);
        var third = Vector128.Create((byte)0x30);
        var x = input.X;
        var dx = input.Dx;
        var bias = input.Bias;
        var p = weights;
        var dw = scales;
        for (var t = 0; t < tiles; t++, output += 16)
        {
            Vector128<float> f0 = default, f1 = default, f2 = default, f3 = default;
            var xb = x;
            var db = dx;
            for (var b = 0; b < Blocks; b++, dw += 16)
            {
                Vector128<float> g0 = default, g1 = default, g2 = default, g3 = default;
                for (var s = 0; s < 4; s++, db++)
                {
                    // A MASK ALONE, NO SHIFT: the second and third fields stay at four and sixteen times
                    // their value, in sums of their own, and come down once per part. Eight columns of
                    // at most 32 * 127 fit 16 bits, so nothing is lost.
                    Vector128<short> l0 = default, h0 = default, l1 = default, h1 = default, l2 = default, h2 = default;
                    for (var q = 0; q < 8; q++, p += 16, xb += 4)
                    {
                        var v = AdvSimd.LoadVector128(p);
                        var c = AdvSimd.ShiftRightLogical(v, 6).AsSByte();
                        var e = AdvSimd.LoadAndReplicateToVector128(xb + 3);
                        h0 = AdvSimd.MultiplyWideningUpperAndAdd(h0, c, e);
                        l0 = AdvSimd.MultiplyWideningLowerAndAdd(l0, c.GetLower(), e.GetLower());
                        c = AdvSimd.And(v, third).AsSByte();
                        e = AdvSimd.LoadAndReplicateToVector128(xb + 2);
                        h2 = AdvSimd.MultiplyWideningUpperAndAdd(h2, c, e);
                        l2 = AdvSimd.MultiplyWideningLowerAndAdd(l2, c.GetLower(), e.GetLower());
                        c = AdvSimd.And(v, second).AsSByte();
                        e = AdvSimd.LoadAndReplicateToVector128(xb + 1);
                        h1 = AdvSimd.MultiplyWideningUpperAndAdd(h1, c, e);
                        l1 = AdvSimd.MultiplyWideningLowerAndAdd(l1, c.GetLower(), e.GetLower());
                        var cLast = AdvSimd.And(v, mask).AsSByte();
                        var eLast = AdvSimd.LoadAndReplicateToVector128(xb);
                        h0 = AdvSimd.MultiplyWideningUpperAndAdd(h0, cLast, eLast);
                        l0 = AdvSimd.MultiplyWideningLowerAndAdd(l0, cLast.GetLower(), eLast.GetLower());
                    }
                    var lo = AdvSimd.ShiftRightArithmeticAdd(AdvSimd.ShiftRightArithmeticAdd(l0, l1, 2), l2, 4);
                    var hi = AdvSimd.ShiftRightArithmeticAdd(AdvSimd.ShiftRightArithmeticAdd(h0, h1, 2), h2, 4);
                    var d = AdvSimd.LoadAndReplicateToVector128(db);
                    g0 = AdvSimd.FusedMultiplyAdd(g0, AdvSimd.ConvertToSingle(AdvSimd.SignExtendWideningUpper(lo)), d);
                    g1 = AdvSimd.FusedMultiplyAdd(g1, AdvSimd.ConvertToSingle(AdvSimd.SignExtendWideningLower(lo.GetLower())), d);
                    g2 = AdvSimd.FusedMultiplyAdd(g2, AdvSimd.ConvertToSingle(AdvSimd.SignExtendWideningUpper(hi)), d);
                    g3 = AdvSimd.FusedMultiplyAdd(g3, AdvSimd.ConvertToSingle(AdvSimd.SignExtendWideningLower(hi.GetLower())), d);
                }
                var offset = AdvSimd.LoadAndReplicateToVector128(bias + b);
                f0 = AdvSimd.FusedMultiplyAdd(f0, AdvSimd.Subtract(g0, offset), AdvSimd.LoadVector128(dw + 4));
                f1 = AdvSimd.FusedMultiplyAdd(f1, AdvSimd.Subtract(g1, offset), AdvSimd.LoadVector128(dw));
                f2 = AdvSimd.FusedMultiplyAdd(f2, AdvSimd.Subtract(g2, offset), AdvSimd.LoadVector128(dw + 12));
                f3 = AdvSimd.FusedMultiplyAdd(f3, AdvSimd.Subtract(g3, offset), AdvSimd.LoadVector128(dw + 8));
            }
            AdvSimd.Store(output, f1);
            AdvSimd.Store(output + 4, f0);
            AdvSimd.Store(output + 8, f3);
            AdvSimd.Store(output + 12, f2);
        }
    }

    // TWO COLUMNS TO A LOAD, NO BROADCAST. ld1r costs three cycles and eight of them were half the
    // tile kernel's time, so an activation is spread once per input instead: a 16-byte load holds
    // eight copies of one column's quant then eight of the next. smlal reads the low halves and smlal2
    // the high ones, so the weights keep eight rows of a column in their low lanes and the same rows of
    // the next column in their high lanes, and both add into the same eight rows' sums.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void PairRows(byte* weights, float* scales, in Input input, float* output, int tiles)
    {
        var mask = Vector128.Create((byte)3);
        var second = Vector128.Create((byte)0x0C);
        var third = Vector128.Create((byte)0x30);
        var table = input.Spread;
        var dx = input.Dx;
        var bias = input.Bias;
        var p = weights;
        var dw = scales;
        for (var t = 0; t < tiles; t++, output += 16)
        {
            Vector128<float> f0 = default, f1 = default, f2 = default, f3 = default;
            var e = table;
            var db = dx;
            for (var b = 0; b < Blocks; b++, dw += 16)
            {
                Vector128<float> g0 = default, g1 = default, g2 = default, g3 = default;
                for (var s = 0; s < 4; s++, db++)
                {
                    Vector128<short> a0 = default, a1 = default, a2 = default, a3 = default, b0 = default, b1 = default, b2 = default, b3 = default;
                    for (var g = 0; g < 4; g++, p += 32, e += 64)
                    {
                        var va = AdvSimd.LoadVector128(p);
                        var vb = AdvSimd.LoadVector128(p + 16);
                        var x0 = AdvSimd.LoadVector128(e);
                        var x1 = AdvSimd.LoadVector128(e + 16);
                        var x2 = AdvSimd.LoadVector128(e + 32);
                        var x3 = AdvSimd.LoadVector128(e + 48);
                        var c0 = AdvSimd.And(va, mask).AsSByte();
                        var d0 = AdvSimd.And(vb, mask).AsSByte();
                        var c1 = AdvSimd.And(va, second).AsSByte();
                        var d1 = AdvSimd.And(vb, second).AsSByte();
                        var c2 = AdvSimd.And(va, third).AsSByte();
                        var d2 = AdvSimd.And(vb, third).AsSByte();
                        var c3 = AdvSimd.ShiftRightLogical(va, 6).AsSByte();
                        var d3 = AdvSimd.ShiftRightLogical(vb, 6).AsSByte();

                        a0 = AdvSimd.MultiplyWideningUpperAndAdd(a0, c0, x0);
                        b0 = AdvSimd.MultiplyWideningUpperAndAdd(b0, d0, x0);
                        a1 = AdvSimd.MultiplyWideningUpperAndAdd(a1, c1, x1);
                        b1 = AdvSimd.MultiplyWideningUpperAndAdd(b1, d1, x1);
                        a2 = AdvSimd.MultiplyWideningUpperAndAdd(a2, c2, x2);
                        b2 = AdvSimd.MultiplyWideningUpperAndAdd(b2, d2, x2);
                        a3 = AdvSimd.MultiplyWideningUpperAndAdd(a3, c3, x3);
                        b3 = AdvSimd.MultiplyWideningUpperAndAdd(b3, d3, x3);

                        a0 = AdvSimd.MultiplyWideningLowerAndAdd(a0, c0.GetLower(), AdvSimd.LoadVector64(e));
                        b0 = AdvSimd.MultiplyWideningLowerAndAdd(b0, d0.GetLower(), AdvSimd.LoadVector64(e));
                        a1 = AdvSimd.MultiplyWideningLowerAndAdd(a1, c1.GetLower(), AdvSimd.LoadVector64(e + 16));
                        b1 = AdvSimd.MultiplyWideningLowerAndAdd(b1, d1.GetLower(), AdvSimd.LoadVector64(e + 16));
                        a2 = AdvSimd.MultiplyWideningLowerAndAdd(a2, c2.GetLower(), AdvSimd.LoadVector64(e + 32));
                        b2 = AdvSimd.MultiplyWideningLowerAndAdd(b2, d2.GetLower(), AdvSimd.LoadVector64(e + 32));
                        a3 = AdvSimd.MultiplyWideningLowerAndAdd(a3, c3.GetLower(), AdvSimd.LoadVector64(e + 48));
                        b3 = AdvSimd.MultiplyWideningLowerAndAdd(b3, d3.GetLower(), AdvSimd.LoadVector64(e + 48));
                    }
                    var lo = AdvSimd.ShiftRightArithmeticAdd(AdvSimd.ShiftRightArithmeticAdd(AdvSimd.Add(a0, a3), a1, 2), a2, 4);
                    var hi = AdvSimd.ShiftRightArithmeticAdd(AdvSimd.ShiftRightArithmeticAdd(AdvSimd.Add(b0, b3), b1, 2), b2, 4);
                    var d = AdvSimd.LoadAndReplicateToVector128(db);
                    g0 = AdvSimd.FusedMultiplyAdd(g0, AdvSimd.ConvertToSingle(AdvSimd.SignExtendWideningUpper(lo)), d);
                    g1 = AdvSimd.FusedMultiplyAdd(g1, AdvSimd.ConvertToSingle(AdvSimd.SignExtendWideningLower(lo.GetLower())), d);
                    g2 = AdvSimd.FusedMultiplyAdd(g2, AdvSimd.ConvertToSingle(AdvSimd.SignExtendWideningUpper(hi)), d);
                    g3 = AdvSimd.FusedMultiplyAdd(g3, AdvSimd.ConvertToSingle(AdvSimd.SignExtendWideningLower(hi.GetLower())), d);
                }
                var offset = AdvSimd.LoadAndReplicateToVector128(bias + b);
                f0 = AdvSimd.FusedMultiplyAdd(f0, AdvSimd.Subtract(g0, offset), AdvSimd.LoadVector128(dw + 4));
                f1 = AdvSimd.FusedMultiplyAdd(f1, AdvSimd.Subtract(g1, offset), AdvSimd.LoadVector128(dw));
                f2 = AdvSimd.FusedMultiplyAdd(f2, AdvSimd.Subtract(g2, offset), AdvSimd.LoadVector128(dw + 12));
                f3 = AdvSimd.FusedMultiplyAdd(f3, AdvSimd.Subtract(g3, offset), AdvSimd.LoadVector128(dw + 8));
            }
            AdvSimd.Store(output, f1);
            AdvSimd.Store(output + 4, f0);
            AdvSimd.Store(output + 8, f3);
            AdvSimd.Store(output + 12, f2);
        }
    }

    // THIRTY-TWO ROWS TO A LOAD. On this core a vector load costs about 2.5 cycles and a vector
    // instruction about 0.75, so what a block pays is mostly the loads: this one takes four of the
    // spread activations for 256 weights where the pair kernel takes eight, and a field's four weight
    // vectors pass through it together, so a sum is touched again only after three others.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void QuadRows(byte* weights, float* scales, in Input input, float* output, int tiles)
    {
        var mask = Vector128.Create((byte)3);
        var second = Vector128.Create((byte)0x0C);
        var third = Vector128.Create((byte)0x30);
        var table = input.Spread;
        var dx = input.Dx;
        var bias = input.Bias;
        var p = weights;
        var dw = scales;
        for (var t = 0; t < tiles; t++, output += 32)
        {
            Vector128<float> f0 = default, f1 = default, f2 = default, f3 = default, f4 = default, f5 = default, f6 = default, f7 = default;
            var e = table;
            var db = dx;
            for (var b = 0; b < Blocks; b++, dw += 32)
            {
                Vector128<float> g0 = default, g1 = default, g2 = default, g3 = default, g4 = default, g5 = default, g6 = default, g7 = default;
                for (var s = 0; s < 4; s++, db++)
                {
                    Vector128<short> a0 = default, a1 = default, a2 = default, a3 = default, b0 = default, b1 = default, b2 = default, b3 = default, c0 = default, c1 = default, c2 = default, c3 = default, d0 = default, d1 = default, d2 = default, d3 = default;
                    for (var g = 0; g < 4; g++, p += 64, e += 64)
                    {
                        var va = AdvSimd.LoadVector128(p);
                        var vb = AdvSimd.LoadVector128(p + 16);
                        var vc = AdvSimd.LoadVector128(p + 32);
                        var vd = AdvSimd.LoadVector128(p + 48);
                        var x3 = AdvSimd.LoadVector128(e + 48);
                        var l3 = x3.GetLower();
                        var u30 = AdvSimd.ShiftRightLogical(va, 6).AsSByte();
                        var u31 = AdvSimd.ShiftRightLogical(vb, 6).AsSByte();
                        var u32 = AdvSimd.ShiftRightLogical(vc, 6).AsSByte();
                        var u33 = AdvSimd.ShiftRightLogical(vd, 6).AsSByte();
                        a3 = AdvSimd.MultiplyWideningUpperAndAdd(a3, u30, x3);
                        b3 = AdvSimd.MultiplyWideningUpperAndAdd(b3, u31, x3);
                        c3 = AdvSimd.MultiplyWideningUpperAndAdd(c3, u32, x3);
                        d3 = AdvSimd.MultiplyWideningUpperAndAdd(d3, u33, x3);
                        a3 = AdvSimd.MultiplyWideningLowerAndAdd(a3, u30.GetLower(), l3);
                        b3 = AdvSimd.MultiplyWideningLowerAndAdd(b3, u31.GetLower(), l3);
                        c3 = AdvSimd.MultiplyWideningLowerAndAdd(c3, u32.GetLower(), l3);
                        d3 = AdvSimd.MultiplyWideningLowerAndAdd(d3, u33.GetLower(), l3);

                        var x2 = AdvSimd.LoadVector128(e + 32);
                        var l2 = x2.GetLower();
                        var u20 = AdvSimd.And(va, third).AsSByte();
                        var u21 = AdvSimd.And(vb, third).AsSByte();
                        var u22 = AdvSimd.And(vc, third).AsSByte();
                        var u23 = AdvSimd.And(vd, third).AsSByte();
                        a2 = AdvSimd.MultiplyWideningUpperAndAdd(a2, u20, x2);
                        b2 = AdvSimd.MultiplyWideningUpperAndAdd(b2, u21, x2);
                        c2 = AdvSimd.MultiplyWideningUpperAndAdd(c2, u22, x2);
                        d2 = AdvSimd.MultiplyWideningUpperAndAdd(d2, u23, x2);
                        a2 = AdvSimd.MultiplyWideningLowerAndAdd(a2, u20.GetLower(), l2);
                        b2 = AdvSimd.MultiplyWideningLowerAndAdd(b2, u21.GetLower(), l2);
                        c2 = AdvSimd.MultiplyWideningLowerAndAdd(c2, u22.GetLower(), l2);
                        d2 = AdvSimd.MultiplyWideningLowerAndAdd(d2, u23.GetLower(), l2);

                        var x1 = AdvSimd.LoadVector128(e + 16);
                        var l1 = x1.GetLower();
                        var u10 = AdvSimd.And(va, second).AsSByte();
                        var u11 = AdvSimd.And(vb, second).AsSByte();
                        var u12 = AdvSimd.And(vc, second).AsSByte();
                        var u13 = AdvSimd.And(vd, second).AsSByte();
                        a1 = AdvSimd.MultiplyWideningUpperAndAdd(a1, u10, x1);
                        b1 = AdvSimd.MultiplyWideningUpperAndAdd(b1, u11, x1);
                        c1 = AdvSimd.MultiplyWideningUpperAndAdd(c1, u12, x1);
                        d1 = AdvSimd.MultiplyWideningUpperAndAdd(d1, u13, x1);
                        a1 = AdvSimd.MultiplyWideningLowerAndAdd(a1, u10.GetLower(), l1);
                        b1 = AdvSimd.MultiplyWideningLowerAndAdd(b1, u11.GetLower(), l1);
                        c1 = AdvSimd.MultiplyWideningLowerAndAdd(c1, u12.GetLower(), l1);
                        d1 = AdvSimd.MultiplyWideningLowerAndAdd(d1, u13.GetLower(), l1);

                        var x0 = AdvSimd.LoadVector128(e + 0);
                        var l0 = x0.GetLower();
                        var u00 = AdvSimd.And(va, mask).AsSByte();
                        var u01 = AdvSimd.And(vb, mask).AsSByte();
                        var u02 = AdvSimd.And(vc, mask).AsSByte();
                        var u03 = AdvSimd.And(vd, mask).AsSByte();
                        a0 = AdvSimd.MultiplyWideningUpperAndAdd(a0, u00, x0);
                        b0 = AdvSimd.MultiplyWideningUpperAndAdd(b0, u01, x0);
                        c0 = AdvSimd.MultiplyWideningUpperAndAdd(c0, u02, x0);
                        d0 = AdvSimd.MultiplyWideningUpperAndAdd(d0, u03, x0);
                        a0 = AdvSimd.MultiplyWideningLowerAndAdd(a0, u00.GetLower(), l0);
                        b0 = AdvSimd.MultiplyWideningLowerAndAdd(b0, u01.GetLower(), l0);
                        c0 = AdvSimd.MultiplyWideningLowerAndAdd(c0, u02.GetLower(), l0);
                        d0 = AdvSimd.MultiplyWideningLowerAndAdd(d0, u03.GetLower(), l0);
                    }
                    var s0 = AdvSimd.ShiftRightArithmeticAdd(AdvSimd.ShiftRightArithmeticAdd(AdvSimd.Add(a0, a3), a1, 2), a2, 4);
                    var s1 = AdvSimd.ShiftRightArithmeticAdd(AdvSimd.ShiftRightArithmeticAdd(AdvSimd.Add(b0, b3), b1, 2), b2, 4);
                    var s2 = AdvSimd.ShiftRightArithmeticAdd(AdvSimd.ShiftRightArithmeticAdd(AdvSimd.Add(c0, c3), c1, 2), c2, 4);
                    var s3 = AdvSimd.ShiftRightArithmeticAdd(AdvSimd.ShiftRightArithmeticAdd(AdvSimd.Add(d0, d3), d1, 2), d2, 4);
                    var d = AdvSimd.LoadAndReplicateToVector128(db);
                    g0 = AdvSimd.FusedMultiplyAdd(g0, AdvSimd.ConvertToSingle(AdvSimd.SignExtendWideningLower(s0.GetLower())), d);
                    g1 = AdvSimd.FusedMultiplyAdd(g1, AdvSimd.ConvertToSingle(AdvSimd.SignExtendWideningUpper(s0)), d);
                    g2 = AdvSimd.FusedMultiplyAdd(g2, AdvSimd.ConvertToSingle(AdvSimd.SignExtendWideningLower(s1.GetLower())), d);
                    g3 = AdvSimd.FusedMultiplyAdd(g3, AdvSimd.ConvertToSingle(AdvSimd.SignExtendWideningUpper(s1)), d);
                    g4 = AdvSimd.FusedMultiplyAdd(g4, AdvSimd.ConvertToSingle(AdvSimd.SignExtendWideningLower(s2.GetLower())), d);
                    g5 = AdvSimd.FusedMultiplyAdd(g5, AdvSimd.ConvertToSingle(AdvSimd.SignExtendWideningUpper(s2)), d);
                    g6 = AdvSimd.FusedMultiplyAdd(g6, AdvSimd.ConvertToSingle(AdvSimd.SignExtendWideningLower(s3.GetLower())), d);
                    g7 = AdvSimd.FusedMultiplyAdd(g7, AdvSimd.ConvertToSingle(AdvSimd.SignExtendWideningUpper(s3)), d);
                }
                var offset = AdvSimd.LoadAndReplicateToVector128(bias + b);
                f0 = AdvSimd.FusedMultiplyAdd(f0, AdvSimd.Subtract(g0, offset), AdvSimd.LoadVector128(dw + 0));
                f1 = AdvSimd.FusedMultiplyAdd(f1, AdvSimd.Subtract(g1, offset), AdvSimd.LoadVector128(dw + 4));
                f2 = AdvSimd.FusedMultiplyAdd(f2, AdvSimd.Subtract(g2, offset), AdvSimd.LoadVector128(dw + 8));
                f3 = AdvSimd.FusedMultiplyAdd(f3, AdvSimd.Subtract(g3, offset), AdvSimd.LoadVector128(dw + 12));
                f4 = AdvSimd.FusedMultiplyAdd(f4, AdvSimd.Subtract(g4, offset), AdvSimd.LoadVector128(dw + 16));
                f5 = AdvSimd.FusedMultiplyAdd(f5, AdvSimd.Subtract(g5, offset), AdvSimd.LoadVector128(dw + 20));
                f6 = AdvSimd.FusedMultiplyAdd(f6, AdvSimd.Subtract(g6, offset), AdvSimd.LoadVector128(dw + 24));
                f7 = AdvSimd.FusedMultiplyAdd(f7, AdvSimd.Subtract(g7, offset), AdvSimd.LoadVector128(dw + 28));
            }
            AdvSimd.Store(output + 0, f0);
            AdvSimd.Store(output + 4, f1);
            AdvSimd.Store(output + 8, f2);
            AdvSimd.Store(output + 12, f3);
            AdvSimd.Store(output + 16, f4);
            AdvSimd.Store(output + 20, f5);
            AdvSimd.Store(output + 24, f6);
            AdvSimd.Store(output + 28, f7);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Batch4Rows(byte* weights, float* scales, in Input i0, in Input i1, in Input i2, in Input i3,
        float* o0, float* o1, float* o2, float* o3, int rows)
    {
        var mask = Vector128.Create((byte)3);
        sbyte* x0 = i0.X, x1 = i1.X, x2 = i2.X, x3 = i3.X;
        float* d0 = i0.Dx, d1 = i1.Dx, d2 = i2.Dx, d3 = i3.Dx;
        int* s0 = i0.Sx, s1 = i1.Sx, s2 = i2.Sx, s3 = i3.Sx;
        for (var r = 0; r < rows; r++)
        {
            Vector128<float> a0 = default, a1 = default, a2 = default, a3 = default;
            var p = weights + (long)r * Blocks * 32;
            var dw = scales + (long)r * Blocks;
            for (var b = 0; b < Blocks; b++, p += 32)
            {
                var v = AdvSimd.LoadVector128(p);
                var w = AdvSimd.LoadVector128(p + 16);
                var c0 = AdvSimd.And(v, mask).AsSByte();
                var e0 = AdvSimd.And(w, mask).AsSByte();
                var c1 = AdvSimd.And(AdvSimd.ShiftRightLogical(v, 2), mask).AsSByte();
                var e1 = AdvSimd.And(AdvSimd.ShiftRightLogical(w, 2), mask).AsSByte();
                var c2 = AdvSimd.And(AdvSimd.ShiftRightLogical(v, 4), mask).AsSByte();
                var e2 = AdvSimd.And(AdvSimd.ShiftRightLogical(w, 4), mask).AsSByte();
                var c3 = AdvSimd.ShiftRightLogical(v, 6).AsSByte();
                var e3 = AdvSimd.ShiftRightLogical(w, 6).AsSByte();
                var o = b * Ptq1.BlockWeights;
                var q = b * 4;
                var d = AdvSimd.LoadAndReplicateToVector128(dw + b);
                a0 = Token(c0, e0, c1, e1, c2, e2, c3, e3, x0 + o, s0 + q, d0 + q, d, a0);
                a1 = Token(c0, e0, c1, e1, c2, e2, c3, e3, x1 + o, s1 + q, d1 + q, d, a1);
                a2 = Token(c0, e0, c1, e1, c2, e2, c3, e3, x2 + o, s2 + q, d2 + q, d, a2);
                a3 = Token(c0, e0, c1, e1, c2, e2, c3, e3, x3 + o, s3 + q, d3 + q, d, a3);
            }
            o0[r] = Vector128.Sum(a0);
            o1[r] = Vector128.Sum(a1);
            o2[r] = Vector128.Sum(a2);
            o3[r] = Vector128.Sum(a3);
        }
    }

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
