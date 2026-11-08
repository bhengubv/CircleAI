// LayerStream.cs
//
// EVERY LAYER FROM FLASH, A LAYER AHEAD OF THE ONE COMPUTING. A dense model reads all of its layers for every word, and
// one bigger than the phone's RAM (the 27B: 6.8 GB of tiles against about 0.9 GB free on the P30) runs only if its layers
// come in from flash as the words need them. Each layer's tiles - its mixing and its feed-forward, each a contiguous
// range of its store (TileStore) - are read into a slot of a small ring on the device's channels while the layer before
// computes, and the slot is given back when the layer is done. The layers the recipe keeps warm (as many as fit beside
// the ring) are read once, in their turn, and stay while the stream lives; with room for every layer nothing streams at
// all, and the stream is the copy the model used to keep - read in on a turn, let go when the engine is idle.
//
// Loads go in pass order, warm and streamed alike, so the first layer computes as soon as it is in rather than when
// everything is. The same tiles through the same kernel: a streamed layer computes exactly what the copy did.

using System.Collections.Concurrent;
using System.Diagnostics;
using CircleAI.Inference.Managed.Kernels;

namespace CircleAI.Inference.Managed.Store;

/// <summary>One layer's tiles in RAM: its mixing and its feed-forward, by their sets' kinds (null where the layer has none).</summary>
public sealed class LayerTiles(Ptq1PairMatrix?[] mixing, Ptq1PairMatrix?[] feedForward)
{
    /// <summary>The mixing weights, by <see cref="TileSet.Mixing"/>'s kinds.</summary>
    public Ptq1PairMatrix?[] Mixing { get; } = mixing;

    /// <summary>The feed-forward's gate, up and down, by <see cref="TileSet.FeedForward"/>'s kinds.</summary>
    public Ptq1PairMatrix?[] FeedForward { get; } = feedForward;
}

/// <summary>A model's layers read from their stores on flash: warm ones held, the rest through a ring of slots, a layer ahead.</summary>
public sealed unsafe class LayerStream : IDisposable
{
    // Where a layer lies in each store: the start of its first tensor and the length of its tensors, in the stores' data.
    private readonly record struct Place(long MixStart, long MixLength, long FfnStart, long FfnLength)
    {
        public long Bytes => MixLength + FfnLength;
    }

    // A buffer a layer is read into: a warm layer's own, or a slot of the ring.
    private sealed class Holder(long bytes)
    {
        public byte* Buffer = FlashFile.Allocate(bytes);
        public int Layer = -1;
        public bool InUse;
        public LayerTiles? Tiles;
        public Exception? Failed;
        public readonly ManualResetEventSlim Ready = new(false);
    }

    private readonly TileStore _mixing, _feedForward;
    private readonly FlashFile _mixFile, _ffnFile;
    private readonly Place[] _places;
    private readonly Holder?[] _warm;   // by layer: its own buffer when warm
    private readonly Holder[] _ring;
    private readonly int[] _streamed;   // the layers that come through the ring, in pass order
    private readonly int _channels, _readBytes;
    private readonly object _gate = new();
    private readonly BlockingCollection<(int Layer, Holder Holder)> _loads = new();
    private readonly Thread _loader;
    private int _cursor;                // the next of _streamed to give a slot
    private long _waitTicks, _loadedBytes, _loadTicks;
    private int _disposed;

