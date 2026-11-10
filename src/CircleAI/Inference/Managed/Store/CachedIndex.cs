// CachedIndex.cs
//
// THE CACHED INDEX ON FLASH, AND THE WARM INDEX IN RAM (the indexed mind's three tiers). The core index (ThoughtIndex)
// names, for each layer's thought group, the neurons its words need. This file holds a portion of the core: those
// neurons' pieces - their ffn_gate and ffn_up rows - side by side in the vector kernel's tiles, one piece a group, read in
// one read where a word wants it; the groups the most words fell in first, until the flash budget is spent. The warm index
// (IndexedFeedForward) keeps the hottest pieces in RAM, up to the device's budget. A word's piece comes from RAM, else the
// cached index, else the core - the model's own file, the group's rows gathered from it and tiled.
//
// Layout, little-endian:
//   header   magic "CAICACHE", version, the model's length, layers, groups, neurons a list, embedding, entries, data start
//   entries  per cached piece: layer, group, where it starts in the file, its bytes
//   pieces   page-aligned: the gate's tiles, its scales, the up's tiles, its scales

using System.Buffers.Binary;
using System.Diagnostics;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Kernels;
using CircleAI.Inference.Managed.Model;

namespace CircleAI.Inference.Managed.Store;

/// <summary>A group's piece: its neurons, and their gate, up and down rows (the whole knowledge layer, by neuron).</summary>
public sealed class IndexPiece(ushort[] neurons, Ptq1PairMatrix gate, Ptq1PairMatrix up, DownPiece down)
{
    /// <summary>The neurons, sorted: row i of <see cref="Gate"/>, <see cref="Up"/> and <see cref="Down"/> is neuron Neurons[i].</summary>
    public ushort[] Neurons { get; } = neurons;

    /// <summary>The neurons' ffn_gate rows.</summary>
    public Ptq1PairMatrix Gate { get; } = gate;

    /// <summary>The neurons' ffn_up rows.</summary>
    public Ptq1PairMatrix Up { get; } = up;

    /// <summary>The neurons' ffn_down columns, by neuron (the model keeps ffn_down by row; the index keeps it by neuron).</summary>
    public DownPiece Down { get; } = down;
}

/// <summary>
/// A group's ffn_down, by neuron: for each of the group's neurons, its column of ffn_down as a ternary row. The model
/// stores ffn_down by output row, where a neuron is a column spread across every row; the index re-quantises each
/// neuron's column into a row of its own, so a word's down projection sums only the group's neurons.
/// </summary>
public sealed unsafe class DownPiece
{
    private readonly byte* _rows;
    private readonly int _neurons, _embedding, _rowBytes, _blocks;

    internal DownPiece(byte* rows, int neurons, int embedding)
    {
        _rows = rows;
        _neurons = neurons;
        _embedding = embedding;
        _blocks = embedding / Ptq1.BlockWeights;
        _rowBytes = _blocks * Ptq1.BlockBytes;
    }

    /// <summary>
    /// The feed-forward's down projection for a word, over the group's neurons only:
    /// <paramref name="output"/>[e] = sum over the group of <paramref name="activations"/>[neuron] times that neuron's
    /// ffn_down column. <paramref name="neurons"/> are the piece's neurons, in its order.
    /// </summary>
    public void Project(ReadOnlySpan<float> activations, ReadOnlySpan<ushort> neurons, Span<float> output)
    {
        output[.._embedding].Clear();
        Span<sbyte> trits = stackalloc sbyte[Ptq1.BlockWeights];
        for (var i = 0; i < _neurons; i++)
        {
            var a = activations[neurons[i]];
            if (a == 0) continue;
            var row = _rows + (long)i * _rowBytes;
            for (var b = 0; b < _blocks; b++)
            {
                var block = new ReadOnlySpan<byte>(row + b * Ptq1.BlockBytes, Ptq1.BlockBytes);
                var scale = Ptq1.Scale(block);
                Ptq1.DecodeScalar(block, trits);
                var at = b * Ptq1.BlockWeights;
                for (var j = 0; j < Ptq1.BlockWeights; j++) output[at + j] += a * trits[j] * scale;
            }
        }
    }
}

