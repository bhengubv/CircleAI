// KernelThreads.cs
//
// THE FASTEST WAY THIS DEVICE RUNS THE C# ENGINE'S PRODUCTS, MEASURED ONCE (Step 4, "primed").
//
// The engine's ternary products run on the lookup-table kernel (Ptq1Lut) on a pool of threads, and
// which count is fastest is a fact about the device, not about the code: on the P30 three threads
// beat four and eight, because its little cores held the big ones back (2026-10-30), while a phone
// with two big cores and six little ones may do best with all eight sharing the rows. The thread
// count was a rule of thumb (DeviceContext.BrainThreads: the fast cores, one left over). Before a
// brain says it is ready, the first one on a device now measures the kernel at a 2B's real shape on
// one thread and up, picks the fewest threads within 5% of the best, and keeps the answer - so no
// later load pays for the measurement again. The rule of thumb stays as the fallback.
//
// THE LOOKUP-TABLE KERNEL IS THE ONLY PATH THE MODEL'S PRODUCTS TAKE (Qwen35Model.Product), so the
// thread count is the one thing about that path a device can choose; which vector instructions the
// other kernels use is a property of the CPU (Ptq1.Best), not a measurement.
//
// KEPT PER DEVICE AND PER RUNTIME: a new build, a different runtime (Mono or CoreCLR) or a
// different core count measures again, because any of those can change the answer.

using System.Globalization;
using System.Runtime.InteropServices;
using CircleAI.Core.Logging;

namespace CircleAI.Inference.Managed.Kernels;

/// <summary>Picks, by measurement, how many threads the C# engine's products run on here.</summary>
public static class KernelThreads
{
    private const string Area = "Engine";
    private static readonly object Gate = new();
    private static Choice? _known;

    /// <summary>What was measured and chosen.</summary>
    /// <param name="Threads">The thread count chosen.</param>
    /// <param name="TokensPerSecond">What the products alone would reach on it, for a 2B.</param>
    /// <param name="Fingerprint">The device and runtime it was measured on.</param>
    /// <param name="Ptq1GigabytesPerSecond">The fastest PTQ1_0 weights were read here, at any thread count; 0 when not measured.</param>
    /// <param name="Pq2GigabytesPerSecond">The same for PQ2_0; 0 when not measured.</param>
    public sealed record Choice(int Threads, double TokensPerSecond, string Fingerprint,
                                double Ptq1GigabytesPerSecond = 0, double Pq2GigabytesPerSecond = 0)
    {
        /// <summary>
        /// How much faster a model packed as <paramref name="to"/> reads its weights here than one packed as
        /// <paramref name="from"/> - the factor a speed measured in one packing carries to the other
        /// (BrainSpeed); null when either was not measured or the packing is not the C# engine's.
        /// </summary>
        public double? PackingFactor(string? from, string? to)
        {
            var a = Rate(from);
            var b = Rate(to);
            return a > 0 && b > 0 ? b / a : null;
        }

        private double Rate(string? packing) => Packing(packing) switch
        {
            "PTQ1_0" => Ptq1GigabytesPerSecond,
            "PQ2_0" => Pq2GigabytesPerSecond,
            _ => 0,
        };
    }

    /// <summary>The kernel a catalogue row's quantization runs on: PTQ1_0, PQ2_0 (with or without its MTP head), or null.</summary>
    public static string? Packing(string? quantization) =>
        quantization is null ? null
        : quantization.StartsWith("PQ2_0", StringComparison.OrdinalIgnoreCase) ? "PQ2_0"
        : quantization.StartsWith("PTQ1_0", StringComparison.OrdinalIgnoreCase) ? "PTQ1_0"
        : null;

    /// <summary>Within this share of the best, fewer threads win: they leave cores for the person.</summary>
    public const double CloseEnough = 0.05;

    /// <summary>The most threads tried.</summary>
    public const int MostThreads = 8;

    /// <summary>The choice made on this device, once there is one; null before.</summary>
    public static Choice? Known
    {
        get { lock (Gate) return _known; }
    }

    /// <summary>Where the choice is kept: the platform's cache folder, beside the primed starts.</summary>
    public static string DefaultDirectory => Path.Combine(Path.GetTempPath(), "CircleAI");

    /// <summary>A measurement in place of the real one. For tests.</summary>
    internal static Func<int, IReadOnlyList<KernelBench.Result>>? MeasureForTest { get; set; }

    /// <summary>The PQ2_0 measurement in place of the real one; with only <see cref="MeasureForTest"/> set, none is taken. For tests.</summary>
    internal static Func<int, IReadOnlyList<KernelBench.Result>>? MeasurePq2ForTest { get; set; }

    /// <summary>The file the choice is kept in, inside the directory.</summary>
    public const string FileName = "kernel-threads.txt";

    /// <summary>What this device measured, read from where it is kept - never measured here; null when nothing was.</summary>
    /// <remarks>
    /// FOR A PROCESS THAT PLANS RATHER THAN RUNS: the service's main process chooses the brain and
    /// the inference process builds it, and the measurement is taken where the first C# brain is
    /// built. Both are one app with one cache folder, so the planner reads the file the builder wrote.
    /// </remarks>
    public static Choice? Remembered(string? directory = null)
    {
        lock (Gate)
        {
            var fingerprint = Fingerprint();
            if (_known is { } known && known.Fingerprint == fingerprint) return known;
            var stored = Read(Path.Combine(directory ?? DefaultDirectory, FileName));
            return stored is not null && stored.Fingerprint == fingerprint ? stored : null;
        }
    }

