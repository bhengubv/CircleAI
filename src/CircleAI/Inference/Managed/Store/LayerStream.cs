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
//
// FROM THE MODEL'S OWN FILE where its sets are read in place (TileStore.InPlace): each tensor read past the cache into a
// window and repacked into the slot - the bytes a written store holds - while the next comes into the other window, so
// flash keeps one copy of the weights, at the file's 1.75 bits a weight rather than a store's 2.25.
//
// THE MIXING ALONE where an index serves the knowledge layers (IndexedFeedForward): a word's ffn_gate, ffn_up and ffn_down
// then come as its pieces, and a stream opened with no feed-forward store reads none of them.

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

    private readonly TileStore _mixing;
    private readonly TileStore? _feedForward;   // null: the stream brings the mixing alone
    private readonly FlashFile _mixFile;
    private readonly FlashFile? _ffnFile;
    private readonly Place[] _places;
    private readonly Holder?[] _warm;   // by layer: its own buffer when warm
    private readonly Holder[] _ring;
    private readonly int[] _streamed;   // the layers that come through the ring, in pass order
    private readonly int _channels, _readBytes;
    private readonly byte* _window;     // one tensor of the model's file, for a set read in place; null otherwise
    private readonly byte* _ahead;      // the next tensor, read into it while the one in _window is repacked
    private readonly bool _runs;        // every set read in place: a layer's tensors are one run, and the next layer's follow on
    private readonly Runtime.WorkerPool? _repackers;   // the repack's own threads, for a stream that reads in place
    private readonly object _gate = new();
    private readonly BlockingCollection<(int Layer, Holder Holder)> _loads = new();
    private readonly Thread _loader;
    private int _cursor;                // the next of _streamed to give a slot
    private long _waitTicks, _loadedBytes, _loadTicks;
    private long _flashBytes, _flashTime, _repackTime;   // what the loads' time went to: flash (bytes, TimeSpan ticks), the CPU
    private LoadTimes? _first;          // the stream's first load, before any pass computed beside it; taken once
    private int _firstSeen;
    private int _disposed;

    /// <summary>
    /// What one load took: the tiles' bytes, the time it took whole, and - for a layer read in place - the bytes of the
    /// model's file read and the time reading and repacking took, side by side.
    /// </summary>
    public sealed record LoadTimes(long Bytes, TimeSpan Whole, long FromFlash, TimeSpan Reading, TimeSpan Repacking);

    /// <summary>
    /// Opens a stream of <paramref name="mixing"/>'s and <paramref name="feedForward"/>'s layers: as many held warm, from the
    /// first, as fit in <paramref name="warmBudget"/> bytes; the rest through <paramref name="slots"/> slots; every read in
    /// reads of <paramref name="readBytes"/> on <paramref name="channels"/> readers. The first loads start at once. With
    /// <paramref name="feedForward"/> null the stream brings the mixing alone - for a model whose knowledge layers an
    /// index serves.
    /// </summary>
    public LayerStream(TileStore mixing, TileStore? feedForward, int channels, int readBytes, long warmBudget, int slots)
    {
        ArgumentNullException.ThrowIfNull(mixing);
        if (feedForward is not null && mixing.Layers != feedForward.Layers) throw new ArgumentException("The stores hold different layers.", nameof(feedForward));
        _mixing = mixing;
        _feedForward = feedForward;
        _channels = Math.Max(1, channels);
        _readBytes = Math.Max(FlashFile.Page, readBytes);
        Layers = mixing.Layers;
        _places = new Place[Layers];
        for (var il = 0; il < Layers; il++)
        {
            var (ms, ml) = mixing.LayerRange(il);
            var (fs, fl) = feedForward?.LayerRange(il) ?? (0L, 0L);
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
        _ffnFile = feedForward is null ? null : FlashFile.Open(feedForward.FilePath);
        Direct = _mixFile.Direct && (_ffnFile?.Direct ?? true);
        InPlace = mixing.InPlace || feedForward is { InPlace: true };
        _runs = mixing.InPlace && (feedForward?.InPlace ?? true);

        // THE REPACK ON THREADS OF ITS OWN: the thread pool's are taken by the reads beside it, each blocked on flash.
        if (_runs) _repackers = new Runtime.WorkerPool(TileStore.RepackDegree);
        if (InPlace)
        {
            var most = Math.Max(mixing.InPlace ? mixing.MostSourceBytes : 0, feedForward is { InPlace: true } ? feedForward.MostSourceBytes : 0);
            _window = FlashFile.Allocate(most);
            _ahead = FlashFile.Allocate(most);
        }
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

    /// <summary>Whether a set is read from the model's own file and repacked as it lands (TileStore.InPlace).</summary>
    public bool InPlace { get; }

    /// <summary>Whether the stream brings each layer's feed-forward too; false when it brings the mixing alone.</summary>
    public bool BringsFeedForward => _feedForward is not null;

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

    /// <summary>
    /// What the time reading layers took went to since the last call (<see cref="TakeCounts"/> says all of it): the bytes
    /// read from flash - of the model's own file where a set is read in place - and the time reading them took, and the
    /// time repacking them into tiles took, which is the CPU's and none for a store's own tiles. A tensor is repacked beside
    /// the next one's read, so the two may come to more than the whole. Starts counting again.
    /// </summary>
    /// <remarks>
    /// "LAYERS: 3708 MB READ FROM FLASH IN 160786 MS (23 MB/S)" (the P30, 2026-11-15), on flash that reads 205 MB/s on four
    /// readers and a head store that streamed at 165 MB/s in the same turn. The time was the load's whole - each tensor read
    /// and then repacked, one after the other - and said nothing of which of the two it went to.
    /// </remarks>
    public (long FromFlash, TimeSpan Reading, TimeSpan Repacking) TakeParts() =>
        (Interlocked.Exchange(ref _flashBytes, 0), TimeSpan.FromTicks(Interlocked.Exchange(ref _flashTime, 0)), TimeSpan.FromTicks(Interlocked.Exchange(ref _repackTime, 0)));

    /// <summary>
    /// The stream's first load, once it is in - its first layer, read before any pass computed beside it - and then not
    /// again: what reading and repacking take with nothing else on the CPU, against what a pass's loads take beside the
    /// products. Null until then, and after it was taken.
    /// </summary>
    public LoadTimes? TakeFirstLoad() => Interlocked.Exchange(ref _first, null);

    /// <summary>The stream in a line, for the log.</summary>
    public string Describe() => (_ring.Length == 0
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"every one of {Layers} layers warm, {WarmBytes / 1048576.0:F0} MB")
        : string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{WarmLayers} of {Layers} layers warm ({WarmBytes / 1048576.0:F0} MB), {_streamed.Length} streamed ({StreamedBytes / 1048576.0:F0} MB a pass as tiles) through {Slots} slots of {SlotBytes / 1048576.0:F0} MB"))
        + (InPlace ? ", from the model's own file" : string.Empty);

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
    //
    // A LAYER READ IN PLACE IS ONE RUN WITH THE NEXT (the P30, 2026-11-15: a pass of the 9B read its 1534 MB in 11.2 s and
    // repacked them in 10.5 s, side by side, and its loads still took 14.6 s - at each of the 32 layers the first tensor's
    // read had no repack beside it, and the last one's repack no read). A layer's mixing and feed-forward are one run of
    // tensors, each repacked while the next comes in; and when the next load is already waiting, its first tensor comes
    // into the free window while this layer's last is repacked - so flash and the CPU go on from one layer to the next.
    private void Load()
    {
        try
        {
            while (true)
            {
                int layer;
                Holder holder;
                var carried = _carried;
                _carried = null;
                if (carried is not null) (layer, holder) = (carried.Layer, carried.Holder);
                else if (_loads.TryTake(out var taken, Timeout.Infinite)) (layer, holder) = taken;
                else break;   // the stream is going, and nothing is left to load
                var started = Stopwatch.GetTimestamp();
                (long Bytes, TimeSpan Reading, TimeSpan Repacking) parts = default;
                try
                {
                    var place = _places[layer];
                    if (_runs)
                    {
                        parts = RunInPlace(layer, holder, carried);
                        Account(parts);
                    }
                    else
                    {
                        Read(_mixFile, _mixing, layer, holder.Buffer, place.MixStart, place.MixLength);
                        if (_feedForward is not null) Read(_ffnFile!, _feedForward, layer, holder.Buffer + place.MixLength, place.FfnStart, place.FfnLength);
                    }
                    holder.Tiles = new LayerTiles(
                        Views(_mixing, layer, holder.Buffer, place.MixStart),
                        _feedForward is null
                            ? new Ptq1PairMatrix?[TileSet.FeedForward.Kinds.Count]
                            : Views(_feedForward, layer, holder.Buffer + place.MixLength, place.FfnStart));
                    holder.Failed = null;
                    Interlocked.Add(ref _loadedBytes, place.Bytes);
                }
                catch (Exception ex)
                {
                    holder.Failed = ex;
                }
                finally
                {
                    var took = Stopwatch.GetTimestamp() - started;
                    Interlocked.Add(ref _loadTicks, took);
                    // THE FIRST LOAD IS READ BEFORE ANY PASS COMPUTES BESIDE IT: kept, so what it took alone can be said.
                    if (holder.Failed is null && Interlocked.Exchange(ref _firstSeen, 1) == 0)
                        Volatile.Write(ref _first, new LoadTimes(_places[layer].Bytes, Stopwatch.GetElapsedTime(0, took), parts.Bytes, parts.Reading, parts.Repacking));
                    holder.Ready.Set();
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // The stream is going.
        }
    }

    // One tensor of a layer read in place, and where it lands.
    private readonly record struct Tensor(TileStore Store, FlashFile Source, TileStore.Entry Entry, nint Into, long Start);

    // A load the loader took from the queue early: its layer's tensors, the first read into Window beside the last repack of
    // the load before, what that read took (Stopwatch ticks) or what stopped it.
    private sealed record Carried(int Layer, Holder Holder, Tensor[] Tensors, nint Window, long ReadTicks, Exception? Failed);

    private Carried? _carried;   // the loader's own: the next load, taken early

    // A layer's tensors in pass order, its mixing then its feed-forward.
    private Tensor[] TensorsOf(int layer, Holder holder)
    {
        var place = _places[layer];
        var tensors = new List<Tensor>();
        foreach (var e in _mixing.LayerEntries(layer)) tensors.Add(new Tensor(_mixing, _mixFile, e, (nint)holder.Buffer, place.MixStart));
        if (_feedForward is not null)
            foreach (var e in _feedForward.LayerEntries(layer)) tensors.Add(new Tensor(_feedForward, _ffnFile!, e, (nint)(holder.Buffer + place.MixLength), place.FfnStart));
        return [.. tensors];
    }

    private long Fetch(Tensor t, nint window) => TileStore.Fetch(t.Source, t.Store.Set, t.Entry, (byte*)window, _channels, _readBytes);

    private long Pack(Tensor t, nint window) => TileStore.Pack(t.Entry, (byte*)window, (byte*)t.Into, t.Start, _repackers);

    // A layer read in place: each tensor repacked while the next is read - its first already in a window where the load
    // before read it (carried) - and, when the next load is waiting, that one's first read beside this one's last repack.
    // What it read from the model's file, and the time reading and repacking took.
    private (long Bytes, TimeSpan Reading, TimeSpan Repacking) RunInPlace(int layer, Holder holder, Carried? carried)
    {
        if (carried?.Failed is { } failed) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failed).Throw();
        var tensors = carried?.Tensors ?? TensorsOf(layer, holder);
        if (tensors.Length == 0) return (0, TimeSpan.Zero, TimeSpan.Zero);
        long bytes = 0, reading = carried?.ReadTicks ?? 0, repacking = 0;
        nint window = (nint)_window, ahead = (nint)_ahead;
        var here = carried?.Window ?? window;
        var there = here == window ? ahead : window;
        if (carried is null) reading += Fetch(tensors[0], here);
        for (var i = 0; i < tensors.Length; i++)
        {
            var t = tensors[i];
            bytes += TileStore.SourceSpan(t.Entry);
            var (from, to) = (here, there);
            long packed = 0, fetched = 0;
            if (i + 1 < tensors.Length)
            {
                var u = tensors[i + 1];
                Parallel.Invoke(() => packed = Pack(t, from), () => fetched = Fetch(u, to));
                reading += fetched;
                (here, there) = (there, here);
            }
            else if (_loads.TryTake(out var following))
            {
                // THE NEXT LAYER'S FIRST TENSOR BESIDE THIS ONE'S LAST REPACK. The load is kept before anything can fail,
                // so one taken from the queue is never lost; a read that failed goes with it, and fails it.
                var next = TensorsOf(following.Layer, following.Holder);
                Exception? stopped = null;
                try
                {
                    Parallel.Invoke(
                        () => packed = Pack(t, from),
                        () =>
                        {
                            try
                            {
                                if (next.Length > 0) fetched = Fetch(next[0], to);
                            }
                            catch (Exception ex)
                            {
                                stopped = ex;
                            }
                        });
                }
                finally
                {
                    _carried = new Carried(following.Layer, following.Holder, next, to, fetched, stopped);
                }
            }
            else
            {
                packed = Pack(t, from);
            }
            repacking += packed;
        }
        return (bytes, Stopwatch.GetElapsedTime(0, reading), Stopwatch.GetElapsedTime(0, repacking));
    }

    private void Read(FlashFile file, TileStore store, int layer, byte* into, long start, long length)
    {
        if (length == 0) return;
        if (store.InPlace)
        {
            Account(store.ReadLayer(file, layer, into, start, _window, _channels, _readBytes, _ahead));
            return;
        }
        var began = Stopwatch.GetTimestamp();
        var got = file.ReadChannels(into, length, store.DataOffset + start, _channels, _readBytes);
        Interlocked.Add(ref _flashTime, Stopwatch.GetElapsedTime(began).Ticks);
        Interlocked.Add(ref _flashBytes, got);
        if (got < length) throw new InvalidDataException($"{store.FilePath} ends inside its data.");
    }

    // What a read in place took, counted (TakeParts).
    private void Account((long Bytes, TimeSpan Reading, TimeSpan Repacking) read)
    {
        Interlocked.Add(ref _flashBytes, read.Bytes);
        Interlocked.Add(ref _flashTime, read.Reading.Ticks);
        Interlocked.Add(ref _repackTime, read.Repacking.Ticks);
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
        _ffnFile?.Dispose();
        _repackers?.Dispose();
        FlashFile.Free(_window);
        FlashFile.Free(_ahead);
    }
}
