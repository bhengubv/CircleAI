// KernelLimits.cs
//
// WHAT LIMITS A TOKEN ON THIS DEVICE, MEASURED APART FROM THE ENGINE.
//
// KernelBench says how fast a kernel runs on a matrix that sits in the cache, on whatever cores the
// scheduler gives it. A token is not that: its weights come from memory, its threads land on big and
// little cores, and every product ends at a barrier. So each limit is taken on its own here, with the
// threads held to the cores asked for: the real clock, how fast memory reads, what one block of the
// lookup-table kernel costs with its data in each level of the memory, how that scales over cores,
// what handing work to the pool costs, and what a sustained load does to all of it.
//
// A MEASUREMENT, NEVER THE ENGINE'S PATH: it holds its threads to cores with sched_setaffinity, which
// nothing on the path to an answer calls. Reached only through the soak receiver's "limits" verb.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using CircleAI.Inference.Managed.Runtime;

namespace CircleAI.Inference.Managed.Kernels;

/// <summary>Measures this device's clock, memory and core limits for the C# engine's products.</summary>
public static unsafe class KernelLimits
{
    // Blocks of 128 weights a 2B token runs through its products, its output head left out: the
    // 24 layers' ternary weights, counted from the file. What a per-block cost becomes in a token.
    private const long BlocksPerToken = 10_715_136;

    private const int Cols = 2048;
    private const int Rows = 6144;
    private const int BufferBytes = 128 << 20;

    [DllImport("libc", EntryPoint = "sched_setaffinity")]
    private static extern int SetAffinity(int pid, nint size, ulong* mask);