    /// <summary>
    /// The thread count to run the products on: remembered when this device and runtime measured it
    /// before, measured now when not, and <paramref name="fallback"/> when measuring fails.
    /// </summary>
    /// <param name="fallback">The rule of thumb, used when nothing can be measured.</param>
    /// <param name="directory">Where the choice is kept; null uses <see cref="DefaultDirectory"/>.</param>
    public static int Choose(int fallback, string? directory = null)
    {
        lock (Gate)
        {
            var fingerprint = Fingerprint();
            if (_known is { } known && known.Fingerprint == fingerprint) return known.Threads;

            var file = Path.Combine(directory ?? DefaultDirectory, FileName);
            if (Read(file) is { } stored && stored.Fingerprint == fingerprint)
            {
                _known = stored;
                return stored.Threads;
            }

            try
            {
                var most = Math.Clamp(Environment.ProcessorCount, 1, MostThreads);
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var results = MeasureForTest?.Invoke(most) ?? KernelBench.Ptq1LutThreads(passes: 4, maxThreads: most).Results;
                var choice = Pick(results, fingerprint) ?? new Choice(Math.Max(1, fallback), 0, fingerprint);

                // AND THE OTHER PACKING, WHILE THE DEVICE IS BEING MEASURED (OPEN-GAPS C8): a speed
                // measured on a PTQ1_0 brain said nothing true about a PQ2_0 one - this PC read PQ2_0
                // about 3.6 times faster, the P30 a little slower - so how fast each packing reads its
                // weights here is kept beside the threads, for the planner to scale by.
                var pq2 = MeasurePq2ForTest?.Invoke(most)
                          ?? (MeasureForTest is null ? KernelBench.Pq2LutThreads(passes: 4, maxThreads: most).Results : Array.Empty<KernelBench.Result>());
                choice = choice with { Pq2GigabytesPerSecond = Fastest(pq2) };

                _known = choice;
                Write(file, choice);
                CircleLog.Info(Area, string.Create(CultureInfo.InvariantCulture,
                    $"kernel measured: the lookup-table products run fastest on {choice.Threads} thread(s) here (~{choice.TokensPerSecond:F2} tok/s for a 2B's products), measured in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms; {string.Join(", ", results.Select(r => $"{r.Threads}t {r.TokensPerSecond:F2}"))}; weights read at {choice.Ptq1GigabytesPerSecond:F3} GB/s in PTQ1_0, {choice.Pq2GigabytesPerSecond:F3} in PQ2_0"));
                return choice.Threads;
            }
            catch (Exception ex)
            {
                CircleLog.Warn(Area, $"the kernel could not be measured ({ex.GetType().Name}); running on {fallback} thread(s), the device's rule of thumb");
                return Math.Max(1, fallback);
            }
        }
    }

    /// <summary>The fewest threads within <see cref="CloseEnough"/> of the fastest; null for no results.</summary>
    public static Choice? Pick(IReadOnlyList<KernelBench.Result> results, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(results);
        var usable = results.Where(r => r.Threads > 0 && r.TokensPerSecond > 0 && !double.IsInfinity(r.TokensPerSecond)).ToList();
        if (usable.Count == 0) return null;

        var best = usable.Max(r => r.TokensPerSecond);
        var pick = usable.Where(r => r.TokensPerSecond >= best * (1 - CloseEnough))
                         .OrderBy(r => r.Threads)
                         .First();
        return new Choice(pick.Threads, pick.TokensPerSecond, fingerprint, Ptq1GigabytesPerSecond: Fastest(usable));
    }

    // The fastest weights were read at any thread count, or 0 for nothing measured.
    private static double Fastest(IReadOnlyList<KernelBench.Result> results)
    {
        var rates = results.Where(r => r.GigabytesPerSecond > 0 && !double.IsInfinity(r.GigabytesPerSecond)).Select(r => r.GigabytesPerSecond).ToList();
        return rates.Count > 0 ? rates.Max() : 0;
    }

    /// <summary>Forgets the choice in memory (the file stays). For tests.</summary>
    internal static void Forget()
    {
        lock (Gate) _known = null;
    }

    /// <summary>What a remembered choice must match: cores, runtime, architecture, the best vector path - and what is measured.</summary>
    /// <remarks>
    /// "PACKINGS" IS PART OF IT (C8): a choice kept before both packings were measured carries no
    /// PQ2_0 rate, so a device that holds one measures once more rather than plan on half the facts.
    /// </remarks>
    internal static string Fingerprint() => string.Create(CultureInfo.InvariantCulture,
        $"{Environment.ProcessorCount}|{RuntimeInformation.ProcessArchitecture}|{RuntimeInformation.FrameworkDescription}|{Ptq1.Best}|packings");

    private static Choice? Read(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var parts = File.ReadAllText(file).Split('\n');
            if (parts.Length < 3) return null;
            if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var threads) || threads <= 0
                || !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
                return null;
            return new Choice(threads, rate, parts[2].Trim(), Rate(parts, 3), Rate(parts, 4));
        }
        catch
        {
            return null;
        }
    }

    // A rate kept on line <paramref name="line"/>, or 0 for a file written before it was measured.
    private static double Rate(string[] parts, int line) =>
        parts.Length > line && double.TryParse(parts[line].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) && rate > 0
            ? rate : 0;

    private static void Write(string file, Choice choice)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, string.Create(CultureInfo.InvariantCulture,
                $"{choice.Threads}\n{choice.TokensPerSecond:R}\n{choice.Fingerprint}\n{choice.Ptq1GigabytesPerSecond:R}\n{choice.Pq2GigabytesPerSecond:R}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Measured again next time; slower, never wrong.
        }
    }
}
