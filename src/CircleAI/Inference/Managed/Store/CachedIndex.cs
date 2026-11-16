// CachedIndex.cs
//
// STEP 2 ON FLASH, AND STEP 4 IN RAM (the owner, 2026-11-15: "1) Download llm, 2) split, organise by thoughts, 3) create
// CORE index. Select coherent portions from 3 into RAM for 4) warm index. Only 4 ever reaches into 3, only 3 reaches into
// 2 and only 2 reaches into 1"). Step 3, the core index (ThoughtIndex), names each thought's neurons at every layer. A
// piece is one thought's neurons at one layer, side by side: their ffn_gate and ffn_up rows in the vector kernel's tiles,
// and - where the layers do not bring their own (LayersTakeTheirOwnDown) - their ffn_down by neuron. This layout holds
// step 2's pieces: coherent thoughts a thought at a time, every layer of it in the order the layers run, so once a word's
// thought is known at the first layer its whole pass is one run of flash, read ahead of the layers that want it. Step 4
// (IndexedFeedForward) keeps whole thoughts in RAM, the most used first; what it does not hold step 2 brings; what step 2
// does not hold it gathers from step 1, the model's own file. Step 2's other half is every layer's attention, and its
// ffn_down where the layers take their own, tiled once (TileStore) so no pass repacks the model's file.
//
// FFN_DOWN BY NEURON, THE FOLD UNDONE. The model stores ffn_down by output row, where a neuron is a column; and where the
// file folds a Hadamard rotation into ffn_down's input (Ternary-Bonsai-2), a stored column is not a neuron at all but a
// mix of the 128 or 512 in its block. Split undoes it: each stored row is turned back to the neurons' own basis
// (InputTransform.Unfold) and a piece keeps each of its neurons' own column as a row. Those values are no longer ternary,
// so the row is kept at sixteen levels a weight (Q4).
//
// Layout, little-endian:
//   header   magic "CAICACHE", version, the model's length, layers, groups, neurons a list, embedding, entries, data start,
//            the mark of the core index whose lists the pieces were written for, whether the pieces hold their ffn_down
//   entries  per piece: layer, group, where it starts in the file, its bytes, its heat (the use step 4 warms it by)
//   pieces   each a whole number of the profile's read chunks: the gate's tiles, its scales, the up's tiles, its scales,
//            then - where held - the down's rows, a neuron a row, Q4. Coherent thoughts' pieces lie a thought at a time,
//            its layers in order; per-layer groups' a layer at a time.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Kernels;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Rotation;

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
/// A group's ffn_down, by neuron: for each of the group's neurons, its own column of ffn_down as a row of sixteen-level
/// weights (<see cref="Q4"/>). The model stores ffn_down by output row, where a neuron is a column spread across every row
/// - and, where the file folds a rotation in, mixed with the others of its block; the index keeps each neuron's column,
/// the fold undone, as a row of its own, so a word's down projection sums only the group's neurons.
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
        _blocks = embedding / Q4.BlockWeights;
        _rowBytes = RowBytes(embedding);
    }

    /// <summary>Bytes a neuron's row takes for a model <paramref name="embedding"/> wide.</summary>
    public static int RowBytes(int embedding) => embedding / Q4.BlockWeights * Q4.BlockBytes;

    /// <summary>
    /// The feed-forward's down projection for a word, over the group's neurons only:
    /// <paramref name="output"/>[e] = sum over the group of <paramref name="activations"/>[neuron] times that neuron's
    /// ffn_down column. <paramref name="neurons"/> are the piece's neurons, in its order; the activations are the
    /// neurons' own, not turned.
    /// </summary>
    public void Project(ReadOnlySpan<float> activations, ReadOnlySpan<ushort> neurons, Span<float> output)
    {
        if (output.Length < _embedding) throw new ArgumentException($"The down projection is {_embedding} wide, not {output.Length}.", nameof(output));
        output[.._embedding].Clear();
        fixed (float* o = output)
        {
            for (var i = 0; i < _neurons; i++)
            {
                var a = activations[neurons[i]];
                if (a == 0) continue;
                var row = _rows + (long)i * _rowBytes;
                for (var b = 0; b < _blocks; b++) Q4.AddScaled(row + b * Q4.BlockBytes, a, o + b * Q4.BlockWeights);
            }
        }
    }
}

/// <summary>Step 2's pieces side by side on flash: each thought's neurons at each layer (<see cref="CachedIndex"/>'s layout).</summary>
public sealed class CachedIndex
{
    /// <summary>"CAICACHE", little-endian.</summary>
    public const ulong Magic = 0x4548_4341_4349_4143;

    /// <summary>
    /// The layout's version: 2 kept ffn_down's rows at sixteen levels, the fold undone, and each piece's heat; 3 lays
    /// coherent thoughts' pieces a thought at a time, every layer of it in order, and holds no ffn_down where the layers
    /// take their own (<see cref="LayersTakeTheirOwnDown(ThoughtIndex)"/>).
    /// </summary>
    public const int Version = 3;

    internal const int HeaderBytes = 64;
    internal const int EntryBytes = 32;
    private readonly Dictionary<long, (long Offset, long Bytes, long Heat)> _entries;

    internal CachedIndex(string path, int groups, long pieceStride, IReadOnlyDictionary<long, (long Offset, long Bytes, long Heat)> entries, bool holdsDown = true)
    {
        FilePath = path;
        Groups = groups;
        PieceStride = pieceStride;
        HoldsDown = holdsDown;
        _entries = new Dictionary<long, (long Offset, long Bytes, long Heat)>(entries);
    }

    /// <summary>The file.</summary>
    public string FilePath { get; }

    /// <summary>Thought groups a layer.</summary>
    public int Groups { get; }

    /// <summary>The bytes a stored piece takes, the same for every one - a whole number of the profile's read chunks, so a
    /// scattered read runs at nearly full flash speed.</summary>
    public long PieceStride { get; }

    /// <summary>
    /// Whether each piece holds its neurons' ffn_down; false where the layers take their own, whole and exact, from step
    /// 2's tiled layers (<see cref="LayersTakeTheirOwnDown(ThoughtIndex)"/>).
    /// </summary>
    public bool HoldsDown { get; }

    /// <summary>
    /// WHETHER THE LAYERS TAKE THEIR OWN FFN_DOWN, WHOLE, OVER THE PIECES' ROUNDED COPY (the owner, 2026-11-15, of an index
    /// that uses the model's own output weights: "Yes, try it"). A piece holds its listed neurons' columns of ffn_down at
    /// sixteen levels - one Q4 block, 66 bytes, for each 128 of a neuron's outputs - and the model's file the whole matrix
    /// at 1.75 bits a weight: 28 bytes for each 128 of every neuron's. From 42 in a hundred listed the model's own is the
    /// fewer bytes, as well as exact. Its columns cannot be cut by neuron without rounding: the file folds a rotation into
    /// ffn_down's input, so a stored column is a mix of the 128 or 512 neurons of its block.
    /// </summary>
    public static bool LayersTakeTheirOwnDown(ThoughtIndex core)
    {
        ArgumentNullException.ThrowIfNull(core);
        return LayersTakeTheirOwnDown(core.ListNeurons, core.FeedForward);
    }

    /// <summary>As <see cref="LayersTakeTheirOwnDown(ThoughtIndex)"/>, for lists of <paramref name="listNeurons"/> of <paramref name="feedForward"/> neurons.</summary>
    public static bool LayersTakeTheirOwnDown(int listNeurons, int feedForward) =>
        (long)listNeurons * Q4.BlockBytes >= (long)feedForward * Ptq1.BlockBytes;

    /// <summary>Pieces it holds.</summary>
    public int Count => _entries.Count;

