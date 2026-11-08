// FfnStore.cs
//
// THE FEED-FORWARD LAYERS ON FLASH, IN THE SHAPE THE VECTOR KERNEL COMPUTES ON. Every layer's gate, up and
// down, repacked once into Ptq1PairMatrix's tiles and written to a file beside the head's store: about 255 MB
// of the 2B that a paired model otherwise keeps in RAM for as long as it is loaded, whether anyone is talking
// or not. A turn that finds them cold streams them in whole (FfnWarm) on the device's channels and the layers
// read them where they land; they stay warm while turns keep coming, and the device's recipe decides when an
// idle engine lets them go - at once where the flash brings them back in a fraction of a second, after a few
// minutes where it takes a second.
//
// Whole matrices, so a warm feed-forward computes exactly what the copy did: the same tiles, the same kernel.
//
// Layout, little-endian:
//   page 0   header: magic, version, the model it was cut from (its length), layers, embedding and feed-forward
//            widths, tensor count, where the index and the data are, the data's length
//   index    per tensor (layer by layer: gate, up, down): layer, kind, rows, columns, its GGUF offset and bytes,
//            where its tiles and scales are in the data, and their lengths
//   data     each tensor's tiles, then its scales, each whole pages
//
// Written to a ".partial" file and renamed when whole.

using System.Buffers.Binary;
using System.Diagnostics;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Kernels;
using CircleAI.Inference.Managed.Model;

namespace CircleAI.Inference.Managed.Store;

/// <summary>A model's feed-forward weights in the vector kernel's tiles, in a file on flash.</summary>
public sealed class FfnStore
{
    /// <summary>"CAIFFN01", little-endian.</summary>
    public const ulong Magic = 0x3130_4E46_4649_4143;

    /// <summary>The layout's version.</summary>
    public const int Version = 1;

    private const int EntryBytes = 64;
    private static readonly string[] Kinds = ["ffn_gate", "ffn_up", "ffn_down"];

    /// <summary>One tensor in the store: what it is, and where its tiles and scales are in the data.</summary>
    public readonly record struct Entry(int Layer, int Kind, int Rows, int Columns, long GgufOffset, long GgufBytes,
        long WeightsOffset, long WeightsBytes, long ScalesOffset, long ScaleCount);

    private FfnStore(string path, int layers, long dataOffset, long dataBytes, IReadOnlyList<Entry> entries)
    {
        FilePath = path;
        Layers = layers;
        DataOffset = dataOffset;
        DataBytes = dataBytes;
        Entries = entries;
    }

    /// <summary>The file.</summary>
    public string FilePath { get; }

    /// <summary>Layers held: every one of the model's.</summary>
    public int Layers { get; }

    /// <summary>Where the data starts in the file.</summary>
    public long DataOffset { get; }

    /// <summary>The data's length: what a warm feed-forward holds in RAM.</summary>
    public long DataBytes { get; }

    /// <summary>Every tensor, layer by layer: gate, up, down.</summary>
    public IReadOnlyList<Entry> Entries { get; }

    /// <summary>Why <paramref name="model"/>'s feed-forward cannot be stored this way; null when it can.</summary>
    public static string? Unreadable(GgufFile model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Qwen35Config c;
        try
        {
            c = Qwen35Config.FromGguf(model);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or KeyNotFoundException)
        {
            return ex.Message;
        }
        for (var il = 0; il < c.Layers; il++)
        {
            foreach (var kind in Kinds)
            {
                var tensor = model.Tensor($"blk.{il}.{kind}.weight");
                if (tensor is null) return $"the model has no blk.{il}.{kind}.weight";
                if (tensor.Type != GgmlType.PTQ1_0) return $"blk.{il}.{kind}.weight is {tensor.Type}; the vector kernel's tiles are PTQ1_0's";
                if (tensor.Rows % Ptq1PairMatrix.TileRows != 0 || tensor.RowLength % Ptq1.BlockWeights != 0) return $"blk.{il}.{kind}.weight is not whole tiles";
            }
        }
        return null;
    }

    /// <summary>
    /// Writes the store of <paramref name="model"/>'s feed-forward to <paramref name="path"/>; returns it, opened. Each tensor
    /// is read from the model's file past the cache, in reads of <paramref name="readBytes"/> on <paramref name="channels"/>
    /// readers (the device's recipe), repacked on the cores, and written and let go before the next.
    /// </summary>
    public static unsafe FfnStore Write(GgufFile model, string path, CancellationToken ct = default, int channels = 4, int readBytes = 1 << 20)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (Unreadable(model) is { } why) throw new NotSupportedException(why);
        var c = Qwen35Config.FromGguf(model);

