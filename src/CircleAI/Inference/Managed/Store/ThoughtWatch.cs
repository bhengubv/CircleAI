// ThoughtWatch.cs
//
// WATCH: WHAT EACH WORD THOUGHT, AND WHICH NEURONS IT FIRED, LAYER BY LAYER (the indexed mind, plan step 4). The record the
// index is built from: for every word that runs while a watch is on, at every layer, a small sketch of the thought entering
// the feed-forward and the neurons that fired strongly there - silu(gate) x up, their true strength. On flash, appended as
// the words run; RAM holds one record.
//
// THE SKETCH (ThoughtSketch): the thought - the normed state, the embedding's width - hashed into 64 buckets with a sign
// each, every coordinate in one bucket, so two thoughts' sketches keep their inner product in expectation at one add a
// coordinate. The same hashing everywhere (a fixed seed): the index compares a word's sketch with the ones Watch saw.
// THE STRONG NEURONS: the largest by size, as many as the watch keeps, as a bitmap.
//
// Layout, little-endian:
//   header   magic "CAIWATCH", version, layers, embedding, feed-forward, sketch width, neurons kept a record
//   records  layer (int16), the sketch (sketch width halves), the strong neurons (feed-forward bits, whole bytes)

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace CircleAI.Inference.Managed.Store;

/// <summary>A thought hashed into <see cref="Width"/> signed buckets: inner products kept in expectation, one add a coordinate.</summary>
public sealed class ThoughtSketch
{
    /// <summary>Buckets a sketch has.</summary>
    public const int Width = 64;

    private const int Seed = 0x1D_E7ED; // fixed: every watch and every index hash a thought alike
    private readonly byte[] _bucket;
    private readonly float[] _sign;

    /// <summary>The sketch for thoughts <paramref name="embedding"/> wide.</summary>
    public ThoughtSketch(int embedding)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(embedding, 1);
        Embedding = embedding;
        _bucket = new byte[embedding];
        _sign = new float[embedding];
        var rng = new Random(Seed);
        for (var k = 0; k < embedding; k++)
        {
            _bucket[k] = (byte)rng.Next(Width);
            _sign[k] = rng.Next(2) == 0 ? -1f : 1f;
        }
    }

    /// <summary>The width of the thoughts it sketches.</summary>
    public int Embedding { get; }

    /// <summary><paramref name="thought"/>'s sketch into <paramref name="into"/> (<see cref="Width"/> long), scaled to unit length.</summary>
    public void Sketch(ReadOnlySpan<float> thought, Span<float> into)
    {
        if (thought.Length != Embedding) throw new ArgumentException($"Thoughts are {Embedding} wide.", nameof(thought));
        into = into[..Width];
        into.Clear();
        for (var k = 0; k < thought.Length; k++) into[_bucket[k]] += _sign[k] * thought[k];
        double norm = 0;
        foreach (var v in into) norm += (double)v * v;
        if (norm <= 0) return;
        var scale = (float)(1 / Math.Sqrt(norm));
        for (var j = 0; j < into.Length; j++) into[j] *= scale;
    }
}

/// <summary>Records every word's thought sketch and strong neurons at every layer, to a file on flash (<see cref="ThoughtWatch"/>'s layout).</summary>
public sealed class ThoughtWatch : IDisposable
{
    /// <summary>"CAIWATCH", little-endian.</summary>
    public const ulong Magic = 0x4843_5441_5749_4143;

    /// <summary>The layout's version.</summary>
    public const int Version = 1;

    private const int HeaderBytes = 64;
    private readonly FileStream _stream;
    private readonly ThoughtSketch _sketch;
    private readonly float[] _sketched = new float[ThoughtSketch.Width];
    private readonly int[] _buckets = new int[2048];
    private readonly byte[] _record;
    private int _disposed;