    /// <summary>Where layer <paramref name="layer"/>'s group <paramref name="group"/>'s piece lies; null when it is not held.</summary>
    public (long Offset, long Bytes)? Entry(int layer, int group) =>
        _entries.TryGetValue(Key(layer, group, Groups), out var e) ? (e.Offset, e.Bytes) : null;

    /// <summary>
    /// The use layer <paramref name="layer"/>'s group <paramref name="group"/>'s piece was chosen by - the words that fell in
    /// it over the days Revise has seen; 0 when it was chosen by what Watch saw, or is not held. RAM takes the hottest first.
    /// </summary>
    public long Heat(int layer, int group) => _entries.TryGetValue(Key(layer, group, Groups), out var e) ? e.Heat : 0;

    /// <summary>Whether it holds the piece of every group of <paramref name="core"/> that Watch saw a word fall in: the core, complete.</summary>
    public bool HoldsAll(ThoughtIndex core)
    {
        ArgumentNullException.ThrowIfNull(core);
        for (var layer = 0; layer < core.Layers; layer++)
        {
            for (var group = 0; group < core.Groups; group++)
            {
                if (core.Size(layer, group) > 0 && !_entries.ContainsKey(Key(layer, group, Groups))) return false;
            }
        }
        return true;
    }

    internal static long Key(int layer, int group, int groups) => (long)layer * groups + group;

    /// <summary>Bytes a piece of <paramref name="neurons"/> neurons of <paramref name="embedding"/> takes: gate and up as the
    /// kernel's tiles and scales, then - with <paramref name="withDown"/> - down as one sixteen-level row a neuron (the
    /// whole knowledge layer, by neuron).</summary>
    public static long PieceBytes(int neurons, int embedding, bool withDown = true) =>
        2 * (Ptq1PairMatrix.WeightBytes(neurons, embedding) + Ptq1PairMatrix.ScaleCount(neurons, embedding) * sizeof(float))
        + (withDown ? (long)neurons * DownPiece.RowBytes(embedding) : 0);

    /// <summary>
    /// The bytes a stored piece takes where flash is read <paramref name="readBytes"/> at a time: its bytes rounded up to
    /// whole read chunks, so a scattered read of it runs at nearly full flash speed - no piece smaller than the device
    /// reads well. With a page-sized chunk this is just the page-aligned piece.
    /// </summary>
    public static long Stride(int neurons, int embedding, int readBytes, bool withDown = true)
    {
        var chunk = FlashFile.Pages(Math.Max(FlashFile.Page, readBytes));
        return (PieceBytes(neurons, embedding, withDown) + chunk - 1) / chunk * chunk;
    }

    /// <summary>
    /// Writes the pieces of <paramref name="core"/> for <paramref name="model"/> to <paramref name="path"/>: the groups used
    /// most (<paramref name="usage"/>, a count a group by layer then group: REVISE) and then the groups the most of Watch's
    /// words fell in, until <paramref name="budget"/> bytes. A piece <paramref name="from"/> already holds is copied from
    /// it; any other is gathered from the model's own file. With <paramref name="withDown"/> false the pieces hold no
    /// ffn_down: the layers take their own. Returns it, opened.
    /// </summary>
    public static CachedIndex Build(ThoughtIndex core, GgufFile model, string path, long budget, int channels = 4, int readBytes = 1 << 20,
        IReadOnlyList<long>? usage = null, CachedIndex? from = null, bool withDown = true)
    {
        var writer = PieceStoreWriter.Begin(core, model, path, budget, channels, readBytes, usage, from, resume: false, withDown: withDown);
        try
        {
            for (var layer = 0; layer < core.Layers; layer++) writer.Write(layer);
            return writer.Finish();
        }
        catch
        {
            writer.Dispose();
            try { File.Delete(writer.PartialPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>The pieces at <paramref name="path"/> when they were written for <paramref name="model"/> and <paramref name="core"/>; null otherwise.</summary>
    public static CachedIndex? Open(string path, GgufFile model, ThoughtIndex core)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(core);
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 12);
            var head = new byte[HeaderBytes];
            stream.ReadExactly(head);
            if (BinaryPrimitives.ReadUInt64LittleEndian(head) != Magic || BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(8)) != Version
                || BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(12)) != model.Length
                || BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(20)) != core.Layers
                || BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(24)) != core.Groups
                || BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(28)) != core.ListNeurons
                || BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(32)) != core.Embedding
                || BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(48)) != core.Fingerprint)
                return null;
            var count = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(36));
            var table = new byte[(long)count * EntryBytes];
            stream.ReadExactly(table);
            var entries = new Dictionary<long, (long Offset, long Bytes, long Heat)>(count);
            for (var i = 0; i < count; i++)
            {
                var at = table.AsSpan(i * EntryBytes, EntryBytes);
                var layer = BinaryPrimitives.ReadInt32LittleEndian(at);
                var group = BinaryPrimitives.ReadInt32LittleEndian(at[4..]);
                var offset = BinaryPrimitives.ReadInt64LittleEndian(at[8..]);
                var bytes = BinaryPrimitives.ReadInt64LittleEndian(at[16..]);
                if (offset + bytes > stream.Length) return null;
                entries[Key(layer, group, core.Groups)] = (offset, bytes, BinaryPrimitives.ReadInt64LittleEndian(at[24..]));
            }
            var holdsDown = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(56)) != 0;
            var stride = count > 0 ? BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(16, 8)) : FlashFile.Pages(PieceBytes(core.ListNeurons, core.Embedding, holdsDown));
            return new CachedIndex(path, core.Groups, stride, entries, holdsDown);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// REVISE, AT BEDTIME (the owner, 2026-11-12: "The more you use it the more it learns what the user needs"): the heat
    /// of every piece <paramref name="store"/> holds written again from <paramref name="use"/> - a count a group, by layer
    /// then group, of what the person's turns used - in its own table. The pieces stay where they lie; step 4 warms the
    /// hottest thoughts first from the next turn. Returns the store as it now reads.
    /// </summary>
    public static CachedIndex Revise(CachedIndex store, IReadOnlyList<long> use)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(use);
        var entries = new Dictionary<long, (long Offset, long Bytes, long Heat)>(store._entries.Count);
        using (var stream = new FileStream(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1 << 12))
        {
            var head = new byte[HeaderBytes];
            stream.ReadExactly(head);
            if (BinaryPrimitives.ReadUInt64LittleEndian(head) != Magic) throw new InvalidDataException($"{store.FilePath} is not a store of pieces.");
            var count = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(36));
            var table = new byte[(long)count * EntryBytes];
            stream.ReadExactly(table);
            for (var i = 0; i < count; i++)
            {
                var at = table.AsSpan(i * EntryBytes, EntryBytes);
                var key = Key(BinaryPrimitives.ReadInt32LittleEndian(at), BinaryPrimitives.ReadInt32LittleEndian(at[4..]), store.Groups);
                var heat = key < use.Count ? Math.Max(0, use[(int)key]) : 0;
                BinaryPrimitives.WriteInt64LittleEndian(at[24..], heat);
                entries[key] = (BinaryPrimitives.ReadInt64LittleEndian(at[8..]), BinaryPrimitives.ReadInt64LittleEndian(at[16..]), heat);
            }
            stream.Position = HeaderBytes;
            stream.Write(table);
            stream.Flush(flushToDisk: true);
        }
        return new CachedIndex(store.FilePath, store.Groups, store.PieceStride, entries, store.HoldsDown);
    }
}

/// <summary>
/// Pieces being written to flash a layer at a time, so the work can stop between layers - or inside one, which is then
/// thrown away - and carry on from the last layer finished (the indexing queue's units). The file's plan - which groups,
/// and where each piece lies - is fixed when it begins, so a layer's pieces go where they will stay.
/// </summary>
public sealed unsafe class PieceStoreWriter : IDisposable
{
    // The pieces of one layer held in RAM while they are gathered: more groups than this holds take another pass.
    private const long PassBytes = 128L << 20;