        // The index first: every tensor's place.
        var entries = new List<Entry>(c.Layers * Kinds.Length);
        long at = 0;
        for (var il = 0; il < c.Layers; il++)
        {
            for (var k = 0; k < Kinds.Length; k++)
            {
                var tensor = model.Tensor($"blk.{il}.{Kinds[k]}.weight")!;
                var rows = checked((int)tensor.Rows);
                var cols = checked((int)tensor.RowLength);
                var weights = Ptq1PairMatrix.WeightBytes(rows, cols);
                var scales = Ptq1PairMatrix.ScaleCount(rows, cols);
                var entry = new Entry(il, k, rows, cols, tensor.Offset, tensor.ByteCount, at, weights, at + FlashFile.Pages(weights), scales);
                entries.Add(entry);
                at = entry.ScalesOffset + FlashFile.Pages(scales * sizeof(float));
            }
        }
        var indexBytes = FlashFile.Pages((long)entries.Count * EntryBytes);
        var dataOffset = FlashFile.Page + indexBytes;

        var header = new byte[FlashFile.Page];
        var h = header.AsSpan();
        BinaryPrimitives.WriteUInt64LittleEndian(h, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], Version);
        BinaryPrimitives.WriteInt64LittleEndian(h[16..], model.Length);
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], c.Layers);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], c.Embedding);
        BinaryPrimitives.WriteInt32LittleEndian(h[32..], c.FeedForward);
        BinaryPrimitives.WriteInt32LittleEndian(h[36..], entries.Count);
        BinaryPrimitives.WriteInt64LittleEndian(h[40..], FlashFile.Page);
        BinaryPrimitives.WriteInt64LittleEndian(h[48..], dataOffset);
        BinaryPrimitives.WriteInt64LittleEndian(h[56..], at);
        var index = new byte[indexBytes];
        for (var i = 0; i < entries.Count; i++) WriteEntry(index.AsSpan(i * EntryBytes, EntryBytes), entries[i]);

        var partial = path + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var most = entries.Max(e => FlashFile.Pages(e.WeightsBytes) + FlashFile.Pages(e.ScaleCount * sizeof(float)));
        var buffer = FlashFile.Allocate(most);
        var window = FlashFile.Allocate(entries.Max(SourceSpan));
        try
        {
            using var source = FlashFile.Open(model.FilePath);
            using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                stream.Write(header);
                stream.Write(index);
                foreach (var e in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var rows = ReadTensor(source, e, window, channels, readBytes);
                    var weightsLength = (int)FlashFile.Pages(e.WeightsBytes);
                    var output = new Span<byte>(buffer, weightsLength + (int)FlashFile.Pages(e.ScaleCount * sizeof(float)));
                    output.Clear();
                    Ptq1PairMatrix.Repack(rows, e.Rows, e.Columns, output[..weightsLength],
                        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(output[weightsLength..]), RepackDegree);
                    var place = stream.Position;
                    stream.Write(output);
                    // ON FLASH AND OUT OF RAM AS IT GOES. Written through the cache, a store bigger than the phone's RAM
                    // fills it with pages the phone must write back and evict while it reads the next tensor.
                    FlashFile.Settle(stream, place, output.Length);
                }
            }
            File.Move(partial, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        finally
        {
            FlashFile.Free(buffer);
            FlashFile.Free(window);
        }

        // On flash now, and only there.
        using (var written = FlashFile.Open(path, direct: false)) written.Drop(0, written.Length);
        return Open(model, path) ?? throw new InvalidDataException($"{path} was written and does not read back as {model.FilePath}'s feed-forward.");
    }

    /// <summary>The store at <paramref name="path"/> when it was cut from <paramref name="model"/>; null when there is none, or it is another model's.</summary>
    public static FfnStore? Open(GgufFile model, string path)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!File.Exists(path) || Unreadable(model) is not null) return null;
        var c = Qwen35Config.FromGguf(model);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 12);
            var header = new byte[FlashFile.Page];
            stream.ReadExactly(header);
            var h = header.AsSpan();
            if (BinaryPrimitives.ReadUInt64LittleEndian(h) != Magic || BinaryPrimitives.ReadInt32LittleEndian(h[8..]) != Version
                || BinaryPrimitives.ReadInt64LittleEndian(h[16..]) != model.Length || BinaryPrimitives.ReadInt32LittleEndian(h[24..]) != c.Layers
                || BinaryPrimitives.ReadInt32LittleEndian(h[28..]) != c.Embedding || BinaryPrimitives.ReadInt32LittleEndian(h[32..]) != c.FeedForward)
                return null;
            var count = BinaryPrimitives.ReadInt32LittleEndian(h[36..]);
            var indexOffset = BinaryPrimitives.ReadInt64LittleEndian(h[40..]);
            var dataOffset = BinaryPrimitives.ReadInt64LittleEndian(h[48..]);
            var dataBytes = BinaryPrimitives.ReadInt64LittleEndian(h[56..]);
            if (count != c.Layers * Kinds.Length || dataOffset + dataBytes > stream.Length) return null;

            var index = new byte[(long)count * EntryBytes];
            stream.Position = indexOffset;
            stream.ReadExactly(index);
            var entries = new List<Entry>(count);
            for (var i = 0; i < count; i++)
            {
                var e = ReadEntry(index.AsSpan(i * EntryBytes, EntryBytes));
                var tensor = model.Tensor($"blk.{e.Layer}.{Kinds[e.Kind]}.weight");
                if (tensor is null || tensor.Offset != e.GgufOffset || tensor.ByteCount != e.GgufBytes || tensor.Rows != e.Rows || tensor.RowLength != e.Columns)
                    return null;
                entries.Add(e);
            }
            return new FfnStore(path, c.Layers, dataOffset, dataBytes, entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// How many of the store's tensors differ from <paramref name="model"/>'s, repacked again; 0 for a faithful store.
    /// Reads every tensor and lets each go after.
    /// </summary>
    public unsafe int Verify(GgufFile model, int channels = 4, int readBytes = 1 << 20)
    {
        ArgumentNullException.ThrowIfNull(model);
        var wrong = 0;
        using var file = FlashFile.Open(FilePath);
        using var source = FlashFile.Open(model.FilePath);
        var most = Entries.Max(e => FlashFile.Pages(e.WeightsBytes) + FlashFile.Pages(e.ScaleCount * sizeof(float)));
        var read = FlashFile.Allocate(most);
        var expected = FlashFile.Allocate(most);
        var window = FlashFile.Allocate(Entries.Max(SourceSpan));
        try
        {
            foreach (var e in Entries)
            {
                var length = (int)(e.ScalesOffset - e.WeightsOffset + FlashFile.Pages(e.ScaleCount * sizeof(float)));
                var got = file.ReadChannels(read, length, DataOffset + e.WeightsOffset, channels, readBytes);
                var rows = ReadTensor(source, e, window, channels, readBytes);
                var weightsLength = (int)FlashFile.Pages(e.WeightsBytes);
                var again = new Span<byte>(expected, length);
                again.Clear();
                Ptq1PairMatrix.Repack(rows, e.Rows, e.Columns, again[..weightsLength],
                    System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(again[weightsLength..]), RepackDegree);
                if (got < length || !new ReadOnlySpan<byte>(read, length).SequenceEqual(again)) wrong++;
            }
        }
        finally
        {
            FlashFile.Free(read);
            FlashFile.Free(expected);
            FlashFile.Free(window);
        }
        return wrong;
    }

    // Threads a repack is shared over: half the cores, the rest left to the phone.
    private static int RepackDegree => Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    // The page-aligned span of the model's file that holds a tensor's rows.
    private static long SourceSpan(Entry e) => FlashFile.Pages(e.GgufOffset + e.GgufBytes) - (e.GgufOffset & ~(long)(FlashFile.Page - 1));

    // A tensor's rows, read from the model's file into window past the cache: the page-aligned span that holds them, in
    // the device's reads on its channels. The rows are where they fall in it.
    private static unsafe ReadOnlySpan<byte> ReadTensor(FlashFile source, Entry e, byte* window, int channels, int readBytes)
    {
        var start = e.GgufOffset & ~(long)(FlashFile.Page - 1);
        var got = source.ReadChannels(window, SourceSpan(e), start, channels, readBytes);
        var skip = e.GgufOffset - start;
        if (got < skip + e.GgufBytes) throw new InvalidDataException($"{source.FilePath} ends inside blk.{e.Layer}.{Kinds[e.Kind]}.weight.");
        return new ReadOnlySpan<byte>(window + skip, checked((int)e.GgufBytes));
    }

    /// <summary>
    /// Streams the whole feed-forward in from flash on <paramref name="channels"/> readers at once, in reads of
    /// <paramref name="readBytes"/>, into one buffer: warm until disposed.
    /// </summary>
    public unsafe FfnWarm Load(int channels, int readBytes = 1024 * 1024)
    {
        var started = Stopwatch.GetTimestamp();
        var size = (int)Math.Max(FlashFile.Page, FlashFile.Pages(readBytes));
        var buffer = FlashFile.Allocate(DataBytes);
        var file = FlashFile.Open(FilePath);
        try
        {
            var reads = (DataBytes + size - 1) / size;
            long next = -1;
            Exception? failed = null;
            void Read()
            {
                try
                {
                    long i;
                    while ((i = Interlocked.Increment(ref next)) < reads && Volatile.Read(ref failed) is null)
                    {
                        var at = i * size;
                        var length = (int)Math.Min(size, DataBytes - at);
                        if (file.Read(buffer + at, length, DataOffset + at) < length) throw new InvalidDataException($"{FilePath} ends inside its data.");
                        file.Drop(DataOffset + at, length);
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref failed, ex, null);
                }
            }

            var readers = (int)Math.Clamp(channels, 1, Math.Max(1, reads));
            var threads = new Thread[readers - 1];
            for (var t = 0; t < threads.Length; t++)
            {
                threads[t] = new Thread(Read) { IsBackground = true, Name = $"ffn-channel-{t + 1}" };
                threads[t].Start();
            }
            Read();
            foreach (var thread in threads) thread.Join();
            if (failed is not null) throw failed;

            var views = new Ptq1PairMatrix[Layers, Kinds.Length];
            foreach (var e in Entries)
                views[e.Layer, e.Kind] = Ptq1PairMatrix.Over(buffer + e.WeightsOffset, (float*)(buffer + e.ScalesOffset), e.Rows, e.Columns);
            return new FfnWarm(buffer, DataBytes, views, readers, file.Direct, Stopwatch.GetElapsedTime(started));
        }
        catch
        {
            FlashFile.Free(buffer);
            throw;
        }
        finally
        {
            file.Dispose();
        }
    }

    private static void WriteEntry(Span<byte> at, Entry e)
    {
        BinaryPrimitives.WriteInt32LittleEndian(at, e.Layer);
        BinaryPrimitives.WriteInt32LittleEndian(at[4..], e.Kind);
        BinaryPrimitives.WriteInt32LittleEndian(at[8..], e.Rows);
        BinaryPrimitives.WriteInt32LittleEndian(at[12..], e.Columns);
        BinaryPrimitives.WriteInt64LittleEndian(at[16..], e.GgufOffset);
        BinaryPrimitives.WriteInt64LittleEndian(at[24..], e.GgufBytes);
        BinaryPrimitives.WriteInt64LittleEndian(at[32..], e.WeightsOffset);
        BinaryPrimitives.WriteInt64LittleEndian(at[40..], e.WeightsBytes);
        BinaryPrimitives.WriteInt64LittleEndian(at[48..], e.ScalesOffset);
        BinaryPrimitives.WriteInt64LittleEndian(at[56..], e.ScaleCount);
    }

    private static Entry ReadEntry(ReadOnlySpan<byte> at) => new(
        BinaryPrimitives.ReadInt32LittleEndian(at), BinaryPrimitives.ReadInt32LittleEndian(at[4..]),
        BinaryPrimitives.ReadInt32LittleEndian(at[8..]), BinaryPrimitives.ReadInt32LittleEndian(at[12..]),
        BinaryPrimitives.ReadInt64LittleEndian(at[16..]), BinaryPrimitives.ReadInt64LittleEndian(at[24..]),
        BinaryPrimitives.ReadInt64LittleEndian(at[32..]), BinaryPrimitives.ReadInt64LittleEndian(at[40..]),
        BinaryPrimitives.ReadInt64LittleEndian(at[48..]), BinaryPrimitives.ReadInt64LittleEndian(at[56..]));
}