    /// <summary>
    /// Opens a stream of <paramref name="mixing"/>'s and <paramref name="feedForward"/>'s layers: as many held warm, from the
    /// first, as fit in <paramref name="warmBudget"/> bytes; the rest through <paramref name="slots"/> slots; every read in
    /// reads of <paramref name="readBytes"/> on <paramref name="channels"/> readers. The first loads start at once.
    /// </summary>
    public LayerStream(TileStore mixing, TileStore feedForward, int channels, int readBytes, long warmBudget, int slots)
    {
        ArgumentNullException.ThrowIfNull(mixing);
        ArgumentNullException.ThrowIfNull(feedForward);
        if (mixing.Layers != feedForward.Layers) throw new ArgumentException("The stores hold different layers.", nameof(feedForward));
        _mixing = mixing;
        _feedForward = feedForward;
        _channels = Math.Max(1, channels);
        _readBytes = Math.Max(FlashFile.Page, readBytes);
        Layers = mixing.Layers;
        _places = new Place[Layers];
        for (var il = 0; il < Layers; il++)
        {
            var (ms, ml) = mixing.LayerRange(il);
            var (fs, fl) = feedForward.LayerRange(il);
            _places[il] = new Place(ms, ml, fs, fl);
        }

        _warm = new Holder?[Layers];
        long warm = 0;
        for (var il = 0; il < Layers && warm + _places[il].Bytes <= warmBudget; il++)
        {
            _warm[il] = new Holder(_places[il].Bytes);
            warm += _places[il].Bytes;
            WarmLayers++;
        }
        WarmBytes = warm;
        _streamed = Enumerable.Range(0, Layers).Where(il => _warm[il] is null).ToArray();
        StreamedBytes = _streamed.Sum(il => _places[il].Bytes);
        SlotBytes = _streamed.Length == 0 ? 0 : _streamed.Max(il => _places[il].Bytes);
        _ring = new Holder[Math.Min(Math.Max(1, slots), _streamed.Length)];
        for (var s = 0; s < _ring.Length; s++) _ring[s] = new Holder(SlotBytes);

        _mixFile = FlashFile.Open(mixing.FilePath);
        _ffnFile = FlashFile.Open(feedForward.FilePath);
        Direct = _mixFile.Direct && _ffnFile.Direct;
        _loader = new Thread(Load) { IsBackground = true, Name = "layer-stream" };
        _loader.Start();

        // THE FIRST LOADS, IN PASS ORDER: every warm layer, and the ring's slots given the first streamed layers.
        var first = new List<(int Layer, Holder Holder)>();
        for (var il = 0; il < Layers; il++)
            if (_warm[il] is { } holder) first.Add((il, holder));
        for (var s = 0; s < _ring.Length; s++)
        {
            _ring[s].Layer = _streamed[s];
            first.Add((_streamed[s], _ring[s]));
        }
        _cursor = _ring.Length == 0 ? 0 : _ring.Length % _streamed.Length;
        foreach (var load in first.OrderBy(l => l.Layer))
        {
            load.Holder.Layer = load.Layer;
            _loads.Add(load);
        }
    }

    /// <summary>Layers in a pass.</summary>
    public int Layers { get; }

    /// <summary>Layers held warm while the stream lives.</summary>
    public int WarmLayers { get; }

    /// <summary>What the warm layers hold in RAM.</summary>
    public long WarmBytes { get; }

    /// <summary>What a pass reads from flash through the ring.</summary>
    public long StreamedBytes { get; }

    /// <summary>Slots in the ring; none when every layer is warm.</summary>
    public int Slots => _ring.Length;

    /// <summary>What one slot holds: the largest streamed layer.</summary>
    public long SlotBytes { get; }

    /// <summary>What the stream holds in RAM: its warm layers and its slots.</summary>
    public long Bytes => WarmBytes + Slots * SlotBytes;

    /// <summary>Whether its reads go past the OS's cache.</summary>
    public bool Direct { get; }