    /// <summary>Hands <paramref name="say"/> one line per group of measurements as each ends; the last says how long it all took.</summary>
    public static void Report(Action<string> say, int sustainedSeconds = 30)
    {
        ArgumentNullException.ThrowIfNull(say);
        void All(IEnumerable<string> lines)
        {
            foreach (var line in lines) say(line);
        }

        var total = Stopwatch.StartNew();
        var (big, little) = Clusters();
        say($"limits: big cores {string.Join(",", big)}, little cores {string.Join(",", little)}; {RuntimeInformation.FrameworkDescription}, AdvSimd={AdvSimd.IsSupported}");

        All(Clocks(big, little));

        var buffer = (byte*)NativeMemory.AlignedAlloc(BufferBytes, 4096);
        try
        {
            Random.Shared.NextBytes(new Span<byte>(buffer, BufferBytes));
            All(Bandwidth(buffer, big, little));
            All(Blocks(buffer, big, little));
            All(Scaling(buffer, big, little));
            All(Pool(buffer));
            All(Sustained(buffer, big, sustainedSeconds));
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
        say(F($"limits done in {total.Elapsed.TotalSeconds:F0} s"));
    }

    // The cores by their top clock: the fastest are the big ones.
    private static (int[] Big, int[] Little) Clusters()
    {
        var cores = Enumerable.Range(0, Environment.ProcessorCount).Select(c => (Core: c, Top: TopClock(c))).ToList();
        var top = cores.Max(c => c.Top);
        var big = cores.Where(c => c.Top == top).Select(c => c.Core).ToArray();
        var little = cores.Where(c => c.Top != top).Select(c => c.Core).ToArray();
        return (big, little);
    }

    private static long TopClock(int core) => Read($"/sys/devices/system/cpu/cpu{core}/cpufreq/cpuinfo_max_freq");

    private static long ClockNow(int core) => Read($"/sys/devices/system/cpu/cpu{core}/cpufreq/scaling_cur_freq");

    private static long Read(string file)
    {
        try { return long.Parse(File.ReadAllText(file).Trim(), CultureInfo.InvariantCulture); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException) { return 0; }
    }

    /// <summary>The cores with the highest top clock.</summary>
    internal static int[] BigCores() => Clusters().Big;

    /// <summary>The clock this thread's core runs at now, timed: a chain of one-cycle instructions.</summary>
    internal static double ClockNowGhz() => GigahertzNow(1_000_000);

    // Runs body(i) on one thread per core named, each held to its core, and returns when all are done.
    internal static void OnCores(int[] cores, Action<int> body)
    {
        using var start = new Barrier(cores.Length);
        var threads = new Thread[cores.Length];
        Exception? failed = null;
        for (var i = 0; i < cores.Length; i++)
        {
            var index = i;
            threads[i] = new Thread(() =>
            {
                try
                {
                    var mask = 1UL << cores[index];
                    if (SetAffinity(0, sizeof(ulong), &mask) != 0) throw new InvalidOperationException($"core {cores[index]} refused the thread");
                    start.SignalAndWait();
                    body(index);
                }
                catch (Exception e)
                {
                    failed = e;
                }
            })
            { IsBackground = true, Name = $"Limits {index}" };
            threads[i].Start();
        }
        foreach (var thread in threads) thread.Join();
        if (failed is not null) throw new InvalidOperationException("a measuring thread failed", failed);
    }

    // ---- the clock ----

    // 32 additions and exclusive-ors, each waiting on the one before: 32 cycles a pass on any core
    // that does one a cycle, so the time taken is the clock the core really ran at.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static long Chain(long passes, long step)
    {
        var a = step;
        for (long i = 0; i < passes; i++)
        {
            a += step; a ^= step; a += step; a ^= step; a += step; a ^= step; a += step; a ^= step;
            a += step; a ^= step; a += step; a ^= step; a += step; a ^= step; a += step; a ^= step;
            a += step; a ^= step; a += step; a ^= step; a += step; a ^= step; a += step; a ^= step;
            a += step; a ^= step; a += step; a ^= step; a += step; a ^= step; a += step; a ^= step;
        }
        return a;
    }

    private static double GigahertzNow(long passes = 3_000_000)
    {
        var started = Stopwatch.GetTimestamp();
        GC.KeepAlive(Chain(passes, 3));
        return passes * 32.0 / Stopwatch.GetElapsedTime(started).TotalSeconds / 1e9;
    }

    private static IEnumerable<string> Clocks(int[] big, int[] little)
    {
        var lines = new List<string>();
        foreach (var (name, cores) in new[] { ("one big core", big.Take(1).ToArray()), ("one little core", little.Take(1).ToArray()), ("four big cores", big), ("all eight", big.Concat(little).ToArray()) })
        {
            if (cores.Length == 0) continue;
            var ghz = new double[cores.Length];
            OnCores(cores, i =>
            {
                GigahertzNow(); // the governor raises the clock under load: this is the load
                var until = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                while (Stopwatch.GetTimestamp() < until) ghz[i] = GigahertzNow(1_000_000);
            });
            lines.Add(F($"clock, {name} busy: {string.Join(" ", ghz.Select(g => g.ToString("F2", CultureInfo.InvariantCulture)))} GHz; the kernel says big {ClockNow(big[0]) / 1e6:F2}, top {TopClock(big[0]) / 1e6:F2}"));
        }
        return lines;
    }

    // ---- memory ----

    // Reads `bytes` at p once, in 64-byte steps, and returns a sum nothing can drop.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong ReadAll(byte* p, long bytes)
    {
        if (AdvSimd.IsSupported)
        {
            Vector128<ulong> a = default, b = default, c = default, d = default;
            for (long i = 0; i + 64 <= bytes; i += 64)
            {
                a = AdvSimd.Add(a, AdvSimd.LoadVector128((ulong*)(p + i)));
                b = AdvSimd.Add(b, AdvSimd.LoadVector128((ulong*)(p + i + 16)));
                c = AdvSimd.Add(c, AdvSimd.LoadVector128((ulong*)(p + i + 32)));
                d = AdvSimd.Add(d, AdvSimd.LoadVector128((ulong*)(p + i + 48)));
            }
            return (a + b + c + d).GetElement(0);
        }

        ulong s0 = 0, s1 = 0, s2 = 0, s3 = 0;
        for (long i = 0; i + 64 <= bytes; i += 64)
        {
            var q = (ulong*)(p + i);
            s0 += q[0] + q[4];
            s1 += q[1] + q[5];
            s2 += q[2] + q[6];
            s3 += q[3] + q[7];
        }
        return s0 + s1 + s2 + s3;
    }

    private static double ReadRate(byte* buffer, int[] cores, long bytesEach, bool shared)
    {
        var read = new long[cores.Length];
        var seconds = new double[cores.Length];
        var buf = (nint)buffer;
        OnCores(cores, i =>
        {
            var p = (byte*)buf + (shared ? 0 : i * (long)(BufferBytes / cores.Length));
            ReadAll(p, bytesEach); // warm: the JIT, and the cache for a size that fits one
            var started = Stopwatch.GetTimestamp();
            var until = started + Stopwatch.Frequency / 2;
            long done = 0;
            while (Stopwatch.GetTimestamp() < until)
            {
                ReadAll(p, bytesEach);
                done += bytesEach;
            }
            read[i] = done;
            seconds[i] = Stopwatch.GetElapsedTime(started).TotalSeconds;
        });
        return read.Sum() / seconds.Max() / 1e9;
    }

    private static IEnumerable<string> Bandwidth(byte* buffer, int[] big, int[] little)
    {
        var lines = new List<string>
        {
            F($"read, one big core: 16 KB (L1) {ReadRate(buffer, big[..1], 16 << 10, false):F1} GB/s, 256 KB (L2) {ReadRate(buffer, big[..1], 256 << 10, false):F1} GB/s, 128 MB (memory) {ReadRate(buffer, big[..1], BufferBytes, false):F2} GB/s"),
        };
        if (little.Length > 0)
            lines.Add(F($"read, one little core: 16 KB {ReadRate(buffer, little[..1], 16 << 10, false):F1} GB/s, 128 MB {ReadRate(buffer, little[..1], BufferBytes, false):F2} GB/s"));
        foreach (var n in new[] { 2, big.Length })
            if (n <= big.Length) lines.Add(F($"read from memory, {n} big cores: {ReadRate(buffer, big[..n], BufferBytes / n, false):F2} GB/s"));
        if (little.Length > 0)
        {
            lines.Add(F($"read from memory, {little.Length} little cores: {ReadRate(buffer, little, BufferBytes / little.Length, false):F2} GB/s"));
            lines.Add(F($"read from memory, all cores: {ReadRate(buffer, [.. big, .. little], BufferBytes / (big.Length + little.Length), false):F2} GB/s"));
        }
        return lines;
    }

    // ---- what a block costs ----

    private static (Ptq1Lut Lut, float[] Scales) Tables(int cols, int blocks)
    {
        var x = new float[cols];
        var rng = new Random(1);
        for (var i = 0; i < cols; i++) x[i] = (float)(rng.NextDouble() * 2 - 1);
        var q = new Q8Activations(cols);
        q.Quantize(x);
        var lut = new Ptq1Lut(cols);
        lut.Build(q);
        var scales = new float[blocks];
        Array.Fill(scales, 0.02f);
        return (lut, scales);
    }

    // Blocks a second through MatVec on each core named, each walking its own `rows`-row matrices
    // around `span` bytes of the buffer, for `seconds`, after a warm-up long enough for the JIT.
    private static double BlockRate(byte* buffer, int[] cores, int cols, int rows, long spanEach, double seconds = 1.0, double warm = 0.6)
    {
        var rowBytes = cols / Ptq1.BlockWeights * Ptq1.BlockBytes;
        var matrixBytes = (long)rows * rowBytes;
        var blocksEach = rows * (cols / Ptq1.BlockWeights);
        var (lut, scales) = Tables(cols, blocksEach);
        var done = new long[cores.Length];
        var taken = new double[cores.Length];
        var buf = (nint)buffer;
        OnCores(cores, i =>
        {
            var p = (byte*)buf + (long)i * (BufferBytes / cores.Length);
            var output = new float[rows];
            var matrices = Math.Max(1, spanEach / matrixBytes);
            long at = 0;
            void Pass()
            {
                lut.MatVec(new ReadOnlySpan<byte>(p + at * matrixBytes, (int)matrixBytes), scales, output);
                at = (at + 1) % matrices;
            }
            var warmUntil = Stopwatch.GetTimestamp() + (long)(warm * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < warmUntil) Pass();
            var started = Stopwatch.GetTimestamp();
            var until = started + (long)(seconds * Stopwatch.Frequency);
            long passes = 0;
            while (Stopwatch.GetTimestamp() < until)
            {
                Pass();
                passes++;
            }
            done[i] = passes * blocksEach;
            taken[i] = Stopwatch.GetElapsedTime(started).TotalSeconds;
        });
        return done.Sum() / taken.Max();
    }

    private static string BlockLine(string what, double blocksPerSecond, double gigahertz) => F(
        $"{what}: {1e9 / blocksPerSecond:F0} ns a block = {1e9 / blocksPerSecond * gigahertz:F0} cycles, {blocksPerSecond * Ptq1.BlockBytes / 1e9:F2} GB/s of weights; a token's products alone {BlocksPerToken / blocksPerSecond * 1000:F0} ms");

    private static IEnumerable<string> Blocks(byte* buffer, int[] big, int[] little)
    {
        var one = big[..1];
        var clock = BusyClock(one);
        var lines = new List<string>
        {
            BlockLine("block, one big core, tables and weights in L1 (7 KB)", BlockRate(buffer, one, 128, 256, 7 << 10), clock),
            BlockLine("block, one big core, weights in L2 (224 KB)", BlockRate(buffer, one, 128, 8192, 224 << 10), clock),
            BlockLine("block, one big core, weights from memory (120 MB)", BlockRate(buffer, one, 128, 8192, 120L << 20), clock),
            BlockLine($"block, one big core, real shape {Rows}x{Cols}, one matrix", BlockRate(buffer, one, Cols, Rows, 1), clock),
            BlockLine($"block, one big core, real shape {Rows}x{Cols}, from memory", BlockRate(buffer, one, Cols, Rows, 120L << 20), clock),
            BlockLine($"block, one big core, real shape {Cols}x{Rows}, from memory", BlockRate(buffer, one, Rows, Cols, 120L << 20), clock),
        };
        if (little.Length > 0)
            lines.Add(BlockLine($"block, one little core, real shape {Rows}x{Cols}, from memory", BlockRate(buffer, little[..1], Cols, Rows, 120L << 20), BusyClock(little[..1])));
        return lines;
    }

    // The clock a core runs at once it is busy.
    private static double BusyClock(int[] core)
    {
        var ghz = 0d;
        OnCores(core, _ =>
        {
            GigahertzNow();
            ghz = GigahertzNow();
        });
        return ghz;
    }

    private static IEnumerable<string> Scaling(byte* buffer, int[] big, int[] little)
    {
        var lines = new List<string>();
        void Add(string name, int[] cores)
        {
            var rate = BlockRate(buffer, cores, Cols, Rows, BufferBytes / cores.Length - (1 << 20), 1.5);
            lines.Add(F($"scaling, {name}, real shape from memory: {rate / 1e6:F1} M blocks/s, {rate * Ptq1.BlockBytes / 1e9:F2} GB/s of weights; a token's products alone {BlocksPerToken / rate * 1000:F0} ms"));
        }
        for (var n = 1; n <= big.Length; n++) Add($"{n} big", big[..n]);
        if (little.Length > 0)
        {
            Add($"{little.Length} little", little);
            Add("all cores", [.. big, .. little]);
        }
        return lines;
    }

    // ---- handing out the work ----

    private sealed class Nothing : WorkerPool.IWork
    {
        public void Execute(int worker, int workers) { }
    }

    private static IEnumerable<string> Pool(byte* buffer)
    {
        var lines = new List<string>();
        var nothing = new Nothing();
        var (lut, scales) = Tables(Cols, Rows * (Cols / Ptq1.BlockWeights));
        var rowBytes = Cols / Ptq1.BlockWeights * Ptq1.BlockBytes;
        var matrixBytes = (long)Rows * rowBytes;
        var matrices = (BufferBytes - (1 << 20)) / matrixBytes;
        var output = new float[Rows];
        foreach (var threads in new[] { 4, 7 })
        {
            if (threads > Environment.ProcessorCount) continue;
            using var pool = new WorkerPool(threads);
            using var hold = pool.KeepSpinning();
            for (var i = 0; i < 20_000; i++) pool.Run(nothing);
            var started = Stopwatch.GetTimestamp();
            const int runs = 50_000;
            for (var i = 0; i < runs; i++) pool.Run(nothing);
            var handOver = Stopwatch.GetElapsedTime(started).TotalMicroseconds / runs;

            for (var i = 0; i < 40; i++) lut.MatVec(new ReadOnlySpan<byte>(buffer + i % matrices * matrixBytes, (int)matrixBytes), scales, output, pool);
            started = Stopwatch.GetTimestamp();
            long products = 0;
            while (Stopwatch.GetElapsedTime(started).TotalSeconds < 1.5)
            {
                lut.MatVec(new ReadOnlySpan<byte>(buffer + products % matrices * matrixBytes, (int)matrixBytes), scales, output, pool);
                products++;
            }
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds / products;
            var rate = Rows * (Cols / Ptq1.BlockWeights) / (ms / 1000);
            lines.Add(F($"pool of {threads}, as the engine runs it, cores left to the scheduler: {handOver:F1} us to hand out nothing; real shape from memory {ms:F2} ms a product, {rate / 1e6:F1} M blocks/s, a token's products alone {BlocksPerToken / rate * 1000:F0} ms"));
        }
        return lines;
    }

    // ---- a sustained load ----

    private static IEnumerable<string> Sustained(byte* buffer, int[] big, int seconds)
    {
        var lines = new List<string>();
        var (lut, scales) = Tables(Cols, Rows * (Cols / Ptq1.BlockWeights));
        var rowBytes = Cols / Ptq1.BlockWeights * Ptq1.BlockBytes;
        var matrixBytes = (long)Rows * rowBytes;
        var blocks = Rows * (Cols / Ptq1.BlockWeights);
        var counts = new long[big.Length];
        var stop = 0;
        var buf = (nint)buffer;
        var slice = BufferBytes / big.Length;
        var matrices = Math.Max(1, (slice - (1 << 20)) / matrixBytes);

        var watcher = new Thread(() =>
        {
            long last = 0;
            for (var s = 0; s < seconds; s += 3)
            {
                Thread.Sleep(3000);
                var now = counts.Sum();
                var rate = (now - last) * (double)blocks / 3;
                last = now;
                lock (lines) lines.Add(F($"sustained, {big.Length} big cores, {s + 3,2} s: {rate / 1e6:F1} M blocks/s, the kernel says big {ClockNow(big[0]) / 1e6:F2} GHz, a token's products alone {BlocksPerToken / rate * 1000:F0} ms"));
            }
            Volatile.Write(ref stop, 1);
        })
        { IsBackground = true };
        watcher.Start();

        OnCores(big, i =>
        {
            var p = (byte*)buf + (long)i * slice;
            var output = new float[Rows];
            long at = 0;
            while (Volatile.Read(ref stop) == 0)
            {
                lut.MatVec(new ReadOnlySpan<byte>(p + at % matrices * matrixBytes, (int)matrixBytes), scales, output);
                at++;
                Interlocked.Increment(ref counts[i]);
            }
        });
        watcher.Join();
        return lines;
    }

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