    private readonly ThoughtIndex _core;
    private readonly GgufFile _model;
    private readonly CachedIndex? _from;
    private readonly FlashFile? _fromFile;
    private readonly string _path;
    private readonly long _pieceBytes;
    private readonly int _channels, _readBytes, _perPass;
    private readonly bool _withDown;         // whether the pieces hold their ffn_down
    private readonly List<int>[] _byLayer;   // the groups chosen at each layer, in order
    private readonly Dictionary<long, (long Offset, long Bytes, long Heat)> _entries;
    private readonly FileStream _stream;
    private RowGather? _gather;
    private byte* _pieces;
    private int _disposed;

    private PieceStoreWriter(ThoughtIndex core, GgufFile model, string path, long pieceBytes, int channels, int readBytes, CachedIndex? from,
        List<int>[] byLayer, Dictionary<long, (long Offset, long Bytes, long Heat)> entries, FileStream stream, bool resumed, bool withDown)
    {
        _core = core;
        _model = model;
        _path = path;
        _pieceBytes = pieceBytes;
        _channels = channels;
        _readBytes = readBytes;
        _withDown = withDown;
        _from = from is not null && from.PieceStride == pieceBytes && from.HoldsDown == withDown ? from : null;
        _fromFile = _from is null ? null : FlashFile.Open(_from.FilePath);
        _byLayer = byLayer;
        _entries = entries;
        _stream = stream;
        _perPass = (int)Math.Clamp(PassBytes / pieceBytes, 1, Math.Max(1, core.Groups));
        Resumed = resumed;
    }

    /// <summary>Where the pieces will be once <see cref="Finish"/> has run.</summary>
    public string FilePath => _path;

    /// <summary>Where they are while they are being written.</summary>
    public string PartialPath => _path + ".partial";

    /// <summary>Layers to write: one call of <see cref="Write"/> each.</summary>
    public int Layers => _core.Layers;

    /// <summary>Pieces the plan holds.</summary>
    public int Pieces => _entries.Count;

    /// <summary>The bytes each piece takes.</summary>
    public long PieceStride => _pieceBytes;

    /// <summary>Whether an earlier run's unfinished file with the same plan was found and kept: its layers need not be written again.</summary>
    public bool Resumed { get; }

    /// <summary>
    /// Begins (or, with <paramref name="resume"/>, carries on) writing the pieces of <paramref name="core"/> for
    /// <paramref name="model"/> to <paramref name="path"/>: the groups used most (<paramref name="usage"/>) and then the
    /// groups the most of Watch's words fell in, until <paramref name="budget"/> bytes. A piece <paramref name="from"/>
    /// holds is copied from it rather than gathered from the model's file. With <paramref name="withDown"/> false the
    /// pieces hold no ffn_down: the layers take their own (<see cref="CachedIndex.LayersTakeTheirOwnDown(ThoughtIndex)"/>).
    /// </summary>
    public static PieceStoreWriter Begin(ThoughtIndex core, GgufFile model, string path, long budget, int channels = 4, int readBytes = 1 << 20,
        IReadOnlyList<long>? usage = null, CachedIndex? from = null, bool resume = true, bool withDown = true)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(model);
        if (usage is not null && usage.Count != core.Layers * core.Groups)
            throw new ArgumentException($"The use is a count a group, {core.Layers * core.Groups} of them, not {usage.Count}.", nameof(usage));

        var pieceBytes = CachedIndex.Stride(core.ListNeurons, core.Embedding, readBytes, withDown);

        // WHICH PIECES: the groups used most first (REVISE, when the use is given), then the groups the most of Watch's
        // words fell in, while the budget lasts. For coherent thoughts, whole thoughts: every layer of the most-used
        // thought before any of the next.
        var chosen = new List<(int Layer, int Group)>();
        long spent = 0;
        long Used(int layer, int group) => usage is null ? 0L : usage[(int)CachedIndex.Key(layer, group, core.Groups)];
        var ordered = core.Coherent
            ? Enumerable.Range(0, core.Groups)
                .Where(g => core.Size(0, g) > 0)
                .OrderByDescending(g => Enumerable.Range(0, core.Layers).Sum(il => Used(il, g)))
                .ThenByDescending(g => core.Size(0, g))
                .SelectMany(g => Enumerable.Range(0, core.Layers).Select(il => (Layer: il, Group: g)))
            : Enumerable.Range(0, core.Layers).SelectMany(il => Enumerable.Range(0, core.Groups).Select(g => (Layer: il, Group: g)))
                .Where(p => core.Size(p.Layer, p.Group) > 0)
                .OrderByDescending(p => Used(p.Layer, p.Group))
                .ThenByDescending(p => core.Size(p.Layer, p.Group));
        foreach (var (layer, group) in ordered)
        {
            if (spent + pieceBytes > budget) break;
            chosen.Add((layer, group));
            spent += pieceBytes;
        }

        // WHERE THEY LIE - STEP 2, ORGANISED BY THOUGHTS (the owner, 2026-11-15: "we need to know what to load when"). A
        // coherent thought's pieces a thought at a time, its layers in the order a pass runs them: once a word's thought is
        // found at the first layer, every piece its pass will want is one run of flash, read ahead of the layers. Per-layer
        // groups are found afresh at every layer, so theirs lie a layer at a time.
        if (core.Coherent)
            chosen = [.. chosen.OrderBy(p => p.Group).ThenBy(p => p.Layer)];
        else
            chosen.Sort();