/// <summary>The cached index: a portion of the core index's pieces, side by side on flash (<see cref="CachedIndex"/>'s layout).</summary>
public sealed class CachedIndex
{
    /// <summary>"CAICACHE", little-endian.</summary>
    public const ulong Magic = 0x4548_4341_4349_4143;

    /// <summary>The layout's version.</summary>
    public const int Version = 1;

    private const int EntryBytes = 24;
    private readonly Dictionary<long, (long Offset, long Bytes)> _entries;

    private CachedIndex(string path, int groups, long pieceStride, IReadOnlyDictionary<long, (long Offset, long Bytes)> entries)
    {
        FilePath = path;
        Groups = groups;
        PieceStride = pieceStride;
        _entries = new Dictionary<long, (long Offset, long Bytes)>(entries);
    }

    /// <summary>The file.</summary>
    public string FilePath { get; }

    /// <summary>Thought groups a layer.</summary>
    public int Groups { get; }

    /// <summary>The bytes a stored piece takes, the same for every one - a whole number of the profile's read chunks, so a
    /// scattered read runs at nearly full flash speed.</summary>
    public long PieceStride { get; }

    /// <summary>Pieces it holds.</summary>
    public int Count => _entries.Count;

    /// <summary>Where layer <paramref name="layer"/>'s group <paramref name="group"/>'s piece lies; null when the cache does not hold it.</summary>
    public (long Offset, long Bytes)? Entry(int layer, int group) =>
        _entries.TryGetValue(Key(layer, group, Groups), out var e) ? e : null;

    internal static long Key(int layer, int group, int groups) => (long)layer * groups + group;

    /// <summary>Bytes a piece of <paramref name="neurons"/> neurons of <paramref name="embedding"/> takes: gate and up as the
    /// kernel's tiles and scales, then down as one ternary row a neuron (the whole knowledge layer, by neuron).</summary>
    public static long PieceBytes(int neurons, int embedding) =>
        2 * (Ptq1PairMatrix.WeightBytes(neurons, embedding) + Ptq1PairMatrix.ScaleCount(neurons, embedding) * sizeof(float))
        + (long)neurons * (embedding / Ptq1.BlockWeights * Ptq1.BlockBytes);

    /// <summary>
    /// Writes the cached index of <paramref name="core"/> for <paramref name="model"/> to <paramref name="path"/>: the pieces of
    /// the groups the most words fell in, until <paramref name="budget"/> bytes; each layer's gate and up read once from the
    /// model's file past the cache, the groups' rows gathered and tiled. Returns it, opened.
    /// </summary>
    public static unsafe CachedIndex Build(ThoughtIndex core, GgufFile model, string path, long budget, int channels = 4, int readBytes = 1 << 20, IReadOnlyList<long>? usage = null)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(model);
        // The stored piece is a whole number of the profile's read chunks, so a scattered read of it runs at nearly full
        // flash speed - no piece smaller than the device reads well. With a page-sized chunk this is just the page-aligned piece.
        var chunk = FlashFile.Pages(Math.Max(FlashFile.Page, readBytes));
        var pieceBytes = (PieceBytes(core.ListNeurons, core.Embedding) + chunk - 1) / chunk * chunk;

        // THE PORTION: the groups the day used most first (REVISE, when usage is given), then the groups the most of Watch's
        // words fell in; while the budget lasts; then laid out layer by layer.
        var chosen = new List<(int Layer, int Group)>();
        long spent = 0;
        var ordered = Enumerable.Range(0, core.Layers).SelectMany(il => Enumerable.Range(0, core.Groups).Select(g => (Layer: il, Group: g)))
            .Where(p => core.Size(p.Layer, p.Group) > 0)
            .OrderByDescending(p => usage is null ? 0L : usage[(int)Key(p.Layer, p.Group, core.Groups)])
            .ThenByDescending(p => core.Size(p.Layer, p.Group));
        foreach (var (layer, group) in ordered)
        {
            if (spent + pieceBytes > budget) break;
            chosen.Add((layer, group));
            spent += pieceBytes;
        }
        chosen.Sort();

