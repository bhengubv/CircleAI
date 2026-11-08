// FlashProfile.cs
//
// WHAT THIS DEVICE'S FLASH CAN DO, MEASURED ONCE, AND THE RECIPE IT GIVES. Straight reads on one reader
// and on four; scattered reads of 4 KB, 64 KB, 256 KB and 1 MB on one, two, four and eight readers at
// once. Flash is many dies side by side, so one reader leaves most of them idle; how many readers it
// takes to fill them, and how big a read has to be before scattered reads run near full speed, are
// facts about the device, not the code.
//
// THE NUMBERS ARE THE FLASH'S, NOT RAM'S. Every read goes past the OS's cache where the device allows
// it, and every range read is dropped from the cache straight after, so a later read of it comes from
// flash again; the whole file is dropped before each test. Without that, the second pass over a file
// reads RAM and the profile lies.
//
// The recipe: the smallest read whose best speed is within 10% of the best of all, and the fewest
// readers within 5% of that size's best. Kept per device, beside the kernel's thread choice.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace CircleAI.Inference.Managed.Store;

/// <summary>What a device's flash measured, and the chunk size and channel count it gives.</summary>
/// <param name="Direct">Whether reads went past the OS's cache.</param>
/// <param name="Straight1">Straight reads on one reader, MB/s.</param>
/// <param name="Straight4">Straight reads on four readers (or every core, when fewer), MB/s.</param>
/// <param name="Scattered">Scattered reads by size and readers.</param>
/// <param name="ChunkBytes">The recipe's read size.</param>
/// <param name="Channels">The recipe's readers.</param>
/// <param name="Fingerprint">The device, runtime and folder it was measured on.</param>
public sealed record FlashProfile(bool Direct, double Straight1, double Straight4, IReadOnlyList<FlashProfile.Point> Scattered,
    int ChunkBytes, int Channels, string Fingerprint)
{
    /// <summary>One scattered test: reads of <paramref name="Bytes"/> on <paramref name="Channels"/> readers at once.</summary>
    public sealed record Point(int Bytes, int Channels, double MegabytesPerSecond);

    /// <summary>The file the profile is kept in.</summary>
    public const string FileName = "flash-profile.txt";

    /// <summary>The read sizes tried.</summary>
    public static IReadOnlyList<int> Sizes { get; } = [4096, 64 * 1024, 256 * 1024, 1024 * 1024];

    private const int StraightRead = 1024 * 1024;

    /// <summary>The profile kept for this device in <paramref name="directory"/>; null when none is, or it was another device's.</summary>
    public static FlashProfile? Remembered(string directory)
    {
        try
        {
            var file = Path.Combine(directory, FileName);
            if (!File.Exists(file)) return null;
            var lines = File.ReadAllLines(file);
            if (lines.Length < 6 || lines[0] != FingerprintOf(directory)) return null;
            var points = new List<Point>();
            foreach (var line in lines.Skip(6))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3) points.Add(new Point(Int(parts[0]), Int(parts[1]), Real(parts[2])));
            }
            return new FlashProfile(lines[1] == "direct", Real(lines[2]), Real(lines[3]), points, Int(lines[4]), Int(lines[5]), lines[0]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }

        static int Int(string s) => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
        static double Real(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Measures the flash <paramref name="path"/> sits on - a file of at least a few MB, read and never
    /// written - and keeps the profile in <paramref name="directory"/>. Each test runs for <paramref name="perTest"/>.
    /// </summary>
    public static FlashProfile Measure(string path, string directory, TimeSpan? perTest = null, CancellationToken ct = default)
    {
        var time = perTest ?? TimeSpan.FromMilliseconds(400);
        using var file = FlashFile.Open(path);
        var length = file.Length / FlashFile.Page * FlashFile.Page;
        if (length < 4L * 1024 * 1024) throw new InvalidOperationException($"{path} is {file.Length} bytes; the flash is measured over a few MB at least.");

        var most = Math.Clamp(Environment.ProcessorCount, 1, 8);
        var readers = new[] { 1, 2, 4, 8 }.Where(n => n <= most).ToArray();
        using var buffers = new Buffers(readers.Max(), Math.Max(StraightRead, Sizes.Max()));

        var straight1 = ReadStraight(file, length, 1, time, buffers, ct);
        var straight4 = ReadStraight(file, length, Math.Min(4, most), time, buffers, ct);
        var points = new List<Point>();
        foreach (var size in Sizes)
        {
            foreach (var n in readers)
                points.Add(new Point(size, n, ReadScattered(file, length, size, n, time, buffers, ct)));
        }

        var best = points.Max(p => p.MegabytesPerSecond);
        var chunk = Sizes.First(s => points.Where(p => p.Bytes == s).Max(p => p.MegabytesPerSecond) >= best * 0.9);
        var atChunk = points.Where(p => p.Bytes == chunk).ToList();
        var bestAtChunk = atChunk.Max(p => p.MegabytesPerSecond);
        var channels = atChunk.Where(p => p.MegabytesPerSecond >= bestAtChunk * 0.95).Min(p => p.Channels);

        var profile = new FlashProfile(file.Direct, straight1, straight4, points, chunk, channels, FingerprintOf(directory));
        Keep(profile, directory);
        return profile;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"reads past the cache: {(Direct ? "yes" : "no, dropped after each read")}; straight {Straight1:F0} MB/s on one reader, {Straight4:F0} on four; scattered");
        foreach (var size in Scattered.Select(p => p.Bytes).Distinct())
        {
            var row = Scattered.Where(p => p.Bytes == size).OrderBy(p => p.Channels).ToList();
            text.Append(CultureInfo.InvariantCulture, $" {Size(size)} {string.Join("/", row.Select(p => p.MegabytesPerSecond.ToString("F0", CultureInfo.InvariantCulture)))}");
        }
        var counts = Scattered.Select(p => p.Channels).Distinct().OrderBy(n => n);
        text.Append(CultureInfo.InvariantCulture, $" MB/s on {string.Join("/", counts)} readers; recipe: {Size(ChunkBytes)} reads on {Channels} channel(s)");
        return text.ToString();
    }

    private static string Size(int bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024)} MB" : $"{bytes / 1024} KB";

    // Straight reads: each reader its own share of the file, start to end, in 1 MB reads.
    private static unsafe double ReadStraight(FlashFile file, long length, int readers, TimeSpan time, Buffers buffers, CancellationToken ct)
    {
        file.Drop(0, length);
        var share = length / readers / FlashFile.Page * FlashFile.Page;
        return Run(readers, time, ct, (reader, deadline) =>
        {
            long bytes = 0;
            var from = reader * share;
            var at = from;
            do
            {
                if (at + StraightRead > from + share) break;
                bytes += file.Read(buffers[reader], StraightRead, at);
                file.Drop(at, StraightRead);
                at += StraightRead;
            }
            while (Stopwatch.GetTimestamp() < deadline); // a reader that started late still reads once
            return bytes;
        });
    }

    // Scattered reads: each reader at random whole-page offsets of its own.
    private static unsafe double ReadScattered(FlashFile file, long length, int size, int readers, TimeSpan time, Buffers buffers, CancellationToken ct)
    {
        file.Drop(0, length);
        var places = (length - size) / FlashFile.Page;
        return Run(readers, time, ct, (reader, deadline) =>
        {
            long bytes = 0;
            var random = new Random(size * 31 + reader);
            do
            {
                var at = random.NextInt64(0, places + 1) * FlashFile.Page;
                bytes += file.Read(buffers[reader], size, at);
                file.Drop(at, size);
            }
            while (Stopwatch.GetTimestamp() < deadline); // a reader that started late still reads once
            return bytes;
        });
    }

    // Runs one test on `readers` threads for `time`; MB/s over all of them.
    private static double Run(int readers, TimeSpan time, CancellationToken ct, Func<int, long, long> read)
    {
        ct.ThrowIfCancellationRequested();
        var totals = new long[readers];
        var started = Stopwatch.GetTimestamp();
        var deadline = started + (long)(time.TotalSeconds * Stopwatch.Frequency);
        var threads = new Thread[readers];
        Exception? failed = null;
        for (var t = 0; t < readers; t++)
        {
            var reader = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    totals[reader] = read(reader, deadline);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref failed, ex, null);
                }
            }) { IsBackground = true, Name = $"flash-reader-{reader}" };
            threads[t].Start();
        }
        foreach (var thread in threads) thread.Join();
        if (failed is not null) throw failed;
        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        return seconds <= 0 ? 0 : totals.Sum() / seconds / (1024.0 * 1024.0);
    }

    private static string FingerprintOf(string directory) => string.Create(CultureInfo.InvariantCulture,
        $"{Environment.ProcessorCount}|{RuntimeInformation.ProcessArchitecture}|{RuntimeInformation.FrameworkDescription}|{Path.GetFullPath(directory)}");

    private static void Keep(FlashProfile profile, string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var lines = new List<string>
            {
                profile.Fingerprint,
                profile.Direct ? "direct" : "cached",
                profile.Straight1.ToString("R", CultureInfo.InvariantCulture),
                profile.Straight4.ToString("R", CultureInfo.InvariantCulture),
                profile.ChunkBytes.ToString(CultureInfo.InvariantCulture),
                profile.Channels.ToString(CultureInfo.InvariantCulture),
            };
            lines.AddRange(profile.Scattered.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Bytes} {p.Channels} {p.MegabytesPerSecond:R}")));
            File.WriteAllLines(Path.Combine(directory, FileName), lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Measured again next time: slower, never wrong.
        }
    }

    // One aligned buffer per reader.
    private sealed unsafe class Buffers : IDisposable
    {
        private readonly nint[] _buffers;

        public Buffers(int count, int bytes)
        {
            _buffers = new nint[count];
            for (var i = 0; i < count; i++) _buffers[i] = (nint)FlashFile.Allocate(bytes);
        }

        public byte* this[int reader] => (byte*)_buffers[reader];

        public void Dispose()
        {
            for (var i = 0; i < _buffers.Length; i++)
            {
                FlashFile.Free((byte*)_buffers[i]);
                _buffers[i] = 0;
            }
        }
    }
}