        var dataStart = FlashFile.Pages(CachedIndex.HeaderBytes + (long)chosen.Count * CachedIndex.EntryBytes);
        var header = new byte[dataStart];
        BinaryPrimitives.WriteUInt64LittleEndian(header, CachedIndex.Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), CachedIndex.Version);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(12), model.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), core.Layers);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), core.Groups);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), core.ListNeurons);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32), core.Embedding);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(36), chosen.Count);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(40), dataStart);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(48), core.Fingerprint);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(56), withDown ? 1 : 0);
        var entries = new Dictionary<long, (long Offset, long Bytes, long Heat)>();
        var byLayer = new List<int>[core.Layers];
        for (var il = 0; il < core.Layers; il++) byLayer[il] = [];
        for (var i = 0; i < chosen.Count; i++)
        {
            var key = CachedIndex.Key(chosen[i].Layer, chosen[i].Group, core.Groups);
            var offset = dataStart + i * pieceBytes;
            var heat = usage is null ? 0L : usage[(int)key];
            var at = header.AsSpan(CachedIndex.HeaderBytes + i * CachedIndex.EntryBytes, CachedIndex.EntryBytes);
            BinaryPrimitives.WriteInt32LittleEndian(at, chosen[i].Layer);
            BinaryPrimitives.WriteInt32LittleEndian(at[4..], chosen[i].Group);
            BinaryPrimitives.WriteInt64LittleEndian(at[8..], offset);
            BinaryPrimitives.WriteInt64LittleEndian(at[16..], pieceBytes);
            BinaryPrimitives.WriteInt64LittleEndian(at[24..], heat);
            entries[key] = (offset, pieceBytes, heat);
            byLayer[chosen[i].Layer].Add(chosen[i].Group);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var stream = new FileStream(path + ".partial", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        try
        {
            // AN EARLIER RUN'S UNFINISHED FILE IS KEPT when its plan is this plan to the byte: its layers stay written.
            var resumed = false;
            if (resume && stream.Length >= header.Length)
            {
                var held = new byte[header.Length];
                stream.ReadExactly(held);
                resumed = held.AsSpan().SequenceEqual(header);
            }
            if (!resumed)
            {
                stream.SetLength(0);
                stream.Position = 0;
                stream.Write(header);
                stream.Flush(flushToDisk: true);
            }
            return new PieceStoreWriter(core, model, path, pieceBytes, Math.Max(1, channels), Math.Max(FlashFile.Page, readBytes), from, byLayer, entries, stream, resumed, withDown);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Writes layer <paramref name="layer"/>'s pieces where the plan put them, and settles them on flash. False when
    /// <paramref name="stop"/> said to stop before the layer was done: the layer is then to be written again.
    /// </summary>
    public bool Write(int layer, Func<bool>? stop = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if ((uint)layer >= (uint)_core.Layers) throw new ArgumentOutOfRangeException(nameof(layer), layer, $"The plan holds {_core.Layers} layers.");
        var groups = _byLayer[layer];
        if (groups.Count == 0) return true;
        if (_pieces == null) _pieces = FlashFile.Allocate(_perPass * _pieceBytes);

        for (var first = 0; first < groups.Count; first += _perPass)
        {
            if (stop?.Invoke() == true) return false;
            var n = Math.Min(_perPass, groups.Count - first);
            var lists = new List<ushort[]>(n);
            var into = new List<nint>(n);
            for (var i = 0; i < n; i++)
            {
                var group = groups[first + i];
                var piece = _pieces + i * _pieceBytes;
                if (_from?.Entry(layer, group) is { } held)
                {
                    // ALREADY ON FLASH (the core's piece, for the cached portion): copied, not gathered again.
                    if (_fromFile!.ReadChannels(piece, held.Bytes, held.Offset, _channels, _readBytes) < held.Bytes)
                        throw new InvalidDataException($"{_from.FilePath} ends inside layer {layer}'s group {group}.");
                    continue;
                }
                new Span<byte>(piece, (int)_pieceBytes).Clear();
                lists.Add(_core.ListArray(layer, group));
                into.Add((nint)piece);
            }
            if (lists.Count > 0)
            {
                _gather ??= new RowGather(_model, _core.ListNeurons, _core.Embedding, _channels, _readBytes);
                _gather.Load(layer);
                if (!_gather.Tile(lists, into, stop, _withDown)) return false;
            }
            for (var i = 0; i < n; i++)
            {
                if (stop?.Invoke() == true) return false;
                var offset = _entries[CachedIndex.Key(layer, groups[first + i], _core.Groups)].Offset;
                _stream.Position = offset;
                _stream.Write(new ReadOnlySpan<byte>(_pieces + i * _pieceBytes, (int)_pieceBytes));
                FlashFile.Settle(_stream, offset, _pieceBytes);
            }
        }
        return true;
    }

    /// <summary>Every layer written: the file takes its place, and is returned opened.</summary>
    public CachedIndex Finish()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _stream.Flush(flushToDisk: true);
        Dispose();
        File.Move(PartialPath, _path, overwrite: true);
        return new CachedIndex(_path, _core.Groups, _pieceBytes, _entries, _withDown);
    }

    /// <summary>Lets the file and the buffers go. What was written stays where it is, for a later run to carry on from.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stream.Dispose();
        _gather?.Dispose();
        _fromFile?.Dispose();
        FlashFile.Free(_pieces);
        _pieces = null;
    }
}

/// <summary>
/// A layer's ffn_gate, ffn_up and ffn_down read from the model's own file, and groups' pieces gathered from them: the
/// neurons' gate and up rows tiled, and their ffn_down columns - the file's fold undone - as rows (a piece's layout).
/// </summary>
internal sealed unsafe class RowGather : IDisposable
{
    // Output rows of ffn_down turned back to the neurons' basis at a time: the 128 a Q4 block of a neuron's row holds.
    private const int Chunk = Q4.BlockWeights;

    private readonly GgufFile _model;
    private readonly PrismHadamard? _hadamard;
    private readonly FlashFile _file;
    private readonly int _neurons, _embedding, _channels, _readBytes, _rowBytes;
    private readonly int _feedForward, _downFullRowBytes, _downRowBytes;
    private readonly byte* _gate, _up, _rows;
    private readonly long _window, _downWindow;
    private byte* _down;      // ffn_down, read the first time a piece of the loaded layer wants it
    private float* _panel;    // Chunk rows of ffn_down in the neurons' own basis, by neuron: [neuron][row]
    private float* _row;      // one row of ffn_down, decoded
    private long _gateSkip, _upSkip, _downSkip;
    private int _layer = -1;
    private bool _downLoaded;
    private InputTransform? _unfold;   // the loaded layer's ffn_down fold; null where the file folds none

    public RowGather(GgufFile model, int neurons, int embedding, int channels, int readBytes)
    {
        _model = model;
        _hadamard = PrismHadamard.FromGguf(model);
        _neurons = neurons;
        _embedding = embedding;
        _channels = channels;
        _readBytes = readBytes;
        _rowBytes = embedding / Ptq1.BlockWeights * Ptq1.BlockBytes;
        var c = ModelConfig.FromGguf(model);
        _feedForward = c.FeedForward;
        _downFullRowBytes = c.FeedForward / Ptq1.BlockWeights * Ptq1.BlockBytes;
        _downRowBytes = DownPiece.RowBytes(embedding);
        _window = FlashFile.Pages((long)c.FeedForward * _rowBytes) + 2 * FlashFile.Page;
        _downWindow = FlashFile.Pages((long)embedding * _downFullRowBytes) + 2 * FlashFile.Page;
        _file = FlashFile.Open(model.FilePath);
        _gate = FlashFile.Allocate(_window);
        _up = FlashFile.Allocate(_window);
        _rows = FlashFile.Allocate((long)neurons * _rowBytes);
    }

    /// <summary>Bytes read from the model's file so far.</summary>
    public long Read { get; private set; }

    /// <summary>Layer <paramref name="layer"/>'s gate and up, read past the cache; its down follows when a piece wants it. Nothing when it is the layer loaded.</summary>
    public void Load(int layer)
    {
        if (layer == _layer) return;
        _layer = -1;
        _downLoaded = false;
        _gateSkip = ReadTensor($"blk.{layer}.ffn_gate.weight", _gate, _window);
        _upSkip = ReadTensor($"blk.{layer}.ffn_up.weight", _up, _window);
        _unfold = _hadamard?.InputOf($"blk.{layer}.ffn_down.weight");
        _layer = layer;
    }

    /// <summary><paramref name="neurons"/>' gate, up and (with <paramref name="down"/>) down rows from the loaded layer, tiled into <paramref name="piece"/>.</summary>
    public void Tile(ushort[] neurons, byte* piece, bool down = true) => Tile([neurons], [(nint)piece], null, down);

    /// <summary>
    /// Each of <paramref name="lists"/>' gate, up and (with <paramref name="down"/>) down rows from the loaded layer, tiled
    /// into its piece of <paramref name="pieces"/>: ffn_down is turned back to the neurons' basis once for all of them.
    /// False when <paramref name="stop"/> said to stop part way: the pieces are then not whole.
    /// </summary>
    public bool Tile(IReadOnlyList<ushort[]> lists, IReadOnlyList<nint> pieces, Func<bool>? stop = null, bool down = true)
    {
        if (_layer < 0) throw new InvalidOperationException("No layer is loaded.");
        var weights = Ptq1PairMatrix.WeightBytes(_neurons, _embedding);
        var scales = Ptq1PairMatrix.ScaleCount(_neurons, _embedding) * sizeof(float);
        for (var g = 0; g < lists.Count; g++)
        {
            if (stop?.Invoke() == true) return false;
            var piece = (byte*)pieces[g];
            TileOne(_gate + _gateSkip, lists[g], piece, weights);
            TileOne(_up + _upSkip, lists[g], piece + weights + scales, weights);
        }
        return !down || TileDown(lists, pieces, 2 * (weights + scales), stop);
    }