        var dataStart = FlashFile.Pages(64 + (long)chosen.Count * EntryBytes);
        var header = new byte[dataStart];
        BinaryPrimitives.WriteUInt64LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), Version);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(12), model.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), core.Layers);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), core.Groups);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), core.ListNeurons);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32), core.Embedding);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(36), chosen.Count);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(40), dataStart);
        var entries = new Dictionary<long, (long Offset, long Bytes)>();
        for (var i = 0; i < chosen.Count; i++)
        {
            var offset = dataStart + i * pieceBytes;
            var at = header.AsSpan(64 + i * EntryBytes, EntryBytes);
            BinaryPrimitives.WriteInt32LittleEndian(at, chosen[i].Layer);
            BinaryPrimitives.WriteInt32LittleEndian(at[4..], chosen[i].Group);
            BinaryPrimitives.WriteInt64LittleEndian(at[8..], offset);
            BinaryPrimitives.WriteInt64LittleEndian(at[16..], pieceBytes);
            entries[Key(chosen[i].Layer, chosen[i].Group, core.Groups)] = (offset, pieceBytes);
        }

        var partial = path + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var gather = new RowGather(model, core.ListNeurons, core.Embedding, channels, readBytes);
        var piece = FlashFile.Allocate(pieceBytes);
        try
        {
            using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                stream.Write(header);
                var loaded = -1;
                foreach (var (layer, group) in chosen)
                {
                    if (layer != loaded)
                    {
                        gather.Load(layer);
                        loaded = layer;
                    }
                    var span = new Span<byte>(piece, (int)pieceBytes);
                    span.Clear();
                    gather.Tile(core.List(layer, group), piece);
                    var place = stream.Position;
                    stream.Write(span);
                    FlashFile.Settle(stream, place, span.Length);
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
            FlashFile.Free(piece);
        }
        return new CachedIndex(path, core.Groups, pieceBytes, entries);
    }

    /// <summary>The cached index at <paramref name="path"/> when it was built for <paramref name="model"/> and <paramref name="core"/>; null otherwise.</summary>
    public static CachedIndex? Open(string path, GgufFile model, ThoughtIndex core)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(core);
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 12);
            var head = new byte[64];
            stream.ReadExactly(head);
            if (BinaryPrimitives.ReadUInt64LittleEndian(head) != Magic || BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(8)) != Version
                || BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(12)) != model.Length
                || BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(20)) != core.Layers
                || BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(24)) != core.Groups
                || BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(28)) != core.ListNeurons
                || BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(32)) != core.Embedding)
                return null;
            var count = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(36));
            var table = new byte[(long)count * EntryBytes];
            stream.ReadExactly(table);
            var entries = new Dictionary<long, (long Offset, long Bytes)>(count);
            for (var i = 0; i < count; i++)
            {
                var at = table.AsSpan(i * EntryBytes, EntryBytes);
                var layer = BinaryPrimitives.ReadInt32LittleEndian(at);
                var group = BinaryPrimitives.ReadInt32LittleEndian(at[4..]);
                var offset = BinaryPrimitives.ReadInt64LittleEndian(at[8..]);
                var bytes = BinaryPrimitives.ReadInt64LittleEndian(at[16..]);
                if (offset + bytes > stream.Length) return null;
                entries[Key(layer, group, core.Groups)] = (offset, bytes);
            }
            var stride = count > 0 ? BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(16, 8)) : FlashFile.Pages(PieceBytes(core.ListNeurons, core.Embedding));
            return new CachedIndex(path, core.Groups, stride, entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>A layer's ffn_gate and ffn_up read from the model's own file, and a group's rows gathered from them and tiled into a piece.</summary>
internal sealed unsafe class RowGather : IDisposable
{
    private readonly GgufFile _model;
    private readonly FlashFile _file;
    private readonly int _neurons, _embedding, _channels, _readBytes, _rowBytes;
    private readonly int _feedForward, _downFullRowBytes, _downRowBytes;
    private readonly byte* _gate, _up, _rows, _down;
    private readonly float* _col;
    private readonly long _window, _downWindow;
    private long _gateSkip, _upSkip, _downSkip;

    public RowGather(GgufFile model, int neurons, int embedding, int channels, int readBytes)
    {
        _model = model;
        _neurons = neurons;
        _embedding = embedding;
        _channels = channels;
        _readBytes = readBytes;
        _rowBytes = embedding / Ptq1.BlockWeights * Ptq1.BlockBytes;
        var c = ModelConfig.FromGguf(model);
        _feedForward = c.FeedForward;
        _downFullRowBytes = c.FeedForward / Ptq1.BlockWeights * Ptq1.BlockBytes;
        _downRowBytes = embedding / Ptq1.BlockWeights * Ptq1.BlockBytes;
        _window = FlashFile.Pages((long)c.FeedForward * _rowBytes) + 2 * FlashFile.Page;
        _downWindow = FlashFile.Pages((long)embedding * _downFullRowBytes) + 2 * FlashFile.Page;
        _file = FlashFile.Open(model.FilePath);
        _gate = FlashFile.Allocate(_window);
        _up = FlashFile.Allocate(_window);
        _down = FlashFile.Allocate(_downWindow);
        _rows = FlashFile.Allocate((long)neurons * _rowBytes);
        _col = (float*)FlashFile.Allocate((long)embedding * sizeof(float));
    }

    /// <summary>Bytes read from the model's file so far.</summary>
    public long Read { get; private set; }

    /// <summary>Layer <paramref name="layer"/>'s gate, up and down, read past the cache.</summary>
    public void Load(int layer)
    {
        _gateSkip = ReadTensor($"blk.{layer}.ffn_gate.weight", _gate, _window);
        _upSkip = ReadTensor($"blk.{layer}.ffn_up.weight", _up, _window);
        _downSkip = ReadTensor($"blk.{layer}.ffn_down.weight", _down, _downWindow);
    }

    /// <summary><paramref name="neurons"/>' gate, up and down rows from the loaded layer, tiled into <paramref name="piece"/> (a piece's layout).</summary>
    public void Tile(ReadOnlySpan<ushort> neurons, byte* piece)
    {
        var weights = Ptq1PairMatrix.WeightBytes(_neurons, _embedding);
        var scales = Ptq1PairMatrix.ScaleCount(_neurons, _embedding) * sizeof(float);
        TileOne(_gate + _gateSkip, neurons, piece, weights);
        TileOne(_up + _upSkip, neurons, piece + weights + scales, weights);
        TileDown(neurons, piece + 2 * (weights + scales));
    }

    // Each neuron's ffn_down column - one weight per output row - gathered and re-quantised into a ternary row of its own.
    private void TileDown(ReadOnlySpan<ushort> neurons, byte* into)
    {
        var col = new Span<float>(_col, _embedding);
        Span<sbyte> trits = stackalloc sbyte[Ptq1.BlockWeights];
        var down = _down + _downSkip;
        for (var i = 0; i < neurons.Length; i++)
        {
            int block = neurons[i] / Ptq1.BlockWeights, pos = neurons[i] % Ptq1.BlockWeights;
            for (var e = 0; e < _embedding; e++)
            {
                var at = new ReadOnlySpan<byte>(down + (long)e * _downFullRowBytes + (long)block * Ptq1.BlockBytes, Ptq1.BlockBytes);
                Ptq1.DecodeScalar(at, trits);
                col[e] = trits[pos] * Ptq1.Scale(at);
            }
            var dst = into + (long)i * _downRowBytes;
            for (var b = 0; b < _embedding / Ptq1.BlockWeights; b++)
                Ptq1.Pack(col.Slice(b * Ptq1.BlockWeights, Ptq1.BlockWeights), new Span<byte>(dst + b * Ptq1.BlockBytes, Ptq1.BlockBytes));
        }
    }

    private void TileOne(byte* tensor, ReadOnlySpan<ushort> neurons, byte* into, long weights)
    {
        var rows = new Span<byte>(_rows, _neurons * _rowBytes);
        for (var i = 0; i < neurons.Length; i++)
            new ReadOnlySpan<byte>(tensor + (long)neurons[i] * _rowBytes, _rowBytes).CopyTo(rows.Slice(i * _rowBytes, _rowBytes));
        var scaleCount = Ptq1PairMatrix.ScaleCount(_neurons, _embedding);
        Ptq1PairMatrix.Repack(rows, _neurons, _embedding, new Span<byte>(into, (int)weights),
            new Span<float>((float*)(into + weights), (int)scaleCount), Math.Clamp(Environment.ProcessorCount / 2, 1, 8));
    }

    // A tensor's page-aligned span read into `into`; returns where its rows start in it.
    private long ReadTensor(string name, byte* into, long window)
    {
        var tensor = _model.Tensor(name) ?? throw new InvalidDataException($"The model has no {name}.");
        var start = tensor.Offset & ~(long)(FlashFile.Page - 1);
        var span = FlashFile.Pages(tensor.Offset + tensor.ByteCount) - start;
        if (span > window) throw new InvalidDataException($"{name} is wider than the gather's window.");
        var got = _file.ReadChannels(into, span, start, _channels, _readBytes);
        if (got < tensor.Offset - start + tensor.ByteCount) throw new InvalidDataException($"{_model.FilePath} ends inside {name}.");
        Read += span;
        return tensor.Offset - start;
    }

    public void Dispose()
    {
        FlashFile.Free(_gate);
        FlashFile.Free(_up);
        FlashFile.Free(_down);
        FlashFile.Free(_rows);
        FlashFile.Free((byte*)_col);
        _file.Dispose();
    }
}

/// <summary>
/// The three tiers serving a model's feed-forward gate and up: a word's thought finds its group in the core index, and the
/// group's piece comes from RAM (the warm index), else the cached index on flash, else the core - the model's own file.
/// </summary>
public sealed unsafe class IndexedFeedForward : IDisposable
{
    // A cached piece is promoted into RAM once this many words in a row have read it from the cached index: the most-used
    // kept hot, as the design asks, without warming a piece one word touched.
    private const int PromoteAfter = 2;

    private readonly ThoughtIndex _core;
    private readonly CachedIndex? _cached;      // the cached index: a hot portion of the core, on flash
    private readonly CachedIndex? _coreStore;   // the core index's pieces, complete, on flash; read when the cached index lacks a group
    private readonly GgufFile _model;
    private readonly ThoughtSketch _sketch;
    private readonly float[] _sketched = new float[ThoughtSketch.Width];
    private readonly Dictionary<long, Warm> _warm = [];     // the warm index: a group's piece held in RAM, by key
    private readonly Dictionary<long, int> _cachedHits = []; // words that have read a flash group since it was last warm
    private readonly long _warmBudget;
    private long _clock;                   // ticks up on every warm read and promotion, so the least-recently-used can go
    private readonly long[] _usage;        // words that fell in each group since the last TakeUsage - what the night revises from
    private readonly FlashFile? _cachedFile;
    private readonly FlashFile? _coreStoreFile;
    private readonly byte* _read;          // the piece read last from flash, for the word computing now
    private readonly long _pieceBytes, _weights, _scales;
    private readonly int _channels, _readBytes;
    private RowGather? _gather;            // the core's reads, made the first time a group is not cached
    private int _gatherLayer = -1;
    private long _warmHits, _cachedReads, _cachedBytes, _coreReads, _coreBytes;
    private int _disposed;

    // A group's piece in RAM: the bytes it owns, a view over them, and when it was last used.
    private sealed class Warm(nint buffer, IndexPiece piece)
    {
        public nint Buffer { get; } = buffer;
        public IndexPiece Piece { get; } = piece;
        public long Used { get; set; }
    }

    /// <summary>
    /// Serves <paramref name="model"/>'s feed-forward from the warm index in RAM, then <paramref name="cached"/> (a hot
    /// portion, may be null), then <paramref name="coreStore"/> (the complete core on flash, may be null), then the model's
    /// own file; the hottest cached pieces held in RAM up to <paramref name="warmBudget"/> bytes.
    /// </summary>
    public IndexedFeedForward(ThoughtIndex core, CachedIndex? cached, GgufFile model, long warmBudget, int channels = 4, int readBytes = 1 << 20, CachedIndex? coreStore = null)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(model);
        _core = core;
        _cached = cached;
        _coreStore = coreStore;
        _model = model;
        _warmBudget = Math.Max(0, warmBudget);
        _channels = Math.Max(1, channels);
        _readBytes = Math.Max(FlashFile.Page, readBytes);
        _sketch = new ThoughtSketch(core.Embedding);
        _weights = Ptq1PairMatrix.WeightBytes(core.ListNeurons, core.Embedding);
        _scales = Ptq1PairMatrix.ScaleCount(core.ListNeurons, core.Embedding) * sizeof(float);
        _pieceBytes = FlashFile.Pages(CachedIndex.PieceBytes(core.ListNeurons, core.Embedding));
        if (cached is not null) _pieceBytes = Math.Max(_pieceBytes, cached.PieceStride);
        if (coreStore is not null) _pieceBytes = Math.Max(_pieceBytes, coreStore.PieceStride);
        _usage = new long[core.Layers * core.Groups];
        _read = FlashFile.Allocate(_pieceBytes);
        if (coreStore is not null) _coreStoreFile = FlashFile.Open(coreStore.FilePath);
        if (cached is null) return;
        _cachedFile = FlashFile.Open(cached.FilePath);

        // THE WARM INDEX: the hottest cached pieces - the groups the most words fell in - read into RAM while the budget lasts.
        var started = Stopwatch.GetTimestamp();
        var hottest = Enumerable.Range(0, core.Layers).SelectMany(il => Enumerable.Range(0, core.Groups).Select(g => (Layer: il, Group: g)))
            .Where(p => cached.Entry(p.Layer, p.Group) is not null).OrderByDescending(p => core.Size(p.Layer, p.Group));
        foreach (var (layer, group) in hottest)
        {
            if (WarmBytes + _pieceBytes > warmBudget) break;
            var buffer = FlashFile.Allocate(_pieceBytes);
            ReadFromStore(cached, _cachedFile, layer, group, buffer);
            _warm[CachedIndex.Key(layer, group, core.Groups)] = new Warm((nint)buffer, View(layer, group, buffer)) { Used = ++_clock };
            WarmBytes += _pieceBytes;
        }
        WarmElapsed = Stopwatch.GetElapsedTime(started);
    }

    /// <summary>What the warm index holds in RAM.</summary>
    public long WarmBytes { get; private set; }

    /// <summary>Pieces the warm index holds.</summary>
    public int WarmPieces => _warm.Count;

    /// <summary>How long the warm index took to read in.</summary>
    public TimeSpan WarmElapsed { get; }

    /// <summary>The core index it serves from.</summary>
    public ThoughtIndex Core => _core;

    /// <summary>
    /// The piece for a word whose <paramref name="thought"/> enters layer <paramref name="layer"/>'s feed-forward: its group's,
    /// from RAM, else the cached index, else the core. One read from flash is valid until the next call.
    /// </summary>
    public IndexPiece Piece(int layer, ReadOnlySpan<float> thought)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _sketch.Sketch(thought, _sketched);
        var group = _core.Nearest(layer, _sketched);
        var key = CachedIndex.Key(layer, group, _core.Groups);
        _usage[key]++;   // what the day used, for the night to revise the cached index from
        if (_warm.TryGetValue(key, out var warm))
        {
            warm.Used = ++_clock;
            _warmHits++;
            return warm.Piece;
        }
        if (_cached?.Entry(layer, group) is not null)
        {
            ReadFromStore(_cached, _cachedFile!, layer, group, _read);
            _cachedReads++;
            _cachedBytes += _pieceBytes;
            var served = View(layer, group, _read);
            KeepHot(layer, group, key);
            return served;
        }
        if (_coreStore?.Entry(layer, group) is not null)
        {
            // THE CORE, complete on flash: one read brings this group's piece, not its layer.
            ReadFromStore(_coreStore, _coreStoreFile!, layer, group, _read);
            _coreReads++;
            _coreBytes += _pieceBytes;
            var served = View(layer, group, _read);
            KeepHot(layer, group, key);
            return served;
        }
        _gather ??= new RowGather(_model, _core.ListNeurons, _core.Embedding, _channels, _readBytes);
        if (_gatherLayer != layer)
        {
            var before = _gather.Read;
            _gather.Load(layer);
            _gatherLayer = layer;
            _coreBytes += _gather.Read - before;
        }
        new Span<byte>(_read, (int)_pieceBytes).Clear();
        _gather.Tile(_core.List(layer, group), _read);
        _coreReads++;
        return View(layer, group, _read);
    }

    // THE MOST-USED KEPT HOT: once a cached group has been read PromoteAfter words in a row, its piece is copied into RAM,
    // the least-recently-used warm piece making room when the budget is full. A word already warm never reaches here.
    private void KeepHot(int layer, int group, long key)
    {
        if (_warmBudget < _pieceBytes) return;              // no room in RAM for even one piece
        var hits = _cachedHits.GetValueOrDefault(key) + 1;
        if (hits < PromoteAfter)
        {
            _cachedHits[key] = hits;
            return;
        }
        while (WarmBytes + _pieceBytes > _warmBudget && _warm.Count > 0) EvictLeastRecentlyUsed();
        if (WarmBytes + _pieceBytes > _warmBudget) return;  // nothing left to evict and still no room
        var buffer = FlashFile.Allocate(_pieceBytes);
        Buffer.MemoryCopy(_read, buffer, _pieceBytes, _pieceBytes);
        _warm[key] = new Warm((nint)buffer, View(layer, group, buffer)) { Used = ++_clock };
        WarmBytes += _pieceBytes;
        _cachedHits.Remove(key);
    }

    private void EvictLeastRecentlyUsed()
    {
        var oldest = long.MaxValue;
        long oldestKey = -1;
        foreach (var (k, w) in _warm)
        {
            if (w.Used >= oldest) continue;
            oldest = w.Used;
            oldestKey = k;
        }
        if (oldestKey < 0) return;
        FlashFile.Free((byte*)_warm[oldestKey].Buffer);
        _warm.Remove(oldestKey);
        WarmBytes -= _pieceBytes;
    }

    /// <summary>What the tiers served since the last call - words from RAM, reads from the cached index and the core, and their bytes - and the counts start again.</summary>
    public (long Warm, long Cached, long CachedBytes, long Core, long CoreBytes) TakeCounts()
    {
        var counts = (_warmHits, _cachedReads, _cachedBytes, _coreReads, _coreBytes);
        _warmHits = _cachedReads = _cachedBytes = _coreReads = _coreBytes = 0;
        return counts;
    }

    /// <summary>
    /// How many words fell in each group since the last call, by <see cref="CachedIndex.Key(int,int,int)"/>, the count
    /// starting again - the day's use of the index, which the night rebuilds the cached index from (<see cref="CachedIndex.Build"/>).
    /// </summary>
    public long[] TakeUsage()
    {
        var copy = (long[])_usage.Clone();
        Array.Clear(_usage);
        return copy;
    }

    private void ReadFromStore(CachedIndex store, FlashFile file, int layer, int group, byte* into)
    {
        var (offset, bytes) = store.Entry(layer, group)!.Value;
        if (file.ReadChannels(into, bytes, offset, _channels, _readBytes) < bytes)
            throw new InvalidDataException($"{store.FilePath} ends inside layer {layer}'s group {group}.");
    }

    private IndexPiece View(int layer, int group, byte* piece) => new(
        _core.ListArray(layer, group),
        Ptq1PairMatrix.Over(piece, (float*)(piece + _weights), _core.ListNeurons, _core.Embedding),
        Ptq1PairMatrix.Over(piece + _weights + _scales, (float*)(piece + 2 * _weights + _scales), _core.ListNeurons, _core.Embedding),
        new DownPiece(piece + 2 * (_weights + _scales), _core.ListNeurons, _core.Embedding));

    /// <summary>Lets the warm index and every buffer go: RAM holds none of the index after this.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var warm in _warm.Values) FlashFile.Free((byte*)warm.Buffer);
        _warm.Clear();
        FlashFile.Free(_read);
        _gather?.Dispose();
        _cachedFile?.Dispose();
        _coreStoreFile?.Dispose();
    }
}