/// <summary>
/// A model's feed-forward streamed in from its <see cref="FfnStore"/>: the layers read it where it lies while it is
/// armed. The model lets go of the views before this is disposed; RAM holds none of it after.
/// </summary>
public sealed unsafe class FfnWarm : IDisposable
{
    private readonly Ptq1PairMatrix[,] _views;
    private byte* _buffer;

    internal FfnWarm(byte* buffer, long bytes, Ptq1PairMatrix[,] views, int channels, bool direct, TimeSpan elapsed)
    {
        _buffer = buffer;
        _views = views;
        Bytes = bytes;
        Channels = channels;
        Direct = direct;
        Elapsed = elapsed;
    }

    /// <summary>Bytes it holds in RAM.</summary>
    public long Bytes { get; }

    /// <summary>Readers it was streamed in on.</summary>
    public int Channels { get; }

    /// <summary>Whether the reads went past the OS's cache.</summary>
    public bool Direct { get; }

    /// <summary>How long it took to stream in.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>Layer <paramref name="layer"/>'s gate (0), up (1) or down (2), read where it lies.</summary>
    public Ptq1PairMatrix View(int layer, int kind)
    {
        ObjectDisposedException.ThrowIf(_buffer == null, this);
        return _views[layer, kind];
    }

    /// <summary>Lets the feed-forward go: RAM holds none of it after this.</summary>
    public void Dispose()
    {
        var buffer = _buffer;
        _buffer = null;
        FlashFile.Free(buffer);
    }
}