    // EVERY NEURON'S OWN COLUMN OF FFN_DOWN, a chunk of output rows at a time: each stored row decoded, the fold undone
    // so its entries are the neurons' own, and laid by neuron; then each list's neurons take their 128 weights of the chunk
    // as one block of their row.
    private bool TileDown(IReadOnlyList<ushort[]> lists, IReadOnlyList<nint> pieces, long downOffset, Func<bool>? stop)
    {
        if (!_downLoaded)
        {
            if (_down == null) _down = FlashFile.Allocate(_downWindow);
            _downSkip = ReadTensor($"blk.{_layer}.ffn_down.weight", _down, _downWindow);
            _downLoaded = true;
        }
        if (_panel == null)
        {
            _panel = (float*)FlashFile.Allocate((long)_feedForward * Chunk * sizeof(float));
            _row = (float*)FlashFile.Allocate((long)_feedForward * sizeof(float));
        }
        var blocks = _feedForward / Ptq1.BlockWeights;
        var row = new Span<float>(_row, _feedForward);
        Span<sbyte> trits = stackalloc sbyte[Ptq1.BlockWeights];
        var stored = _down + _downSkip;
        for (var first = 0; first < _embedding; first += Chunk)
        {
            if (stop?.Invoke() == true) return false;
            for (var r = 0; r < Chunk; r++)
            {
                var source = stored + (long)(first + r) * _downFullRowBytes;
                for (var b = 0; b < blocks; b++)
                {
                    var block = new ReadOnlySpan<byte>(source + b * Ptq1.BlockBytes, Ptq1.BlockBytes);
                    var scale = Ptq1.Scale(block);
                    Ptq1.DecodeScalar(block, trits);
                    var at = row.Slice(b * Ptq1.BlockWeights, Ptq1.BlockWeights);
                    for (var j = 0; j < Ptq1.BlockWeights; j++) at[j] = trits[j] * scale;
                }
                _unfold?.Unfold(row);
                for (var n = 0; n < _feedForward; n++) _panel[(long)n * Chunk + r] = row[n];
            }
            var blockAt = first / Chunk * Q4.BlockBytes;
            for (var g = 0; g < lists.Count; g++)
            {
                var list = lists[g];
                var into = (byte*)pieces[g] + downOffset + blockAt;
                for (var i = 0; i < list.Length; i++)
                    Q4.Pack(new ReadOnlySpan<float>(_panel + (long)list[i] * Chunk, Chunk), new Span<byte>(into + (long)i * _downRowBytes, Q4.BlockBytes));
            }
        }
        return true;
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
        if (tensor.Type != GgmlType.PTQ1_0) throw new InvalidDataException($"{name} is {tensor.Type}; the index's pieces are gathered from PTQ1_0 rows.");
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
        FlashFile.Free((byte*)_panel);
        FlashFile.Free((byte*)_row);
        _file.Dispose();
    }
}

/// <summary>
/// What served a model's knowledge layers since it was last asked (<see cref="IndexedFeedForward.TakeCounts"/>): pieces
/// from step 4 in RAM; pieces step 2 read ahead of the layer that wanted them, and the bytes its read-ahead took; pieces
/// step 2 read only when a layer asked, and their bytes - of them, the ones step 2 holds no piece for, gathered from step
/// 1, the model's own file; the words that took the piece the word before left where it lay, which read nothing; and how
/// long the layers waited on step 2's read-ahead.
/// </summary>
public readonly record struct IndexCounts(long Warm, long Ahead, long AheadBytes, long Asked, long AskedBytes, long Again = 0,
    long FromModel = 0, TimeSpan Waited = default)
{
    /// <summary>What was read from flash for the pieces: ahead, and when asked.</summary>
    public long Bytes => AheadBytes + AskedBytes;
}

/// <summary>
/// STEP 4, AND HOW IT REACHES DOWN (the owner, 2026-11-15: "Only 4 ever reaches into 3, only 3 reaches into 2 and only 2
/// reaches into 1"). A word asks step 4 - the warm index in RAM, whole thoughts - for its piece. What step 4 does not
/// hold, step 3 names (the core index: the word's thought, found once at the first layer of its pass) and step 2 brings
/// from flash, read ahead: the pass's pieces in the order its layers will want them, coming in while the layers before
/// compute. What step 2 holds no piece for, it gathers from step 1, the model's own file.
/// </summary>
public sealed unsafe class IndexedFeedForward : IDisposable
{
    // A piece is promoted into RAM once this many words in a row have wanted it from flash: the most-used kept hot, as the
    // design asks, without warming a piece one word touched.
    private const int PromoteAfter = 2;

    /// <summary>The most slots step 2 reads ahead into.</summary>
    public const int MostAhead = 4;

    private readonly ThoughtIndex _core;
    private readonly CachedIndex? _pieces;     // step 2: the thoughts' pieces on flash
    private readonly GgufFile _model;
    private readonly ThoughtSketch _sketch;
    private readonly float[] _sketched = new float[ThoughtSketch.Width];
    private readonly Dictionary<long, Warm> _warm = [];     // step 4: a thought's piece held in RAM, by key
    private readonly Dictionary<long, int> _flashHits = []; // words that have wanted a piece from flash since it was last warm
    private readonly long _warmBudget;
    private long _clock;                   // ticks up on every warm read and promotion, so the least-recently-used can go
    private readonly long[] _usage;        // words that fell in each group since the last TakeUsage - what the night revises from
    private readonly FlashFile? _piecesFile;   // step 2, read when a layer asks
    private readonly FlashFile? _aheadFile;    // step 2, read ahead on the loader's own thread
    private readonly byte* _read;          // a piece read when a layer asked, for the word computing now
    private readonly long _pieceBytes, _weights, _scales;
    private readonly bool _withDown;       // whether a piece's ffn_down is read and held: not where the layers bring their own
    private readonly long _held;           // what of a piece is read from flash and held in RAM: all of it, or its gate and up
    private int[] _wordThought = [];       // coherent thoughts: each word's thought, found at the first layer of its pass
    private int[] _wordLayer = [];         // and the layer it was last served at, -1 before its first
    private long _heldKey = -1;            // the piece the last call gave, which the word computing now holds
    private bool _heldDown;                // whether it holds its down
    private bool _heldStored;              // whether it came from step 2 rather than gathered from the model's own file
    private IndexPiece? _heldView;         // the view over it
    private byte* _heldBytes;              // where it lies
    private Slot? _heldSlot;               // the read-ahead slot it lies in; null for _read
    private readonly int _channels, _readBytes;
    private RowGather? _gather;            // step 1, read the first time a thought has no piece in step 2
    private long _warmHits, _againHits, _aheadHits, _aheadRead, _askedReads, _askedBytes, _fromModel, _waitTicks;
    private long[]? _thoughtUsed;          // coherent: each thought's latest use among its warm pieces, while choosing one to let go

    // STEP 2'S READ-AHEAD: a ring of slots its loader reads the pass's plan into, in the order the layers will want it.
    private readonly Slot[] _ring;
    private readonly BlockingCollection<Slot>? _loads;
    private readonly Thread? _loader;
    private readonly object _gate = new();
    private readonly SortedSet<int> _passThoughts = [];   // the thoughts the pass's words found at its first layer
    private int _passFirst = -1;           // that layer
    private bool _planPending;             // thoughts found that no plan has been made for
    private long[] _plan = [];             // the pass's pieces, in the order the layers will want them
    private int _planNext;                 // the next of the plan a slot is given
    private int _generation;               // a plan's number: a slot given an earlier one's piece is not this pass's
    private int _disposed;

    // A thought's piece in RAM: the bytes it owns, a view over them, and when it was last used.
    private sealed class Warm(nint buffer, IndexPiece piece)
    {
        public nint Buffer { get; } = buffer;
        public IndexPiece Piece { get; } = piece;
        public long Used { get; set; }
    }