    /// <summary>Layer <paramref name="layer"/>'s tiles, once they are in RAM; give them back with <see cref="Release"/>.</summary>
    public LayerTiles Acquire(int layer)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if ((uint)layer >= (uint)Layers) throw new ArgumentOutOfRangeException(nameof(layer), layer, $"The stream holds {Layers} layers.");
        var started = Stopwatch.GetTimestamp();
        Holder holder;
        if (_warm[layer] is { } warm)
        {
            holder = warm;
        }
        else
        {
            holder = Slot(layer) ?? Rewind(layer);
        }
        holder.Ready.Wait();
        Interlocked.Add(ref _waitTicks, Stopwatch.GetTimestamp() - started);
        if (holder.Failed is { } failed) throw new IOException($"Layer {layer}'s tiles could not be read from flash.", failed);
        if (_warm[layer] is null)
            lock (_gate) holder.InUse = true;
        return holder.Tiles!;
    }

    /// <summary>Gives layer <paramref name="layer"/>'s tiles back: a streamed layer's slot goes to the next layer in the pass.</summary>
    public void Release(int layer)
    {
        if (Volatile.Read(ref _disposed) != 0 || (uint)layer >= (uint)Layers || _warm[layer] is not null) return;
        (int Layer, Holder Holder) next;
        lock (_gate)
        {
            var slot = Array.Find(_ring, s => s.Layer == layer && s.InUse);
            if (slot is null) return;
            slot.InUse = false;
            var following = _streamed[_cursor];
            _cursor = (_cursor + 1) % _streamed.Length;
            if (following == layer) return; // a slot for every streamed layer: it holds the one it will hold next
            slot.Tiles = null;
            slot.Ready.Reset();
            slot.Layer = following;
            next = (following, slot);
        }
        _loads.Add(next);
    }

    /// <summary>
    /// What the stream did since the last call - the time a layer was waited for, the bytes read and the time reading
    /// them took - and starts counting again.
    /// </summary>
    public (TimeSpan Waited, long Read, TimeSpan Reading) TakeCounts()
    {
        var waited = Interlocked.Exchange(ref _waitTicks, 0);
        var read = Interlocked.Exchange(ref _loadedBytes, 0);
        var reading = Interlocked.Exchange(ref _loadTicks, 0);
        return (Stopwatch.GetElapsedTime(0, waited), read, Stopwatch.GetElapsedTime(0, reading));
    }

    /// <summary>The stream in a line, for the log.</summary>
    public string Describe() => _ring.Length == 0
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"every one of {Layers} layers warm, {WarmBytes / 1048576.0:F0} MB")
        : string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{WarmLayers} of {Layers} layers warm ({WarmBytes / 1048576.0:F0} MB), {_streamed.Length} streamed ({StreamedBytes / 1048576.0:F0} MB a pass) through {Slots} slots of {SlotBytes / 1048576.0:F0} MB");

    // The slot that holds or is loading `layer`, free to be taken.
    private Holder? Slot(int layer)
    {
        lock (_gate) return Array.Find(_ring, s => s.Layer == layer && !s.InUse);
    }

    // A PASS THAT DID NOT GO IN ORDER (a turn stopped mid-pass): the loads in flight finish, and the ring starts again
    // from `layer`.
    private Holder Rewind(int layer)
    {
        foreach (var slot in _ring) slot.Ready.Wait();
        lock (_gate)
        {
            if (Array.Exists(_ring, s => s.InUse)) throw new InvalidOperationException("A layer of the stream is still in use; it is given back before another is taken.");
            var at = Array.IndexOf(_streamed, layer);
            for (var s = 0; s < _ring.Length; s++)
            {
                var slot = _ring[s];
                slot.Ready.Reset();
                slot.Tiles = null;
                slot.Failed = null;
                slot.Layer = _streamed[(at + s) % _streamed.Length];
            }
            _cursor = (at + _ring.Length) % _streamed.Length;
        }
        foreach (var slot in _ring) _loads.Add((slot.Layer, slot));
        return _ring[0];
    }

    // THE LOADER: each load in turn, the layer's mixing then its feed-forward, on the device's channels, and its views.
    private void Load()
    {
        try
        {
            foreach (var (layer, holder) in _loads.GetConsumingEnumerable())
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var place = _places[layer];
                    Read(_mixFile, _mixing, holder.Buffer, place.MixStart, place.MixLength);
                    Read(_ffnFile, _feedForward, holder.Buffer + place.MixLength, place.FfnStart, place.FfnLength);
                    holder.Tiles = new LayerTiles(
                        Views(_mixing, layer, holder.Buffer, place.MixStart),
                        Views(_feedForward, layer, holder.Buffer + place.MixLength, place.FfnStart));
                    holder.Failed = null;
                    Interlocked.Add(ref _loadedBytes, place.Bytes);
                }
                catch (Exception ex)
                {
                    holder.Failed = ex;
                }
                finally
                {
                    Interlocked.Add(ref _loadTicks, Stopwatch.GetTimestamp() - started);
                    holder.Ready.Set();
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // The stream is going.
        }
    }

    private void Read(FlashFile file, TileStore store, byte* into, long start, long length)
    {
        if (length == 0) return;
        var got = file.ReadChannels(into, length, store.DataOffset + start, _channels, _readBytes);
        if (got < length) throw new InvalidDataException($"{store.FilePath} ends inside its data.");
    }

    // A layer's tensors as views of where they landed: each at its offset from the layer's first.
    private static Ptq1PairMatrix?[] Views(TileStore store, int layer, byte* buffer, long start)
    {
        var views = new Ptq1PairMatrix?[store.Set.Kinds.Count];
        foreach (var e in store.LayerEntries(layer))
            views[e.Kind] = Ptq1PairMatrix.Over(buffer + (e.WeightsOffset - start), (float*)(buffer + (e.ScalesOffset - start)), e.Rows, e.Columns);
        return views;
    }

    /// <summary>Lets every layer go: RAM holds none of the stream after this.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _loads.CompleteAdding();
        _loader.Join();
        _loads.Dispose();
        foreach (var holder in _warm.Concat(_ring))
        {
            if (holder is null) continue;
            FlashFile.Free(holder.Buffer);
            holder.Buffer = null;
            holder.Tiles = null;
            holder.Ready.Dispose();
        }
        _mixFile.Dispose();
        _ffnFile.Dispose();
    }
}