    /// <summary>
    /// A watch writing to <paramref name="path"/> for a model of <paramref name="layers"/> layers, thoughts
    /// <paramref name="embedding"/> wide and <paramref name="feedForward"/> neurons a layer, keeping the strongest
    /// <paramref name="kept"/> of them a record. Replaces what the file held.
    /// </summary>
    public ThoughtWatch(string path, int layers, int embedding, int feedForward, int kept)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(layers, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(feedForward, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(kept, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(kept, feedForward);
        Layers = layers;
        Embedding = embedding;
        FeedForward = feedForward;
        Kept = kept;
        _sketch = new ThoughtSketch(embedding);
        _record = new byte[RecordBytes(feedForward)];
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        FilePath = path;
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        var header = new byte[HeaderBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), Version);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), layers);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), embedding);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), feedForward);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), ThoughtSketch.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), kept);
        _stream.Write(header);
    }

    /// <summary>The file.</summary>
    public string FilePath { get; }

    /// <summary>Layers a word passes.</summary>
    public int Layers { get; }

    /// <summary>The thoughts' width.</summary>
    public int Embedding { get; }

    /// <summary>Neurons in a layer's feed-forward.</summary>
    public int FeedForward { get; }

    /// <summary>Strong neurons kept a record.</summary>
    public int Kept { get; }

    /// <summary>Records written.</summary>
    public long Records { get; private set; }

    // A record's bytes: the layer, the sketch as halves, the neurons as bits.
    internal static int RecordBytes(int feedForward) => 2 + ThoughtSketch.Width * 2 + (feedForward + 7) / 8;

    /// <summary>
    /// One word at layer <paramref name="layer"/>: its <paramref name="thought"/> (the state entering the feed-forward)
    /// and its feed-forward <paramref name="activations"/> (silu(gate) x up), the strongest <see cref="Kept"/> of them marked.
    /// </summary>
    public void Record(int layer, ReadOnlySpan<float> thought, ReadOnlySpan<float> activations)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (activations.Length != FeedForward) throw new ArgumentException($"A layer has {FeedForward} neurons.", nameof(activations));
        var record = _record.AsSpan();
        record.Clear();
        BinaryPrimitives.WriteInt16LittleEndian(record, (short)layer);
        _sketch.Sketch(thought, _sketched);
        var halves = MemoryMarshal.Cast<byte, Half>(record.Slice(2, ThoughtSketch.Width * 2));
        for (var j = 0; j < ThoughtSketch.Width; j++) halves[j] = (Half)_sketched[j];
        Mark(activations, record[(2 + ThoughtSketch.Width * 2)..]);
        _stream.Write(record);
        Records++;
    }

    // The strongest Kept activations' bits: a histogram of their sizes' float bits (exponent and three mantissa bits, which
    // order positive floats as they compare) finds the threshold in one pass, and ties at it go in index order.
    private void Mark(ReadOnlySpan<float> activations, Span<byte> bits)
    {
        Array.Clear(_buckets);
        foreach (var a in activations) _buckets[Bucket(a)]++;
        var cut = _buckets.Length - 1;
        var above = 0;
        while (cut > 0 && above + _buckets[cut] < Kept) above += _buckets[cut--];
        var atCut = Kept - above;
        for (var i = 0; i < activations.Length; i++)
        {
            var b = Bucket(activations[i]);
            if (b < cut || (b == cut && atCut-- <= 0)) continue;
            bits[i >> 3] |= (byte)(1 << (i & 7));
        }
    }

    private static int Bucket(float a) => (int)((BitConverter.SingleToUInt32Bits(a) & 0x7FFF_FFFF) >> 20);

    /// <summary>Writes what is buffered to flash and drops it from the cache: the record is on flash, and only there.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        FlashFile.Settle(_stream, 0, _stream.Length);
        _stream.Dispose();
    }

    /// <summary>A watch's records, read back from <paramref name="path"/> in file order.</summary>
    public static WatchRecords Read(string path) => new(path);
}

/// <summary>The records a <see cref="ThoughtWatch"/> wrote: its header, and each record in turn.</summary>
public sealed class WatchRecords
{
    internal WatchRecords(string path)
    {
        FilePath = path;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 12);
        var header = new byte[64];
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt64LittleEndian(header) != ThoughtWatch.Magic || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8)) != ThoughtWatch.Version)
            throw new InvalidDataException($"{path} is not a watch of this version.");
        Layers = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        Embedding = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
        FeedForward = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20));
        if (BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24)) != ThoughtSketch.Width) throw new InvalidDataException($"{path} sketches another width.");
        Kept = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(28));
        Count = (stream.Length - 64) / ThoughtWatch.RecordBytes(FeedForward);
    }

    /// <summary>The file.</summary>
    public string FilePath { get; }

    /// <summary>Layers a word passed.</summary>
    public int Layers { get; }

    /// <summary>The thoughts' width.</summary>
    public int Embedding { get; }

    /// <summary>Neurons in a layer's feed-forward.</summary>
    public int FeedForward { get; }

    /// <summary>Strong neurons kept a record.</summary>
    public int Kept { get; }

    /// <summary>Records in the file.</summary>
    public long Count { get; }

    /// <summary>Every record in file order: its layer, its sketch, and the bitmap of its strong neurons (valid until the next).</summary>
    public IEnumerable<(int Layer, float[] Sketch, byte[] Strong)> Each()
    {
        var size = ThoughtWatch.RecordBytes(FeedForward);
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        stream.Position = 64;
        var record = new byte[size];
        var sketch = new float[ThoughtSketch.Width];
        var strong = new byte[(FeedForward + 7) / 8];
        for (long r = 0; r < Count; r++)
        {
            stream.ReadExactly(record);
            var halves = MemoryMarshal.Cast<byte, Half>(record.AsSpan(2, ThoughtSketch.Width * 2));
            for (var j = 0; j < ThoughtSketch.Width; j++) sketch[j] = (float)halves[j];
            record.AsSpan(2 + ThoughtSketch.Width * 2).CopyTo(strong);
            yield return (BinaryPrimitives.ReadInt16LittleEndian(record), sketch, strong);
        }
    }
}