    // A slot step 2 reads a piece ahead into: the piece it holds or is reading, its place in the plan, whether the layer
    // computing holds it, and whether its read is in. A slot is given again only once its read is in.
    private sealed class Slot(long bytes)
    {
        public byte* Buffer = FlashFile.Allocate(bytes);
        public long Key = -1;
        public int At = -1;
        public int Generation = -1;
        public bool Taken;
        public IndexPiece? View;
        public Exception? Failed;
        public readonly ManualResetEventSlim Ready = new(true);
    }

    /// <summary>The slots step 2 reads ahead into where the caller does not say: one the layer computes from, one coming in.</summary>
    public const int DefaultAhead = 2;

    /// <summary>
    /// What one piece of <paramref name="core"/> takes in RAM, served from <paramref name="pieces"/>: all of it, or - with
    /// <paramref name="withDown"/> false - the whole pages its gate and up lie in. A read-ahead slot holds one.
    /// </summary>
    public static long HeldBytes(ThoughtIndex core, CachedIndex? pieces, bool withDown)
    {
        ArgumentNullException.ThrowIfNull(core);
        var piece = FlashFile.Pages(CachedIndex.PieceBytes(core.ListNeurons, core.Embedding));
        if (pieces is not null) piece = Math.Max(piece, pieces.PieceStride);
        var gateAndUp = 2 * (Ptq1PairMatrix.WeightBytes(core.ListNeurons, core.Embedding) + Ptq1PairMatrix.ScaleCount(core.ListNeurons, core.Embedding) * sizeof(float));
        return withDown ? piece : Math.Min(piece, FlashFile.Pages(gateAndUp));
    }

    /// <summary>
    /// Serves <paramref name="model"/>'s knowledge layers from step 4 in RAM, then step 2's <paramref name="pieces"/> on
    /// flash (null for none: every piece gathered from the model's own file). Step 4 holds whole thoughts within
    /// <paramref name="warmBudget"/> bytes - the most used, by the heat the night revised, then the ones the most of
    /// Watch's words fell in - and step 2 reads ahead into <paramref name="ahead"/> slots of its own beside them (none
    /// where a word's thought is not known before its layers want it: per-layer groups, or no pieces). With
    /// <paramref name="withDown"/> false the layers bring their own ffn_down, and a piece is read and held as its gate
    /// and up alone.
    /// </summary>
    public IndexedFeedForward(ThoughtIndex core, CachedIndex? pieces, GgufFile model, long warmBudget, int channels = 4, int readBytes = 1 << 20, bool withDown = true,
        int ahead = DefaultAhead)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(model);
        if (withDown && pieces is { HoldsDown: false })
            throw new ArgumentException("Step 2's pieces hold no ffn_down: the layers bring their own.", nameof(pieces));
        _core = core;
        _pieces = pieces;
        _model = model;
        _channels = Math.Max(1, channels);
        _readBytes = Math.Max(FlashFile.Page, readBytes);
        _sketch = new ThoughtSketch(core.Embedding);
        _weights = Ptq1PairMatrix.WeightBytes(core.ListNeurons, core.Embedding);
        _scales = Ptq1PairMatrix.ScaleCount(core.ListNeurons, core.Embedding) * sizeof(float);
        _pieceBytes = FlashFile.Pages(CachedIndex.PieceBytes(core.ListNeurons, core.Embedding));   // a piece gathered from step 1 holds its down
        if (pieces is not null) _pieceBytes = Math.Max(_pieceBytes, pieces.PieceStride);
        // A PIECE WITHOUT ITS DOWN (2026-11-15): its gate and up lead it, so a read of their whole pages brings them - and
        // step 4 holds twice the pieces in the same room.
        _withDown = withDown;
        _held = withDown ? _pieceBytes : Math.Min(_pieceBytes, FlashFile.Pages(2 * (_weights + _scales)));
        _usage = new long[core.Layers * core.Groups];
        _read = FlashFile.Allocate(_pieceBytes);
        _warmBudget = Math.Max(0, warmBudget);

        // STEP 2 READS AHEAD where a word's thought is known before its layers want it - coherent thoughts, found at the
        // first layer: two slots at least, one the layer computes from while the next comes in.
        var slots = core.Coherent && pieces is not null ? Math.Min(MostAhead, Math.Max(0, ahead)) : 0;
        if (slots < 2) slots = 0;
        _ring = new Slot[slots];
        for (var s = 0; s < slots; s++) _ring[s] = new Slot(_held);
        AheadBytesHeld = slots * _held;
        if (pieces is null) return;
        _piecesFile = FlashFile.Open(pieces.FilePath);
        if (slots > 0)
        {
            _aheadFile = FlashFile.Open(pieces.FilePath);
            _loads = new BlockingCollection<Slot>();
            _loader = new Thread(Load) { IsBackground = true, Name = "step-2-ahead" };
            _loader.Start();
        }

