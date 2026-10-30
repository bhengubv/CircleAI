// KernelBench.cs
//
// How fast the PTQ1_0 kernels run on THIS device, on every instruction path it can
// take, inside whatever runtime this process is. On Android that runtime is Mono, which
// accelerates only part of System.Runtime.Intrinsics and silently runs the rest as
// plain code - so a path that IsSupported claims is not proof of a fast one. S0.2 runs
// this on the P30 in the shipped Release build and picks the kernel path from what it
// measures.
//
// Synthetic weights at the 2B's real shapes; every path on one thread, then the lookup-table
// path on one thread up to every core. The estimate of tokens per second divides by the
// bytes a 2B token reads (its whole PTQ1_0 file: every weight, the tied output head
// included, is read once per token).

using System.Diagnostics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using CircleAI.Inference.Managed.Runtime;

namespace CircleAI.Inference.Managed.Kernels;

/// <summary>Measures the PTQ1_0 kernels on this device.</summary>
public static class KernelBench
{
    /// <summary>Bytes of PTQ1_0 weights one Bonsai 2 2B token reads (the 2B file's size).</summary>
    public const long Bonsai2BBytesPerToken = 429_183_456;

    /// <summary>One measured path, on <paramref name="Threads"/> threads.</summary>
    public sealed record Result(KernelPath Path, int Rows, int Cols, double MillisecondsPerPass, double GigabytesPerSecond, int Threads = 1)
    {
        /// <summary>Tokens per second a 2B would reach at this speed, its products alone.</summary>
        public double TokensPerSecond => GigabytesPerSecond * 1e9 / Bonsai2BBytesPerToken;
    }

    /// <summary>What the runtime reports it can accelerate, as one line.</summary>
    public static string InstructionSets() =>
        $"AdvSimd={AdvSimd.IsSupported} AdvSimd.Arm64={AdvSimd.Arm64.IsSupported} Dp={Dp.IsSupported} " +
        $"Rdm={Rdm.IsSupported} Avx2={System.Runtime.Intrinsics.X86.Avx2.IsSupported} AvxVnni={AvxVnni.IsSupported} " +
        $"Avx512F={Avx512F.IsSupported} Vector128.Accelerated={Vector128.IsHardwareAccelerated} " +
        $"Best={Kernels.Ptq1.Best} Framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}";

    /// <summary>
    /// Times a <paramref name="rows"/> x <paramref name="cols"/> PTQ1_0 matrix-vector
    /// product on each runnable path (the 2B's FFN up-projection by default).
    /// </summary>
    public static IReadOnlyList<Result> Ptq1(int rows = 6144, int cols = 2048, int passes = 5)
    {
        var (weights, scales, q) = Synthetic(rows, cols);
        var rowBytes = cols / Kernels.Ptq1.BlockWeights * Kernels.Ptq1.BlockBytes;
        var results = new List<Result>();
        var output = new float[rows];
        var lut = new Ptq1Lut(cols); // allocated once, as an engine would hold it
        foreach (var path in Enum.GetValues<KernelPath>())
        {
            if (!Kernels.Ptq1.CanRun(path)) continue;
            Pass(weights, scales, rowBytes, q, lut, output, path); // warm-up: JIT and caches
            var sw = Stopwatch.StartNew();
            for (var p = 0; p < passes; p++) Pass(weights, scales, rowBytes, q, lut, output, path);
            sw.Stop();
            var ms = sw.Elapsed.TotalMilliseconds / passes;
            results.Add(new Result(path, rows, cols, ms, weights.Length / (ms / 1000.0) / 1e9));
        }
        return results;
    }

    /// <summary>
    /// The <see cref="KernelPath.Lut"/> product on 1 to <paramref name="maxThreads"/> threads
    /// (every core unless given), each pass building the tables on the calling thread and
    /// then sharing the rows out, plus how long the build alone takes.
    /// </summary>
    public static (double BuildMilliseconds, IReadOnlyList<Result> Results) Ptq1LutThreads(
        int rows = 6144, int cols = 2048, int passes = 10, int maxThreads = 0)
    {
        var (weights, scales, q) = Synthetic(rows, cols);
        var lut = new Ptq1Lut(cols);
        var output = new float[rows];

        lut.Build(q); // warm-up
        var sw = Stopwatch.StartNew();
        for (var p = 0; p < passes; p++) lut.Build(q);
        var buildMs = sw.Elapsed.TotalMilliseconds / passes;

        var results = new List<Result>();
        var most = maxThreads > 0 ? maxThreads : Environment.ProcessorCount;
        for (var threads = 1; threads <= most; threads++)
        {
            using var pool = new WorkerPool(threads);
            lut.MatVec(weights, scales, output, pool); // warm-up: the threads' first product
            sw.Restart();
            for (var p = 0; p < passes; p++)
            {
                lut.Build(q);
                lut.MatVec(weights, scales, output, pool);
            }
            var ms = sw.Elapsed.TotalMilliseconds / passes;
            results.Add(new Result(KernelPath.Lut, rows, cols, ms, weights.Length / (ms / 1000.0) / 1e9, threads));
        }
        return (buildMs, results);
    }

