// KernelBench.cs
//
// How fast the PTQ1_0 kernels run on THIS device, on every instruction path it can
// take, inside whatever runtime this process is. On Android that runtime is Mono, which
// accelerates only part of System.Runtime.Intrinsics and silently runs the rest as
// plain code - so a path that IsSupported claims is not proof of a fast one. S0.2 runs
// this on the P30 in the shipped Release build and picks the kernel path from what it
// measures.
//
// Single thread, synthetic weights at the 2B's real shapes. The estimate of tokens per
// second divides by the bytes a 2B token reads (its whole PTQ1_0 file: every weight,
// the tied output head included, is read once per token).

using System.Diagnostics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace CircleAI.Inference.Managed.Kernels;

/// <summary>Measures the PTQ1_0 kernels on this device.</summary>
public static class KernelBench
{
    /// <summary>Bytes of PTQ1_0 weights one Bonsai 2 2B token reads (the 2B file's size).</summary>
    public const long Bonsai2BBytesPerToken = 429_183_456;

    /// <summary>One measured path.</summary>
    public sealed record Result(KernelPath Path, int Rows, int Cols, double MillisecondsPerPass, double GigabytesPerSecond)
    {
        /// <summary>Tokens per second a 2B would reach on one thread at this speed.</summary>
        public double TokensPerSecondOneThread => GigabytesPerSecond * 1e9 / Bonsai2BBytesPerToken;
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

        var results = new List<Result>();
        var output = new float[rows];
        var scales = Kernels.Ptq1.BlockScales(weights); // decoded once, as a loaded model would hold them
        foreach (var path in Enum.GetValues<KernelPath>())
        {
            if (!Kernels.Ptq1.CanRun(path)) continue;
            Pass(weights, scales, rowBytes, q, output, path); // warm-up: JIT and caches
            var sw = Stopwatch.StartNew();
            for (var p = 0; p < passes; p++) Pass(weights, scales, rowBytes, q, output, path);
            sw.Stop();
            var ms = sw.Elapsed.TotalMilliseconds / passes;
            results.Add(new Result(path, rows, cols, ms, weights.Length / (ms / 1000.0) / 1e9));
        }
        return results;
    }

    /// <summary>The instruction-set line and one line per measured path, for a log.</summary>
    public static IEnumerable<string> Report(int rows = 6144, int cols = 2048, int passes = 5)
    {
        yield return InstructionSets();
        foreach (var r in Ptq1(rows, cols, passes))
            yield return $"PTQ1_0 {r.Rows}x{r.Cols} {r.Path,-9} {r.MillisecondsPerPass,8:F2} ms  {r.GigabytesPerSecond,6:F3} GB/s  ~{r.TokensPerSecondOneThread,5:F2} tok/s on one thread (2B)";
    }

    private static void Pass(byte[] weights, float[] scales, int rowBytes, Q8Activations q, float[] output, KernelPath path)
    {
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
