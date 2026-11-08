// TileStore.cs
//
// A MODEL'S LAYERS ON FLASH, IN THE SHAPE THE VECTOR KERNEL COMPUTES ON. A set of every layer's ternary weights - the
// feed-forward's gate, up and down (FfnStore), or the mixing's projections (MixStore) - repacked once into
// Ptq1PairMatrix's tiles and written to a file beside the head's store. A model that reads them from here keeps no copy
// of them in RAM: a turn brings them in on the device's channels - all at once where they fit (FfnWarm), a layer ahead of
// the one computing where they do not (LayerStream) - and the device's recipe decides when an idle engine lets them go.
//
// Whole matrices, so a layer read from here computes exactly what the copy did: the same tiles, the same kernel.
//
// Layout, little-endian:
//   page 0   header: the set's magic and version, the model it was cut from (its length), layers, embedding and
//            feed-forward widths, tensor count, where the index and the data are, the data's length
//   index    per tensor (layer by layer, in the set's order): layer, kind, rows, columns, its GGUF offset and bytes,
//            where its tiles and scales are in the data, and their lengths
//   data     each tensor's tiles, then its scales, each whole pages - a layer's tensors one after another
//
// Written to a ".partial" file and renamed when whole. The feed-forward's set is the layout FfnStore wrote before the
// mixing had one: its stores read as they were.

using System.Buffers.Binary;
using System.Diagnostics;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Kernels;
using CircleAI.Inference.Managed.Model;

namespace CircleAI.Inference.Managed.Store;

/// <summary>Which of a layer's weights a <see cref="TileStore"/> holds.</summary>
/// <param name="Name">What the set is called in the log.</param>
/// <param name="Magic">The file's first eight bytes.</param>
/// <param name="Version">The layout's version.</param>
/// <param name="Kinds">The tensors, by the part of their name after the layer: "ffn_gate" for blk.N.ffn_gate.weight.</param>
/// <param name="EveryLayerHasAll">Whether every layer has every kind (the feed-forward's), or each its own (the mixing's:
/// a DeltaNet layer's qkv, gate and out; a full-attention layer's q, k, v and output).</param>
public sealed record TileSet(string Name, ulong Magic, int Version, IReadOnlyList<string> Kinds, bool EveryLayerHasAll)
{
    /// <summary>Every layer's gate, up and down: "CAIFFN01".</summary>
    public static TileSet FeedForward { get; } = new("feed-forward", 0x3130_4E46_4649_4143, 1, ["ffn_gate", "ffn_up", "ffn_down"], true);

    /// <summary>Every layer's mixing: a DeltaNet layer's qkv, gate and out, a full-attention layer's q, k, v and output: "CAIMIX01".</summary>
    public static TileSet Mixing { get; } = new("mixing", 0x3130_5849_4D49_4143, 1,
        ["attn_qkv", "attn_gate", "ssm_out", "attn_q", "attn_k", "attn_v", "attn_output"], false);
}

/// <summary>A set of a model's layer weights (<see cref="TileSet"/>) in the vector kernel's tiles, in a file on flash.</summary>
public sealed class TileStore
{
    private const int EntryBytes = 64;

    /// <summary>One tensor in the store: what it is, and where its tiles and scales are in the data.</summary>
    public readonly record struct Entry(int Layer, int Kind, int Rows, int Columns, long GgufOffset, long GgufBytes,
        long WeightsOffset, long WeightsBytes, long ScalesOffset, long ScaleCount)
    {
        /// <summary>Where the tensor ends in the data: its scales' last whole page.</summary>
        public long End => ScalesOffset + FlashFile.Pages(ScaleCount * sizeof(float));
    }

    private readonly int[] _firstOfLayer; // index of each layer's first entry; one past the end last

    private TileStore(string path, TileSet set, int layers, long dataOffset, long dataBytes, IReadOnlyList<Entry> entries)
    {
        FilePath = path;
        Set = set;
        Layers = layers;
        DataOffset = dataOffset;
        DataBytes = dataBytes;
        Entries = entries;
        _firstOfLayer = new int[layers + 1];
        var at = 0;
        for (var il = 0; il < layers; il++)
        {
            _firstOfLayer[il] = at;
            while (at < entries.Count && entries[at].Layer == il) at++;
        }
        _firstOfLayer[layers] = at;
    }

    /// <summary>The file.</summary>
    public string FilePath { get; }

    /// <summary>Which of the layers' weights it holds.</summary>
    public TileSet Set { get; }

    /// <summary>Layers held: every one of the model's.</summary>
    public int Layers { get; }