    /// <summary>The instruction-set line and one line per measured path and thread count, for a log.</summary>
    public static IEnumerable<string> Report(int rows = 6144, int cols = 2048, int passes = 5)
    {
        var total = Stopwatch.StartNew();
        yield return InstructionSets();
        var (shuffleNs, addNs) = Shuffle();
        yield return $"Vector128 byte shuffle {shuffleNs:F2} ns/op, add {addNs:F2} ns/op";
        foreach (var r in Ptq1(rows, cols, passes))
            yield return $"PTQ1_0 {r.Rows}x{r.Cols} {r.Path,-9} {r.MillisecondsPerPass,8:F2} ms  {r.GigabytesPerSecond,6:F3} GB/s  ~{r.TokensPerSecond,5:F2} tok/s on one thread (2B)";

        var (buildMs, threaded) = Ptq1LutThreads(rows, cols);
        yield return $"Lut tables for {cols} inputs built in {buildMs:F2} ms on one thread";
        foreach (var r in threaded)
            yield return $"PTQ1_0 {r.Rows}x{r.Cols} Lut on {r.Threads} thread{(r.Threads == 1 ? " " : "s")} {r.MillisecondsPerPass,8:F2} ms  {r.GigabytesPerSecond,6:F3} GB/s  ~{r.TokensPerSecond,5:F2} tok/s (2B)";
        yield return $"bench done in {total.Elapsed.TotalSeconds:F1} s ({Environment.ProcessorCount} cores seen)";
    }

    // Random PTQ1_0 weights with a sane scale in every block, their decoded block scales,
    // and one quantised input vector.
    private static (byte[] Weights, float[] Scales, Q8Activations X) Synthetic(int rows, int cols)
    {
        var rng = new Random(1);
        var rowBytes = cols / Kernels.Ptq1.BlockWeights * Kernels.Ptq1.BlockBytes;
        var weights = new byte[rows * rowBytes];
        rng.NextBytes(weights);
        for (var b = 26; b < weights.Length; b += Kernels.Ptq1.BlockBytes)
            BitConverter.TryWriteBytes(weights.AsSpan(b, 2), (Half)0.02f);

        var x = new float[cols];
        for (var i = 0; i < cols; i++) x[i] = (float)(rng.NextDouble() * 2 - 1);
        var q = new Q8Activations(cols);
        q.Quantize(x);
        return (weights, Kernels.Ptq1.BlockScales(weights), q); // scales decoded once, as a loaded model would hold them
    }

    /// <summary>
    /// Nanoseconds per <see cref="Vector128.Shuffle(Vector128{byte}, Vector128{byte})"/> with
    /// indices known only at run time, next to a plain vector add. Near the add's cost, the
    /// runtime made it one table-lookup instruction (NEON TBL) and a vector table kernel is
    /// worth writing; far above it, the shuffle runs lane by lane in software.
    /// </summary>
    public static (double ShuffleNs, double AddNs) Shuffle(int iterations = 4_000_000)
    {
        // A permutation of 0..15, so every result is a valid index for the next round.
        var table = Vector128.Create((byte)7, 12, 3, 9, 0, 14, 5, 11, 2, 15, 8, 1, 13, 6, 10, 4);
        var mask = Vector128.Create((byte)15);
        var idx = Vector128.Create((byte)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) idx = Vector128.Shuffle(table, idx);
        var shuffleNs = sw.Elapsed.TotalNanoseconds / iterations;

        var acc = idx;
        sw.Restart();
        for (var i = 0; i < iterations; i++) acc = (acc + table) & mask;
        var addNs = sw.Elapsed.TotalNanoseconds / iterations;

        GC.KeepAlive(idx.GetElement(0) + acc.GetElement(0)); // keeps both loops from being dropped
        return (shuffleNs, addNs);
    }

    private static void Pass(byte[] weights, float[] scales, int rowBytes, Q8Activations q, Ptq1Lut lut, float[] output, KernelPath path)
    {
        if (path == KernelPath.Lut)
        {
            // The table build is part of the pass: one build serves every row of the matrix.
            lut.Build(q);
            lut.MatVec(weights, scales, output);
            return;
        }

        var blocksPerRow = rowBytes / Kernels.Ptq1.BlockBytes;
        for (var r = 0; r < output.Length; r++)
        {
            var row = weights.AsSpan(r * rowBytes, rowBytes);
            output[r] = path == KernelPath.Table
                ? Kernels.Ptq1.DotTable(row, scales.AsSpan(r * blocksPerRow, blocksPerRow), q)
                : Kernels.Ptq1.Dot(row, q, path);
        }
    }
}