        // STEP 4: COHERENT PORTIONS OF STEP 2 SELECTED INTO RAM while the budget lasts - the thoughts the person's days used
        // most (the heat the night revised), then the ones the most of Watch's words fell in. WHOLE THOUGHTS (the owner,
        // 2026-11-15: "what we keep warm have to be coherent"): every layer of the hottest before any of the next.
        var started = Stopwatch.GetTimestamp();
        var hottest = core.Coherent
            ? Enumerable.Range(0, core.Groups)
                .OrderByDescending(g => Enumerable.Range(0, core.Layers).Sum(il => pieces.Heat(il, g)))
                .ThenByDescending(g => core.Size(0, g))
                .SelectMany(g => Enumerable.Range(0, core.Layers).Select(il => (Layer: il, Group: g)))
                .Where(p => pieces.Entry(p.Layer, p.Group) is not null)
            : Enumerable.Range(0, core.Layers).SelectMany(il => Enumerable.Range(0, core.Groups).Select(g => (Layer: il, Group: g)))
                .Where(p => pieces.Entry(p.Layer, p.Group) is not null)
                .OrderByDescending(p => pieces.Heat(p.Layer, p.Group))
                .ThenByDescending(p => core.Size(p.Layer, p.Group));
        foreach (var (layer, group) in hottest)
        {
            if (WarmBytes + _held > _warmBudget) break;
            var buffer = FlashFile.Allocate(_held);
            ReadFromStore(pieces, _piecesFile, layer, group, buffer);
            _warm[CachedIndex.Key(layer, group, core.Groups)] = new Warm((nint)buffer, View(layer, group, buffer)) { Used = ++_clock };
            WarmBytes += _held;
        }
        WarmElapsed = Stopwatch.GetElapsedTime(started);
    }

    /// <summary>What step 4 holds in RAM.</summary>
    public long WarmBytes { get; private set; }

    /// <summary>Pieces step 4 holds.</summary>
    public int WarmPieces => _warm.Count;

    /// <summary>Whether step 4 holds layer <paramref name="layer"/>'s group <paramref name="group"/>'s piece in RAM.</summary>
    public bool HoldsWarm(int layer, int group) => _warm.ContainsKey(CachedIndex.Key(layer, group, _core.Groups));

    /// <summary>Slots step 2 reads ahead into; none where a word's thought is not known before its layers want it.</summary>
    public int AheadSlots => _ring.Length;

    /// <summary>What the read-ahead's slots hold in RAM.</summary>
    public long AheadBytesHeld { get; }

    /// <summary>How long the warm index took to read in.</summary>
    public TimeSpan WarmElapsed { get; }

    /// <summary>The core index it serves from.</summary>
    public ThoughtIndex Core => _core;

    /// <summary>
    /// The piece for a word whose <paramref name="thought"/> enters layer <paramref name="layer"/>'s feed-forward: its group's
    /// (<see cref="GroupOf"/>, then <see cref="Piece(int, int, bool)"/>). <paramref name="word"/> is the word's place in the
    /// pass: 0 for a single word, its index in a prompt's batch.
    /// </summary>
    public IndexPiece Piece(int layer, ReadOnlySpan<float> thought, bool down = true, int word = 0) =>
        Piece(layer, GroupOf(layer, thought, word), down);

    /// <summary>
    /// Layer <paramref name="layer"/>'s group <paramref name="group"/>'s piece: from step 4 in RAM, else step 2's read-ahead,
    /// else step 2 read now, else - where step 2 holds no piece for it - gathered from the model's own file. A piece from
    /// flash is valid until the next call for another - and serves the next call for the same piece where it lies, so a
    /// prompt's words taken a group at a time read each piece once. With <paramref name="down"/> false the caller has the
    /// layer's own ffn_down and will not read the piece's: a piece gathered from the model's file then leaves it out.
    /// </summary>
    public IndexPiece Piece(int layer, int group, bool down = true)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (down && !_withDown) throw new InvalidOperationException("This index serves layers that bring their own ffn_down: its pieces are held without theirs.");
        if ((uint)layer >= (uint)_core.Layers) throw new ArgumentOutOfRangeException(nameof(layer), layer, $"The index holds {_core.Layers} layers.");
        if ((uint)group >= (uint)_core.Groups) throw new ArgumentOutOfRangeException(nameof(group), group, $"The index holds {_core.Groups} groups a layer.");
        var key = CachedIndex.Key(layer, group, _core.Groups);
        _usage[key]++;   // what the day used, for the night to revise from
        if (_warm.TryGetValue(key, out var warm))
        {
            warm.Used = ++_clock;
            _warmHits++;
            return warm.Piece;
        }

        // THE PIECE THE WORD BEFORE TOOK, WHERE IT LIES: the next word in the same group at this layer does not read it
        // from flash again. On the P30 on 2026-11-15 one list's pieces, too big for RAM, were read again for every one of
        // the check's 32 words - 20.7 GB for them.
        if (key == _heldKey && _heldView is { } again && (_heldDown || !down))
        {
            _againHits++;
            if (_heldStored) KeepHot(layer, group, key, _heldBytes);
            return again;
        }
        LetGoHeld();

        // STEP 2, READ AHEAD: the pass's plan made the first time a layer asks after its words found their thoughts.
        StartPlan();
        if (TakeAhead(key) is { } slot)
        {
            var ahead = Hold(key, slot.View!, _withDown, stored: true, slot.Buffer, slot);
            KeepHot(layer, group, key, slot.Buffer);
            return ahead;
        }

        // STEP 2, READ NOW: one read brings this thought's piece, not its layer.
        if (_pieces?.Entry(layer, group) is not null)
        {
            _askedBytes += ReadFromStore(_pieces, _piecesFile!, layer, group, _read);
            _askedReads++;
            var served = Hold(key, View(layer, group, _read), _withDown, stored: true, _read, slot: null);
            KeepHot(layer, group, key, _read);
            return served;
        }

        // STEP 2 HOLDS NO PIECE FOR IT (a phone short of flash): step 2 gathers the thought's rows from step 1, the
        // model's own file - its whole knowledge layer read for them.
        _gather ??= new RowGather(_model, _core.ListNeurons, _core.Embedding, _channels, _readBytes);
        var before = _gather.Read;
        _gather.Load(layer);
        new Span<byte>(_read, (int)_pieceBytes).Clear();
        _gather.Tile(_core.ListArray(layer, group), _read, down);
        _askedBytes += _gather.Read - before;
        _askedReads++;
        _fromModel++;
        return Hold(key, View(layer, group, _read), down, stored: false, _read, slot: null);
    }

    // What the word computing now holds, kept so the next word that wants it takes it where it lies - and whether it came
    // from step 2, the pieces kept hot (one gathered from the model's own file never is).
    private IndexPiece Hold(long key, IndexPiece view, bool down, bool stored, byte* bytes, Slot? slot)
    {
        _heldKey = key;
        _heldView = view;
        _heldDown = down;
        _heldStored = stored;
        _heldBytes = bytes;
        _heldSlot = slot;
        return view;
    }

    // The word has moved on: a read-ahead slot it held goes to the plan's next piece.
    private void LetGoHeld()
    {
        if (_heldSlot is { } slot) Release(slot);
        _heldSlot = null;
        _heldKey = -1;
        _heldView = null;
        _heldBytes = null;
    }

    // A PASS'S PLAN, made the first time a layer asks after the pass's words found their thoughts: every piece of those
    // thoughts from the pass's first layer to its last - a layer at a time, the thoughts in order, the order the layers
    // ask - that step 4 does not hold and step 2 does. The slots let the pass before go, their reads in first, and are
    // given the plan's first pieces at once.
    private void StartPlan()
    {
        if (!_planPending || _ring.Length == 0) return;
        _planPending = false;
        var keys = new List<long>();
        for (var il = Math.Max(0, _passFirst); il < _core.Layers; il++)
        {
            foreach (var g in _passThoughts)
            {
                var k = CachedIndex.Key(il, g, _core.Groups);
                if (!_warm.ContainsKey(k) && _pieces!.Entry(il, g) is not null) keys.Add(k);
            }
        }
        foreach (var slot in _ring) slot.Ready.Wait();
        lock (_gate)
        {
            _generation++;
            _plan = [.. keys];
            _planNext = 0;
            foreach (var slot in _ring)
            {
                slot.Taken = false;
                Assign(slot);
            }
        }
    }

    // Under _gate, its read in: the slot given the plan's next piece and its read asked of the loader; left empty once the
    // plan is all given.
    private void Assign(Slot slot)
    {
        slot.View = null;
        slot.Failed = null;
        slot.Generation = _generation;
        if (_planNext >= _plan.Length)
        {
            slot.Key = -1;
            slot.At = -1;
            return;
        }
        slot.Key = _plan[_planNext];
        slot.At = _planNext++;
        slot.Ready.Reset();
        _loads!.Add(slot);
    }

    // The slot holding `key`, its read in, taken for the layer computing now; null where the plan does not hold it. The
    // plan's pieces before it that the pass did not ask for - a layer it skipped - are let go, and where the pass jumped
    // ahead of what the slots were given, what it jumped is not read.
    private Slot? TakeAhead(long key)
    {
        if (_ring.Length == 0) return null;
        Slot? slot;
        lock (_gate)
        {
            var at = Array.IndexOf(_plan, key);
            if (at < 0) return null;
            slot = Array.Find(_ring, s => s.Key == key && s.Generation == _generation && !s.Taken);
            if (slot is null && _planNext < at) _planNext = at;
            foreach (var other in _ring)
            {
                if (other == slot || other.Taken || other.Key < 0 || other.Generation != _generation || other.At >= at) continue;
                other.Ready.Wait();
                Assign(other);
            }
            slot ??= Array.Find(_ring, s => s.Key == key && s.Generation == _generation && !s.Taken);
            if (slot is null) return null;
            slot.Taken = true;
        }
        var started = Stopwatch.GetTimestamp();
        slot.Ready.Wait();
        _waitTicks += Stopwatch.GetTimestamp() - started;
        if (slot.Failed is { } failed)
        {
            Release(slot);
            throw new IOException($"Step 2 could not read layer {key / _core.Groups}'s thought {key % _core.Groups} ahead.", failed);
        }
        _aheadHits++;
        return slot;
    }

    // A slot the layer computing let go of: given the plan's next piece, or left empty once the plan is all given.
    private void Release(Slot slot)
    {
        lock (_gate)
        {
            slot.Taken = false;
            if (slot.Generation != _generation)
            {
                slot.Key = -1;
                return;
            }
            Assign(slot);
        }
    }

    // THE LOADER: each slot's piece read from step 2 on its own thread, while the layers compute. It takes no lock: a slot
    // is given again only once its read is in.
    private void Load()
    {
        foreach (var slot in _loads!.GetConsumingEnumerable())
        {
            try
            {
                if (Volatile.Read(ref _disposed) != 0) continue;
                var layer = (int)(slot.Key / _core.Groups);
                var group = (int)(slot.Key % _core.Groups);
                Interlocked.Add(ref _aheadRead, ReadFromStore(_pieces!, _aheadFile!, layer, group, slot.Buffer));
                slot.View = View(layer, group, slot.Buffer);
            }
            catch (Exception ex)
            {
                slot.Failed = ex;
            }
            finally
            {
                slot.Ready.Set();
            }
        }
    }

    /// <summary>
    /// The group - the thought - a word whose <paramref name="thought"/> enters layer <paramref name="layer"/>'s
    /// feed-forward falls in: found from its sketch at every layer, or, where the index is coherent
    /// (<see cref="ThoughtIndex.Coherent"/>), at the first layer a pass brings the word to and kept through the rest of
    /// that pass. <paramref name="word"/> is the word's place in the pass: 0 for a single word, its index in a prompt's
    /// batch. Ask once a word a layer.
    /// </summary>
    public int GroupOf(int layer, ReadOnlySpan<float> thought, int word = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(word);
        if (!_core.Coherent)
        {
            _sketch.Sketch(thought, _sketched);
            return _core.Nearest(layer, _sketched);
        }
        if (word >= _wordLayer.Length)
        {
            var grown = Math.Max(word + 1, 2 * _wordLayer.Length);
            var at = _wordLayer.Length;
            Array.Resize(ref _wordLayer, grown);
            Array.Resize(ref _wordThought, grown);
            _wordLayer.AsSpan(at).Fill(-1);
        }
        // A NEW PASS where the layers start again: the word's thought found afresh, from the first layer it reaches - and
        // WHAT TO LOAD WHEN is known from here (the owner, 2026-11-15): the pass's thoughts, kept for step 2 to plan every
        // later layer's pieces from. A prompt's words find theirs at the same layer, its first word first.
        if (_wordLayer[word] < 0 || layer <= _wordLayer[word])
        {
            _sketch.Sketch(thought, _sketched);
            _wordThought[word] = _core.Nearest(0, _sketched);
            if (word == 0 || !_planPending || _passFirst != layer)
            {
                _passThoughts.Clear();
                _passFirst = layer;
                _planPending = true;
            }
            _passThoughts.Add(_wordThought[word]);
        }
        _wordLayer[word] = layer;
        return _wordThought[word];
    }

    // THE MOST-USED KEPT HOT: once PromoteAfter words in a row have wanted a piece from flash - read, or taken where the
    // word before left it - it is copied from where it lies (`from`) into RAM, the least-recently-used making room when the
    // budget is full: a warm piece, or for coherent thoughts a whole thought, every layer of it at once. A word already
    // warm never reaches here.
    private void KeepHot(int layer, int group, long key, byte* from)
    {
        if (_warmBudget < _held) return;              // no room in RAM for even one piece
        var hits = _flashHits.GetValueOrDefault(key) + 1;
        if (hits < PromoteAfter)
        {
            _flashHits[key] = hits;
            return;
        }
        while (WarmBytes + _held > _warmBudget && LetGoLeastRecentlyUsed(keep: group)) { }
        if (WarmBytes + _held > _warmBudget) return;  // nothing left to let go and still no room
        var buffer = FlashFile.Allocate(_held);
        Buffer.MemoryCopy(from, buffer, _held, _held);
        _warm[key] = new Warm((nint)buffer, View(layer, group, buffer)) { Used = ++_clock };
        WarmBytes += _held;
        _flashHits.Remove(key);
    }

    // Lets the least-recently-used warm piece go - or, for coherent thoughts, the least-recently-used thought other than
    // `keep`, all its layers: RAM holds whole thoughts, and the thought being warmed never gives up its own layers. False
    // when there was nothing to let go.
    private bool LetGoLeastRecentlyUsed(int keep)
    {
        if (_core.Coherent)
        {
            var used = _thoughtUsed ??= new long[_core.Groups];
            used.AsSpan().Fill(-1);
            foreach (var (k, w) in _warm)
            {
                var g = (int)(k % _core.Groups);
                if (g != keep && w.Used > used[g]) used[g] = w.Used;
            }
            var oldest = -1;
            for (var g = 0; g < used.Length; g++)
                if (used[g] >= 0 && (oldest < 0 || used[g] < used[oldest])) oldest = g;
            if (oldest < 0) return false;
            foreach (var k in _warm.Keys.Where(k => k % _core.Groups == oldest).ToList()) Free(k);
            return true;
        }

        var least = long.MaxValue;
        long leastKey = -1;
        foreach (var (k, w) in _warm)
        {
            if (w.Used >= least) continue;
            least = w.Used;
            leastKey = k;
        }
        if (leastKey < 0) return false;
        Free(leastKey);
        return true;
    }

    private void Free(long key)
    {
        FlashFile.Free((byte*)_warm[key].Buffer);
        _warm.Remove(key);
        WarmBytes -= _held;
    }

    /// <summary>
    /// What served since the last call (<see cref="IndexCounts"/>) - step 4, step 2 read ahead and read when asked, step 1
    /// through step 2, the words that took the piece the word before left, and the layers' wait on the read-ahead - and the
    /// counts start again.
    /// </summary>
    public IndexCounts TakeCounts()
    {
        var counts = new IndexCounts(_warmHits, _aheadHits, Interlocked.Exchange(ref _aheadRead, 0), _askedReads, _askedBytes, _againHits,
            _fromModel, Stopwatch.GetElapsedTime(0, _waitTicks));
        _warmHits = _againHits = _aheadHits = _askedReads = _askedBytes = _fromModel = _waitTicks = 0;
        return counts;
    }

    /// <summary>
    /// How many words fell in each group since the last call, by layer then group, the count starting again - the day's
    /// use of the index, which the night revises step 2's heat from (<see cref="CachedIndex.Revise"/>).
    /// </summary>
    public long[] TakeUsage()
    {
        var copy = (long[])_usage.Clone();
        Array.Clear(_usage);
        return copy;
    }

    // A group's piece read from a store into `into`: all of it, or - where the layers bring their own ffn_down - the whole
    // pages its gate and up lie in. The bytes read.
    private long ReadFromStore(CachedIndex store, FlashFile file, int layer, int group, byte* into)
    {
        var (offset, bytes) = store.Entry(layer, group)!.Value;
        var wanted = Math.Min(bytes, _held);
        if (file.ReadChannels(into, wanted, offset, _channels, _readBytes) < wanted)
            throw new InvalidDataException($"{store.FilePath} ends inside layer {layer}'s group {group}.");
        return wanted;
    }

    private IndexPiece View(int layer, int group, byte* piece) => new(
        _core.ListArray(layer, group),
        Ptq1PairMatrix.Over(piece, (float*)(piece + _weights), _core.ListNeurons, _core.Embedding),
        Ptq1PairMatrix.Over(piece + _weights + _scales, (float*)(piece + 2 * _weights + _scales), _core.ListNeurons, _core.Embedding),
        new DownPiece(piece + 2 * (_weights + _scales), _core.ListNeurons, _core.Embedding));

    /// <summary>Lets step 4, the read-ahead and every buffer go: RAM holds none of the index after this.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _loads?.CompleteAdding();
        _loader?.Join();
        foreach (var slot in _ring)
        {
            FlashFile.Free(slot.Buffer);
            slot.Buffer = null;
            slot.Ready.Dispose();
        }
        _loads?.Dispose();
        foreach (var warm in _warm.Values) FlashFile.Free((byte*)warm.Buffer);
        _warm.Clear();
        FlashFile.Free(_read);
        _gather?.Dispose();
        _piecesFile?.Dispose();
        _aheadFile?.Dispose();
    }
}