    /// <summary>Where the data starts in the file.</summary>
    public long DataOffset { get; }

    /// <summary>The data's length: what holding all of it in RAM takes.</summary>
    public long DataBytes { get; }

    /// <summary>Every tensor, layer by layer, in the set's order.</summary>
    public IReadOnlyList<Entry> Entries { get; }

    /// <summary>Layer <paramref name="layer"/>'s tensors.</summary>
    public IEnumerable<Entry> LayerEntries(int layer)
    {
        for (var i = _firstOfLayer[layer]; i < _firstOfLayer[layer + 1]; i++) yield return Entries[i];
    }

    /// <summary>Where layer <paramref name="layer"/>'s tensors lie in the data, one after another: their start and length.</summary>
    public (long Start, long Length) LayerRange(int layer)
    {
        var first = _firstOfLayer[layer];
        var end = _firstOfLayer[layer + 1];
        if (first == end) return (0, 0);
        return (Entries[first].WeightsOffset, Entries[end - 1].End - Entries[first].WeightsOffset);
    }

    /// <summary>Why <paramref name="model"/>'s <paramref name="set"/> cannot be stored this way; null when it can.</summary>
    public static string? Unreadable(GgufFile model, TileSet set)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(set);
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
            var held = 0;
            foreach (var kind in set.Kinds)
            {
                var tensor = model.Tensor($"blk.{il}.{kind}.weight");
                if (tensor is null)
                {
                    if (set.EveryLayerHasAll) return $"the model has no blk.{il}.{kind}.weight";
                    continue;
                }
                if (tensor.Type != GgmlType.PTQ1_0) return $"blk.{il}.{kind}.weight is {tensor.Type}; the vector kernel's tiles are PTQ1_0's";
                if (tensor.Rows % Ptq1PairMatrix.TileRows != 0 || tensor.RowLength % Ptq1.BlockWeights != 0) return $"blk.{il}.{kind}.weight is not whole tiles";
                held++;
            }
            if (held == 0) return $"layer {il} has none of the {set.Name}'s weights";
        }
        return null;
    }

    /// <summary>
    /// Writes the store of <paramref name="model"/>'s <paramref name="set"/> to <paramref name="path"/>; returns it, opened.
    /// Each tensor is read from the model's file past the cache, in reads of <paramref name="readBytes"/> on
    /// <paramref name="channels"/> readers (the device's recipe), repacked on the cores, and written and let go before the next.
    /// </summary>
    public static unsafe TileStore Write(GgufFile model, string path, TileSet set, CancellationToken ct = default, int channels = 4, int readBytes = 1 << 20)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (Unreadable(model, set) is { } why) throw new NotSupportedException(why);
        var c = Qwen35Config.FromGguf(model);

        // The index first: every tensor's place.
        var entries = new List<Entry>(c.Layers * set.Kinds.Count);
        long at = 0;
        for (var il = 0; il < c.Layers; il++)
        {
            for (var k = 0; k < set.Kinds.Count; k++)
            {
                var tensor = model.Tensor($"blk.{il}.{set.Kinds[k]}.weight");
                if (tensor is null) continue;
                var rows = checked((int)tensor.Rows);
                var cols = checked((int)tensor.RowLength);
                var weights = Ptq1PairMatrix.WeightBytes(rows, cols);
                var scales = Ptq1PairMatrix.ScaleCount(rows, cols);
                var entry = new Entry(il, k, rows, cols, tensor.Offset, tensor.ByteCount, at, weights, at + FlashFile.Pages(weights), scales);
                entries.Add(entry);
                at = entry.End;
            }
        }
        var indexBytes = FlashFile.Pages((long)entries.Count * EntryBytes);
        var dataOffset = FlashFile.Page + indexBytes;

        var header = new byte[FlashFile.Page];
        var h = header.AsSpan();
        BinaryPrimitives.WriteUInt64LittleEndian(h, set.Magic);
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], set.Version);
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
        var most = entries.Max(e => e.End - e.WeightsOffset);
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
                    var rows = ReadTensor(source, set, e, window, channels, readBytes);
                    var weightsLength = (int)FlashFile.Pages(e.WeightsBytes);
                    var output = new Span<byte>(buffer, (int)(e.End - e.WeightsOffset));
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
        return Open(model, path, set) ?? throw new InvalidDataException($"{path} was written and does not read back as {model.FilePath}'s {set.Name}.");
    }

    /// <summary>
    /// The store of <paramref name="set"/> at <paramref name="path"/> when it was cut from <paramref name="model"/>; null when
    /// there is none, or it is another model's or another set's.
    /// </summary>
    public static TileStore? Open(GgufFile model, string path, TileSet set)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(set);
        if (!File.Exists(path) || Unreadable(model, set) is not null) return null;
        var c = Qwen35Config.FromGguf(model);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 12);
            var header = new byte[FlashFile.Page];
            stream.ReadExactly(header);
            var h = header.AsSpan();
            if (BinaryPrimitives.ReadUInt64LittleEndian(h) != set.Magic || BinaryPrimitives.ReadInt32LittleEndian(h[8..]) != set.Version
                || BinaryPrimitives.ReadInt64LittleEndian(h[16..]) != model.Length || BinaryPrimitives.ReadInt32LittleEndian(h[24..]) != c.Layers
                || BinaryPrimitives.ReadInt32LittleEndian(h[28..]) != c.Embedding || BinaryPrimitives.ReadInt32LittleEndian(h[32..]) != c.FeedForward)
                return null;
            var count = BinaryPrimitives.ReadInt32LittleEndian(h[36..]);
            var indexOffset = BinaryPrimitives.ReadInt64LittleEndian(h[40..]);
            var dataOffset = BinaryPrimitives.ReadInt64LittleEndian(h[48..]);
            var dataBytes = BinaryPrimitives.ReadInt64LittleEndian(h[56..]);
            if (count != Held(model, c, set) || dataOffset + dataBytes > stream.Length) return null;

            var index = new byte[(long)count * EntryBytes];
            stream.Position = indexOffset;
            stream.ReadExactly(index);
            var entries = new List<Entry>(count);
            for (var i = 0; i < count; i++)
            {
                var e = ReadEntry(index.AsSpan(i * EntryBytes, EntryBytes));
                if ((uint)e.Kind >= (uint)set.Kinds.Count) return null;
                if (entries.Count > 0 && (e.Layer < entries[^1].Layer || (e.Layer == entries[^1].Layer && e.Kind <= entries[^1].Kind))) return null;
                var tensor = model.Tensor($"blk.{e.Layer}.{set.Kinds[e.Kind]}.weight");
                if (tensor is null || tensor.Offset != e.GgufOffset || tensor.ByteCount != e.GgufBytes || tensor.Rows != e.Rows || tensor.RowLength != e.Columns)
                    return null;
                entries.Add(e);
            }
            return new TileStore(path, set, c.Layers, dataOffset, dataBytes, entries);
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
        var most = Entries.Max(e => e.End - e.WeightsOffset);
        var read = FlashFile.Allocate(most);
        var expected = FlashFile.Allocate(most);
        var window = FlashFile.Allocate(Entries.Max(SourceSpan));
        try
        {
            foreach (var e in Entries)
            {
                var length = (int)(e.End - e.WeightsOffset);
                var got = file.ReadChannels(read, length, DataOffset + e.WeightsOffset, channels, readBytes);
                var rows = ReadTensor(source, Set, e, window, channels, readBytes);
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

    /// <summary>
    /// Streams the whole set in from flash on <paramref name="channels"/> readers at once, in reads of
    /// <paramref name="readBytes"/>, into one buffer: warm until disposed.
    /// </summary>
    public unsafe FfnWarm Load(int channels, int readBytes = 1024 * 1024)
    {
        var started = Stopwatch.GetTimestamp();
        var buffer = FlashFile.Allocate(DataBytes);
        try
        {
            using var file = FlashFile.Open(FilePath);
            if (file.ReadChannels(buffer, DataBytes, DataOffset, channels, readBytes) < DataBytes)
                throw new InvalidDataException($"{FilePath} ends inside its data.");
            var views = new Ptq1PairMatrix?[Layers, Set.Kinds.Count];
            foreach (var e in Entries)
                views[e.Layer, e.Kind] = Ptq1PairMatrix.Over(buffer + e.WeightsOffset, (float*)(buffer + e.ScalesOffset), e.Rows, e.Columns);
            var readers = (int)Math.Clamp(channels, 1, Math.Max(1, (DataBytes + readBytes - 1) / Math.Max(1, readBytes)));
            return new FfnWarm(buffer, DataBytes, views, readers, file.Direct, Stopwatch.GetElapsedTime(started));
        }
        catch
        {
            FlashFile.Free(buffer);
            throw;
        }
    }

    // Tensors of `set` the model has, every layer counted.
    private static int Held(GgufFile model, Qwen35Config c, TileSet set)
    {
        var held = 0;
        for (var il = 0; il < c.Layers; il++)
            foreach (var kind in set.Kinds)
                if (model.Tensor($"blk.{il}.{kind}.weight") is not null) held++;
        return held;
    }

    // Threads a repack is shared over: half the cores, the rest left to the phone.
    private static int RepackDegree => Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    // The page-aligned span of the model's file that holds a tensor's rows.
    private static long SourceSpan(Entry e) => FlashFile.Pages(e.GgufOffset + e.GgufBytes) - (e.GgufOffset & ~(long)(FlashFile.Page - 1));

    // A tensor's rows, read from the model's file into window past the cache: the page-aligned span that holds them, in
    // the device's reads on its channels. The rows are where they fall in it.
    private static unsafe ReadOnlySpan<byte> ReadTensor(FlashFile source, TileSet set, Entry e, byte* window, int channels, int readBytes)
    {
        var start = e.GgufOffset & ~(long)(FlashFile.Page - 1);
        var got = source.ReadChannels(window, SourceSpan(e), start, channels, readBytes);
        var skip = e.GgufOffset - start;
        if (got < skip + e.GgufBytes) throw new InvalidDataException($"{source.FilePath} ends inside blk.{e.Layer}.{set.Kinds[e.Kind]}.weight.");
        return new ReadOnlySpan<byte>(window + skip, checked((int)e.GgufBytes));
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

/// <summary>Every layer's feed-forward on flash (<see cref="TileSet.FeedForward"/>): the store the split writes first.</summary>
public static class FfnStore
{
    /// <summary>"CAIFFN01", little-endian.</summary>
    public static ulong Magic => TileSet.FeedForward.Magic;

    /// <summary>Why <paramref name="model"/>'s feed-forward cannot be stored this way; null when it can.</summary>
    public static string? Unreadable(GgufFile model) => TileStore.Unreadable(model, TileSet.FeedForward);

    /// <summary>Writes the store of <paramref name="model"/>'s feed-forward (<see cref="TileStore.Write"/>).</summary>
    public static TileStore Write(GgufFile model, string path, CancellationToken ct = default, int channels = 4, int readBytes = 1 << 20) =>
        TileStore.Write(model, path, TileSet.FeedForward, ct, channels, readBytes);

    /// <summary>The feed-forward's store at <paramref name="path"/> when it was cut from <paramref name="model"/>; else null.</summary>
    public static TileStore? Open(GgufFile model, string path) => TileStore.Open(model, path, TileSet.FeedForward);
}

/// <summary>Every layer's mixing on flash (<see cref="TileSet.Mixing"/>): with the feed-forward's, the whole of every layer.</summary>
public static class MixStore
{
    /// <summary>Why <paramref name="model"/>'s mixing cannot be stored this way; null when it can.</summary>
    public static string? Unreadable(GgufFile model) => TileStore.Unreadable(model, TileSet.Mixing);

    /// <summary>Writes the store of <paramref name="model"/>'s mixing (<see cref="TileStore.Write"/>).</summary>
    public static TileStore Write(GgufFile model, string path, CancellationToken ct = default, int channels = 4, int readBytes = 1 << 20) =>
        TileStore.Write(model, path, TileSet.Mixing, ct, channels, readBytes);

    /// <summary>The mixing's store at <paramref name="path"/> when it was cut from <paramref name="model"/>; else null.</summary>
    public static TileStore? Open(GgufFile model, string path) => TileStore.Open(model, path, TileSet.Mixing);
}

/// <summary>
/// A set of a model's layers streamed in whole from its <see cref="TileStore"/>: the layers read it where it lies while it is
/// armed. The model lets go of the views before this is disposed; RAM holds none of it after.
/// </summary>
public sealed unsafe class FfnWarm : IDisposable
{
    private readonly Ptq1PairMatrix?[,] _views;
    private byte* _buffer;

    internal FfnWarm(byte* buffer, long bytes, Ptq1PairMatrix?[,] views, int channels, bool direct, TimeSpan elapsed)
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

    /// <summary>Layer <paramref name="layer"/>'s tensor of kind <paramref name="kind"/> (the feed-forward's gate 0, up 1, down 2), read where it lies.</summary>
    public Ptq1PairMatrix View(int layer, int kind)
    {
        ObjectDisposedException.ThrowIf(_buffer == null, this);
        return _views[layer, kind] ?? throw new InvalidOperationException($"Layer {layer} holds no tensor of kind {kind}.");
    }

    /// <summary>Lets it go: RAM holds none of it after this.</summary>
    public void Dispose()
    {
        var buffer = _buffer;
        _buffer = null;
        FlashFile.Free(buffer);
    }
}
