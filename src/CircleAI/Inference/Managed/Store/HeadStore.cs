// HeadStore.cs
//
// THE OUTPUT HEAD, STORED BY SCRIPT ON FLASH. Every word scores the whole vocabulary - 248,320 rows
// of the 2B, 111 MB, a quarter of what a word reads - to sample from its best twenty. A turn in
// English needs none of the Cyrillic, Chinese or Arabic rows. So the head's rows are written again,
// grouped by script, each group in chunks of whole pages, IN THE SHAPE THE KERNEL COMPUTES ON:
//
//   Rows     the rows as the model packs them, their block scales as floats: for the lookup-table kernels
//   Paired   the vector kernel's tiles (Ptq1PairMatrix): 16 rows at two bits a weight, and their scales
//
// and which token each row is. The index - the groups, and where each chunk is - sits in the same
// file. Everything lives on flash: a turn streams in the groups its conversation uses (its surface) on
// the device's channels, computes from them where they land, and lets them go when it ends. A model whose
// head is served this way keeps no copy of it in RAM at all.
//
// A surface's logits are the whole head's for every token in it, to the bit: the same rows through the
// same kernel, each summed in the same order (a row's sum is its own lane's, whatever rows share its tile).
// A token outside it is -inf. When the whole head's best twenty lie inside the surface, the sampler draws
// exactly what it would have from all of them.
//
// Layout, little-endian:
//   page 0     header: magic, version, packing, layout; the model it was cut from (its length, the head's
//              offset and bytes, the SHA-256 of the head's bytes); rows, row bytes, blocks a row, chunk
//              rows; group and chunk counts; where the index is
//   index      the groups (script, rows, first chunk, chunks), then the chunks (offset, rows, bytes)
//   chunks     each page-aligned and whole pages long:
//                Rows     rows x row bytes, rows x blocks floats, rows x token ids
//                Paired   rows / 16 tiles of blocks x 512 bytes, rows x blocks floats, rows x token ids,
//                         the rows a whole number of tiles - padding rows are token -1
//
// Written to a ".partial" file and renamed when whole, so a process killed mid-write leaves no store
// that looks finished.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Kernels;

namespace CircleAI.Inference.Managed.Store;

/// <summary>The shape a store keeps the head's rows in: the shape of the kernel that computes on them.</summary>
public enum HeadLayout
{
    /// <summary>The rows as the model packs them, and their block scales as floats: the lookup-table kernels.</summary>
    Rows = 0,

    /// <summary>The vector kernel's tiles of 16 rows (<see cref="Ptq1PairMatrix"/>), and their block scales.</summary>
    Paired = 1,
}

/// <summary>The rows of an output head the model scores in place of its whole table: chunks of rows, their scales, and which token each row is.</summary>
public interface IHeadRows
{
    /// <summary>The shape the rows are in.</summary>
    HeadLayout Layout { get; }

    /// <summary>Chunks of rows.</summary>
    int ChunkCount { get; }

    /// <summary>The most rows a chunk holds.</summary>
    int MostRows { get; }

    /// <summary>
    /// Chunk <paramref name="index"/>: its rows' bytes (in <see cref="Layout"/>), their block scales, and the token each
    /// row is - one per row, -1 for a padding row.
    /// </summary>
    void Chunk(int index, out ReadOnlySpan<byte> rows, out ReadOnlySpan<float> scales, out ReadOnlySpan<int> tokens);
}

/// <summary>A model's output head, its rows grouped by script, in a file on flash.</summary>
public sealed class HeadStore
{
    /// <summary>"CAIHEAD1", little-endian.</summary>
    public const ulong Magic = 0x3144_4145_4849_4143;

    /// <summary>The layout's version: 2 added the paired shape.</summary>
    public const int Version = 2;

    /// <summary>Rows a chunk holds: about 260-300 KB of a 2B's head, where flash reads run near full speed.</summary>
    public const int DefaultChunkRows = 512;

    private const string Embedding = "token_embd.weight";
    private const string Untied = "output.weight";
    private const int EntryBytes = 16;

    // THE TABLE THE HEAD SCORES WITH: its own where the model keeps one (the 27B's output.weight, untied), else the
    // embedding table read forward (the 2B, tied). The header records the head's offset, bytes and SHA-256, so a store
    // cut from one cannot pass for the other's.
    private static string HeadName(GgufFile model) => model.Tensor(Untied) is not null ? Untied : Embedding;

    /// <summary>A script's rows: how many, and the chunks that hold them.</summary>
    public sealed record Group(Script Script, int Rows, int FirstChunk, int Chunks);

    /// <summary>A chunk: where it is in the file, its rows (padding included), and its length in whole pages.</summary>
    public readonly record struct Chunk(long Offset, int Rows, int Bytes);

    private HeadStore(string path, GgmlType packing, HeadLayout layout, int rows, int rowBytes, int blocks, int chunkRows,
        IReadOnlyList<Group> groups, IReadOnlyList<Chunk> chunks, long bytes)
    {
        FilePath = path;
        Packing = packing;
        Layout = layout;
        Rows = rows;
        RowBytes = rowBytes;
        BlocksPerRow = blocks;
        ChunkRows = chunkRows;
        Groups = groups;
        Chunks = chunks;
        Bytes = bytes;
    }

    /// <summary>The file.</summary>
    public string FilePath { get; }

    /// <summary>How the model packs its rows: its ternary packing.</summary>
    public GgmlType Packing { get; }

    /// <summary>The shape the rows are kept in.</summary>
    public HeadLayout Layout { get; }

    /// <summary>Rows in all: the vocabulary.</summary>
    public int Rows { get; }

    /// <summary>Bytes a row takes as the model packs it.</summary>
    public int RowBytes { get; }

    /// <summary>Blocks in a row (128 weights each).</summary>
    public int BlocksPerRow { get; }

    /// <summary>Rows a full chunk holds.</summary>
    public int ChunkRows { get; }

    /// <summary>The scripts present, in <see cref="Script"/> order.</summary>
    public IReadOnlyList<Group> Groups { get; }

    /// <summary>Every chunk, group by group.</summary>
    public IReadOnlyList<Chunk> Chunks { get; }

    /// <summary>The file's length.</summary>
    public long Bytes { get; }

    /// <summary>
    /// Writes the store of <paramref name="model"/>'s head to <paramref name="path"/> in <paramref name="layout"/>: its
    /// rows grouped by <paramref name="scriptOf"/>, in chunks of <paramref name="chunkRows"/> (whole tiles of 16 for
    /// the paired shape). What it wrote and what it read of the model are dropped from the OS's cache afterwards.
    /// Returns the store, opened.
    /// </summary>
    public static HeadStore Write(GgufFile model, string path, Func<int, Script> scriptOf, int chunkRows = DefaultChunkRows,
        HeadLayout layout = HeadLayout.Rows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(scriptOf);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkRows, 1);
        var (tensor, packing, blockBytes) = HeadOf(model);
        if (layout == HeadLayout.Paired && (packing != GgmlType.PTQ1_0 || chunkRows % Ptq1PairMatrix.TileRows != 0))
            throw new ArgumentException($"The paired shape is PTQ1_0's, in chunks of whole {Ptq1PairMatrix.TileRows}-row tiles.", nameof(layout));
        var rows = checked((int)tensor.Rows);
        var cols = checked((int)tensor.RowLength);
        var blocks = cols / Ptq1.BlockWeights;
        var rowBytes = blocks * blockBytes;

        // The rows of each script, in token order; the scripts in their enum's order.
        var byScript = new Dictionary<Script, List<int>>();
        for (var token = 0; token < rows; token++)
        {
            var script = scriptOf(token);
            if (!byScript.TryGetValue(script, out var list)) byScript[script] = list = [];
            list.Add(token);
        }
        var order = byScript.Keys.OrderBy(s => s).ToArray();

        // The index first, so every chunk's place is known before one is written.
        var groups = new List<Group>();
        var pieces = new List<(int Group, int First, int Count)>();
        foreach (var script in order)
        {
            var tokens = byScript[script];
            groups.Add(new Group(script, tokens.Count, pieces.Count, (tokens.Count + chunkRows - 1) / chunkRows));
            for (var first = 0; first < tokens.Count; first += chunkRows)
                pieces.Add((groups.Count - 1, first, Math.Min(chunkRows, tokens.Count - first)));
        }
        var indexBytes = FlashFile.Pages((long)(groups.Count + pieces.Count) * EntryBytes);
        var offset = FlashFile.Page + indexBytes;
        var placed = new List<Chunk>(pieces.Count);
        foreach (var (_, _, count) in pieces)
        {
            var held = RowsHeld(count, layout);
            var bytes = checked((int)FlashFile.Pages(ChunkContent(held, rowBytes, blocks, layout)));
            placed.Add(new Chunk(offset, held, bytes));
            offset += bytes;
        }

        var head = model.Bytes(tensor);
        var hash = SHA256.HashData(head);
        var header = new byte[FlashFile.Page];
        WriteHeader(header, model, tensor, packing, layout, rows, rowBytes, blocks, chunkRows, groups.Count, placed.Count, hash, FlashFile.Page, indexBytes);
        var index = new byte[indexBytes];
        for (var g = 0; g < groups.Count; g++)
        {
            var at = index.AsSpan(g * EntryBytes, EntryBytes);
            at[0] = (byte)groups[g].Script;
            BinaryPrimitives.WriteInt32LittleEndian(at[4..], groups[g].Rows);
            BinaryPrimitives.WriteInt32LittleEndian(at[8..], groups[g].FirstChunk);
            BinaryPrimitives.WriteInt32LittleEndian(at[12..], groups[g].Chunks);
        }
        for (var c = 0; c < placed.Count; c++)
        {
            var at = index.AsSpan((groups.Count + c) * EntryBytes, EntryBytes);
            BinaryPrimitives.WriteInt64LittleEndian(at, placed[c].Offset);
            BinaryPrimitives.WriteInt32LittleEndian(at[8..], placed[c].Rows);
            BinaryPrimitives.WriteInt32LittleEndian(at[12..], placed[c].Bytes);
        }

        var partial = path + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                stream.Write(header);
                stream.Write(index);
                var buffer = new byte[placed.Count == 0 ? 0 : placed.Max(c => c.Bytes)];
                var gathered = new byte[layout == HeadLayout.Paired ? (long)chunkRows * rowBytes : 0];
                for (var c = 0; c < pieces.Count; c++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (group, first, count) = pieces[c];
                    var chunk = buffer.AsSpan(0, placed[c].Bytes);
                    chunk.Clear();
                    Fill(chunk, head, byScript[groups[group].Script], first, count, placed[c].Rows, rowBytes, cols, packing, layout, gathered);
                    stream.Write(chunk);
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(partial, path, overwrite: true);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }

        // On flash now, and only there: neither what was written nor what was read stays in RAM.
        using (var written = FlashFile.Open(path, direct: false)) written.Drop(0, written.Length);
        ReleaseMapped(model);
        return Open(model, path, layout) ?? throw new InvalidDataException($"{path} was written and does not read back as {model.FilePath}'s head.");
    }

    /// <summary>Why <paramref name="model"/>'s head cannot be stored by script; null when it can.</summary>
    public static string? Unreadable(GgufFile model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var name = HeadName(model);
        var tensor = model.Tensor(name);
        if (tensor is null) return $"the model has no {name}";
        if (tensor.Type is not (GgmlType.PTQ1_0 or GgmlType.PQ2_0)) return $"{name} is {tensor.Type}";
        return tensor.RowLength % Ptq1.BlockWeights == 0 && tensor.ByteCount > 0 ? null : $"{name} is not whole 128-weight blocks";
    }

    /// <summary>
    /// Lets the OS take back the pages of <paramref name="model"/>'s head that its mapping holds: the store holds the
    /// head now, and a token's own row is read back in when it is looked up. False where there are no Linux calls.
    /// </summary>
    public static unsafe bool ReleaseMapped(GgufFile model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var tensor = model.Tensor(HeadName(model));
        return tensor is { ByteCount: > 0 } && FlashFile.Release(model.Pointer(tensor), tensor.ByteCount, model.FilePath, tensor.Offset);
    }

    /// <summary>
    /// The store at <paramref name="path"/> when it was cut from <paramref name="model"/>'s head in <paramref name="layout"/>;
    /// null when there is none, or it is another model's, another shape's or another version's. Reads its header and index.
    /// </summary>
    public static HeadStore? Open(GgufFile model, string path, HeadLayout layout = HeadLayout.Rows)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!File.Exists(path)) return null;
        var (tensor, packing, blockBytes) = HeadOf(model);
        var rows = checked((int)tensor.Rows);
        var blocks = checked((int)tensor.RowLength) / Ptq1.BlockWeights;
        var rowBytes = blocks * blockBytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 12);
            var header = new byte[FlashFile.Page];
            stream.ReadExactly(header);
            var h = header.AsSpan();
            if (BinaryPrimitives.ReadUInt64LittleEndian(h) != Magic || BinaryPrimitives.ReadInt32LittleEndian(h[8..]) != Version) return null;
            if (BinaryPrimitives.ReadInt32LittleEndian(h[12..]) != (int)packing
                || BinaryPrimitives.ReadInt32LittleEndian(h[112..]) != (int)layout
                || BinaryPrimitives.ReadInt64LittleEndian(h[16..]) != model.Length
                || BinaryPrimitives.ReadInt64LittleEndian(h[24..]) != tensor.Offset
                || BinaryPrimitives.ReadInt64LittleEndian(h[32..]) != tensor.ByteCount
                || BinaryPrimitives.ReadInt32LittleEndian(h[40..]) != rows
                || BinaryPrimitives.ReadInt32LittleEndian(h[44..]) != rowBytes
                || BinaryPrimitives.ReadInt32LittleEndian(h[48..]) != blocks)
                return null;
            var chunkRows = BinaryPrimitives.ReadInt32LittleEndian(h[52..]);
            var groupCount = BinaryPrimitives.ReadInt32LittleEndian(h[56..]);
            var chunkCount = BinaryPrimitives.ReadInt32LittleEndian(h[60..]);
            var indexOffset = BinaryPrimitives.ReadInt64LittleEndian(h[96..]);
            var indexBytes = BinaryPrimitives.ReadInt64LittleEndian(h[104..]);
            if (chunkRows < 1 || groupCount < 0 || chunkCount < 0 || indexBytes < (long)(groupCount + chunkCount) * EntryBytes || indexBytes > int.MaxValue)
                return null;

            var index = new byte[indexBytes];
            stream.Position = indexOffset;
            stream.ReadExactly(index);
            var groups = new List<Group>(groupCount);
            for (var g = 0; g < groupCount; g++)
            {
                var at = index.AsSpan(g * EntryBytes, EntryBytes);
                groups.Add(new Group((Script)at[0], BinaryPrimitives.ReadInt32LittleEndian(at[4..]),
                    BinaryPrimitives.ReadInt32LittleEndian(at[8..]), BinaryPrimitives.ReadInt32LittleEndian(at[12..])));
            }
            var chunks = new List<Chunk>(chunkCount);
            for (var c = 0; c < chunkCount; c++)
            {
                var at = index.AsSpan((groupCount + c) * EntryBytes, EntryBytes);
                chunks.Add(new Chunk(BinaryPrimitives.ReadInt64LittleEndian(at), BinaryPrimitives.ReadInt32LittleEndian(at[8..]),
                    BinaryPrimitives.ReadInt32LittleEndian(at[12..])));
            }
            if (groups.Sum(g => g.Rows) != rows) return null;
            if (layout == HeadLayout.Rows ? chunks.Sum(c => (long)c.Rows) != rows : chunks.Any(c => c.Rows % Ptq1PairMatrix.TileRows != 0)) return null;
            if (chunks.Count > 0 && chunks[^1].Offset + chunks[^1].Bytes > stream.Length) return null;
            return new HeadStore(path, packing, layout, rows, rowBytes, blocks, chunkRows, groups, chunks, stream.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// How many of the store's rows differ from <paramref name="model"/>'s head - a row whose bytes, scales or token are
    /// wrong (in the paired shape, every row of a chunk that does not repack to the same tiles), or a token held twice
    /// or not at all; 0 for a faithful store. Reads every chunk, and lets each go after.
    /// </summary>
    public unsafe long Verify(GgufFile model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var (tensor, _, _) = HeadOf(model);
        var head = model.Bytes(tensor);
        var cols = BlocksPerRow * Ptq1.BlockWeights;
        var seen = new bool[Rows];
        long wrong = 0;
        using var file = FlashFile.Open(FilePath);
        var most = Chunks.Count == 0 ? FlashFile.Page : Chunks.Max(c => c.Bytes);
        var buffer = FlashFile.Allocate(most);
        try
        {
            var expected = new byte[most];
            var gathered = new byte[Layout == HeadLayout.Paired ? (long)(Chunks.Count == 0 ? 0 : Chunks.Max(c => c.Rows)) * RowBytes : 0];
            foreach (var chunk in Chunks)
            {
                var content = (int)ChunkContent(chunk.Rows, RowBytes, BlocksPerRow, Layout);
                if (file.Read(buffer, chunk.Bytes, chunk.Offset) < content) return Rows;
                file.Drop(chunk.Offset, chunk.Bytes);
                var data = new ReadOnlySpan<byte>(buffer, content);
                var tokens = MemoryMarshal.Cast<byte, int>(data[(content - chunk.Rows * sizeof(int))..]);

                // Which tokens the chunk holds: each once, in a real row, and padding only at its end.
                var real = new List<int>(chunk.Rows);
                for (var r = 0; r < chunk.Rows; r++)
                {
                    var token = tokens[r];
                    if (token < 0) continue;
                    if (token >= Rows || seen[token]) { wrong++; continue; }
                    seen[token] = true;
                    real.Add(token);
                }

                // The same tokens' rows, written again from the model, must give the same bytes.
                var again = expected.AsSpan(0, content);
                again.Clear();
                Fill(again, head, real, 0, real.Count, chunk.Rows, RowBytes, cols, Packing, Layout, gathered);
                if (Layout == HeadLayout.Rows)
                {
                    var scaleStart = chunk.Rows * RowBytes;
                    for (var r = 0; r < real.Count; r++)
                    {
                        var same = data.Slice(r * RowBytes, RowBytes).SequenceEqual(again.Slice(r * RowBytes, RowBytes))
                            && data.Slice(scaleStart + r * BlocksPerRow * sizeof(float), BlocksPerRow * sizeof(float))
                                   .SequenceEqual(again.Slice(scaleStart + r * BlocksPerRow * sizeof(float), BlocksPerRow * sizeof(float)));
                        if (!same) wrong++;
                    }
                }
                else if (!data.SequenceEqual(again))
                {
                    wrong += real.Count;
                }
            }
        }
        finally
        {
            FlashFile.Free(buffer);
        }
        foreach (var held in seen)
        {
            if (!held) wrong++;
        }
        return wrong;
    }

    /// <summary>The groups a turn that writes in <paramref name="scripts"/> reads: those, and the special and neutral rows every turn needs.</summary>
    public IReadOnlyList<Group> Surface(IEnumerable<Script> scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        var wanted = new HashSet<Script>(scripts) { Script.Special, Script.Neutral };
        return Groups.Where(g => wanted.Contains(g.Script)).ToList();
    }

    /// <summary>
    /// Streams <paramref name="groups"/>' chunks in from flash on <paramref name="channels"/> readers at once, into one
    /// buffer the surface owns: the turn's head until it is disposed.
    /// </summary>
    public unsafe HeadSurface Load(IReadOnlyList<Group> groups, int channels)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var started = Stopwatch.GetTimestamp();
        var wanted = groups.SelectMany(g => Enumerable.Range(g.FirstChunk, g.Chunks)).Select(c => Chunks[c]).ToArray();
        var places = new long[wanted.Length];
        long total = 0;
        for (var i = 0; i < wanted.Length; i++)
        {
            places[i] = total;
            total += wanted[i].Bytes;
        }

        var buffer = FlashFile.Allocate(total);
        var file = FlashFile.Open(FilePath);
        try
        {
            var next = -1;
            Exception? failed = null;
            void Read()
            {
                try
                {
                    int i;
                    while ((i = Interlocked.Increment(ref next)) < wanted.Length && Volatile.Read(ref failed) is null)
                    {
                        var chunk = wanted[i];
                        if (file.Read(buffer + places[i], chunk.Bytes, chunk.Offset) < ChunkContent(chunk.Rows, RowBytes, BlocksPerRow, Layout))
                            throw new InvalidDataException($"{FilePath} ends inside chunk {i}.");
                        file.Drop(chunk.Offset, chunk.Bytes);
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref failed, ex, null);
                }
            }

            var readers = Math.Clamp(channels, 1, Math.Max(1, wanted.Length));
            var threads = new Thread[readers - 1];
            for (var t = 0; t < threads.Length; t++)
            {
                threads[t] = new Thread(Read) { IsBackground = true, Name = $"head-channel-{t + 1}" };
                threads[t].Start();
            }
            Read();
            foreach (var thread in threads) thread.Join();
            if (failed is not null) throw failed;

            return new HeadSurface(buffer, total, wanted, places, RowBytes, BlocksPerRow, Layout, groups, readers, file.Direct,
                Stopwatch.GetElapsedTime(started));
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

    /// <summary>
    /// <paramref name="groups"/>' chunks read from flash as the scoring reaches them, a window at a time in reads of
    /// <paramref name="readBytes"/> on <paramref name="channels"/> readers - for a trunk that streams its layers, whose
    /// RAM cannot spare the surface. The first window is on its way when this returns.
    /// </summary>
    public HeadStream Stream(IReadOnlyList<Group> groups, int channels, int readBytes, long windowBytes = HeadStream.DefaultWindowBytes)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowBytes, FlashFile.Page);
        var wanted = groups.SelectMany(g => Enumerable.Range(g.FirstChunk, g.Chunks)).Select(c => Chunks[c]).ToArray();
        return new HeadStream(this, wanted, groups, channels, readBytes, windowBytes);
    }

    // ---- the parts ----

    // The rows a chunk of `count` holds in a layout: whole tiles for the paired shape.
    private static int RowsHeld(int count, HeadLayout layout) =>
        layout == HeadLayout.Paired ? (count + Ptq1PairMatrix.TileRows - 1) / Ptq1PairMatrix.TileRows * Ptq1PairMatrix.TileRows : count;

    // A chunk's content before its padding to pages: its rows' bytes, their scales, their tokens.
    internal static long ChunkContent(int rows, int rowBytes, int blocks, HeadLayout layout)
    {
        var weights = layout == HeadLayout.Paired ? Ptq1PairMatrix.WeightBytes(rows, blocks * Ptq1.BlockWeights) : (long)rows * rowBytes;
        return weights + (long)rows * blocks * sizeof(float) + (long)rows * sizeof(int);
    }

    private static (GgufTensor Tensor, GgmlType Packing, int BlockBytes) HeadOf(GgufFile model)
    {
        var name = HeadName(model);
        var tensor = model.Tensor(name) ?? throw new InvalidDataException($"The model has no {name}.");
        var blockBytes = tensor.Type switch
        {
            GgmlType.PTQ1_0 => Ptq1.BlockBytes,
            GgmlType.PQ2_0 => Pq2.BlockBytes,
            _ => throw new NotSupportedException($"{name} is {tensor.Type}; the head is stored for PTQ1_0 and PQ2_0."),
        };
        if (tensor.RowLength % Ptq1.BlockWeights != 0 || tensor.ByteCount < 0) throw new InvalidDataException($"{name} is not whole 128-weight blocks.");
        return (tensor, tensor.Type, blockBytes);
    }

    // One chunk's content: tokens [first, first + count) of `tokens`, in `held` rows (the rest padding, token -1),
    // in the layout's shape, then their scales and their tokens.
    private static void Fill(Span<byte> chunk, ReadOnlySpan<byte> head, List<int> tokens, int first, int count, int held,
        int rowBytes, int cols, GgmlType packing, HeadLayout layout, byte[] gathered)
    {
        var blocks = cols / Ptq1.BlockWeights;
        var weightBytes = layout == HeadLayout.Paired ? (int)Ptq1PairMatrix.WeightBytes(held, cols) : held * rowBytes;
        var scales = MemoryMarshal.Cast<byte, float>(chunk.Slice(weightBytes, held * blocks * sizeof(float)));
        var ids = MemoryMarshal.Cast<byte, int>(chunk.Slice(weightBytes + held * blocks * sizeof(float), held * sizeof(int)));
        ids.Fill(-1);
        if (layout == HeadLayout.Paired)
        {
            // The chunk's rows end to end, the padding rows zero (a zero scale), then the vector kernel's repack.
            var rows = gathered.AsSpan(0, held * rowBytes);
            rows.Clear();
            for (var r = 0; r < count; r++)
            {
                var token = tokens[first + r];
                head.Slice(token * rowBytes, rowBytes).CopyTo(rows.Slice(r * rowBytes, rowBytes));
                ids[r] = token;
            }
            Ptq1PairMatrix.Repack(rows, held, cols, chunk[..weightBytes], scales);
            return;
        }
        for (var r = 0; r < count; r++)
        {
            var token = tokens[first + r];
            var row = head.Slice(token * rowBytes, rowBytes);
            row.CopyTo(chunk.Slice(r * rowBytes, rowBytes));
            Scales(row, blocks, packing, scales.Slice(r * blocks, blocks));
            ids[r] = token;
        }
    }

    // A row's block scales as floats, as the kernels read them.
    private static void Scales(ReadOnlySpan<byte> row, int blocks, GgmlType packing, Span<float> into)
    {
        var blockBytes = packing == GgmlType.PQ2_0 ? Pq2.BlockBytes : Ptq1.BlockBytes;
        for (var b = 0; b < blocks; b++)
        {
            var block = row.Slice(b * blockBytes, blockBytes);
            into[b] = packing == GgmlType.PQ2_0 ? Pq2.Scale(block) : Ptq1.Scale(block);
        }
    }

    private static void WriteHeader(Span<byte> h, GgufFile model, GgufTensor tensor, GgmlType packing, HeadLayout layout, int rows, int rowBytes,
        int blocks, int chunkRows, int groups, int chunks, byte[] hash, long indexOffset, long indexBytes)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(h, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(h[12..], (int)packing);
        BinaryPrimitives.WriteInt64LittleEndian(h[16..], model.Length);
        BinaryPrimitives.WriteInt64LittleEndian(h[24..], tensor.Offset);
        BinaryPrimitives.WriteInt64LittleEndian(h[32..], tensor.ByteCount);
        BinaryPrimitives.WriteInt32LittleEndian(h[40..], rows);
        BinaryPrimitives.WriteInt32LittleEndian(h[44..], rowBytes);
        BinaryPrimitives.WriteInt32LittleEndian(h[48..], blocks);
        BinaryPrimitives.WriteInt32LittleEndian(h[52..], chunkRows);
        BinaryPrimitives.WriteInt32LittleEndian(h[56..], groups);
        BinaryPrimitives.WriteInt32LittleEndian(h[60..], chunks);
        hash.CopyTo(h[64..]);
        BinaryPrimitives.WriteInt64LittleEndian(h[96..], indexOffset);
        BinaryPrimitives.WriteInt64LittleEndian(h[104..], indexBytes);
        BinaryPrimitives.WriteInt32LittleEndian(h[112..], (int)layout);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Overwritten by the next write.
        }
    }
}

/// <summary>
/// The rows of the head a turn reads, streamed in from its <see cref="HeadStore"/> and held until the turn ends:
/// dispose it then, and RAM holds none of the head.
/// </summary>
public sealed unsafe class HeadSurface : IHeadRows, IDisposable
{
    private readonly HeadStore.Chunk[] _chunks;
    private readonly long[] _places;
    private readonly int _rowBytes;
    private readonly int _blocks;
    private byte* _buffer;

    internal HeadSurface(byte* buffer, long bytes, HeadStore.Chunk[] chunks, long[] places, int rowBytes, int blocks, HeadLayout layout,
        IReadOnlyList<HeadStore.Group> groups, int channels, bool direct, TimeSpan elapsed)
    {
        _buffer = buffer;
        Bytes = bytes;
        _chunks = chunks;
        _places = places;
        _rowBytes = rowBytes;
        _blocks = blocks;
        Layout = layout;
        Groups = groups;
        Channels = channels;
        Direct = direct;
        Elapsed = elapsed;
        Rows = groups.Sum(g => g.Rows);
        MostRows = chunks.Length == 0 ? 0 : chunks.Max(c => c.Rows);
    }

    /// <inheritdoc/>
    public HeadLayout Layout { get; }

    /// <summary>The scripts it holds.</summary>
    public IReadOnlyList<HeadStore.Group> Groups { get; }

    /// <summary>Tokens it holds (padding rows not counted).</summary>
    public int Rows { get; }

    /// <summary>Bytes it holds in RAM, padding included.</summary>
    public long Bytes { get; }

    /// <summary>Readers it was streamed in on.</summary>
    public int Channels { get; }

    /// <summary>Whether the reads went past the OS's cache.</summary>
    public bool Direct { get; }

    /// <summary>How long it took to stream in.</summary>
    public TimeSpan Elapsed { get; }

    /// <inheritdoc/>
    public int ChunkCount => _chunks.Length;

    /// <inheritdoc/>
    public int MostRows { get; }

    /// <inheritdoc/>
    public void Chunk(int index, out ReadOnlySpan<byte> rows, out ReadOnlySpan<float> scales, out ReadOnlySpan<int> tokens)
    {
        ObjectDisposedException.ThrowIf(_buffer == null, this);
        var chunk = _chunks[index];
        var start = _buffer + _places[index];
        var cols = _blocks * Ptq1.BlockWeights;
        var weightBytes = Layout == HeadLayout.Paired ? Ptq1PairMatrix.WeightBytes(chunk.Rows, cols) : (long)chunk.Rows * _rowBytes;
        rows = new ReadOnlySpan<byte>(start, checked((int)weightBytes));
        scales = new ReadOnlySpan<float>(start + weightBytes, chunk.Rows * _blocks);
        tokens = new ReadOnlySpan<int>(start + weightBytes + (long)chunk.Rows * _blocks * sizeof(float), chunk.Rows);
    }

    /// <summary>Lets the rows go: RAM holds none of them after this.</summary>
    public void Dispose()
    {
        var buffer = _buffer;
        _buffer = null;
        FlashFile.Free(buffer);
    }
}

/// <summary>
/// A surface's rows read from the store as a head's scoring reaches them, for a trunk that streams every layer from flash:
/// windows of consecutive chunks read past the cache on the device's channels, the next window read while the one before
/// is scored, and after the last the first again, for the next word. RAM holds two windows, not the surface - for the 27B
/// 16 MB, not 198 - and a word reads the surface once more, about a second beside the 30 of its layers.
/// </summary>
public sealed unsafe class HeadStream : IHeadRows, IDisposable
{
    /// <summary>The most a window reads at once: consecutive chunks up to this many bytes.</summary>
    public const long DefaultWindowBytes = 8L << 20;

    private readonly HeadStore.Chunk[] _chunks;
    private readonly int _rowBytes;
    private readonly int _blocks;
    private readonly (long Offset, long Bytes)[] _windows; // consecutive chunks lying end to end in the file
    private readonly int[] _windowOf;
    private readonly long[] _placeInWindow;
    private readonly FlashFile _file;
    private readonly int _channels;
    private readonly int _readBytes;
    private readonly byte*[] _buffers;
    private readonly int[] _holds = [-1, -1];   // the window each buffer holds whole
    private Task? _reading;                      // at most one read in flight, into _readingSlot
    private int _readingWindow = -1;
    private int _readingSlot = -1;
    private long _read;
    private long _readTicks;
    private int _disposed;

    internal HeadStream(HeadStore store, HeadStore.Chunk[] chunks, IReadOnlyList<HeadStore.Group> groups, int channels, int readBytes, long windowBytes)
    {
        _chunks = chunks;
        _rowBytes = store.RowBytes;
        _blocks = store.BlocksPerRow;
        Layout = store.Layout;
        Groups = groups;
        Rows = groups.Sum(g => g.Rows);
        MostRows = chunks.Length == 0 ? 0 : chunks.Max(c => c.Rows);
        _channels = Math.Max(1, channels);
        _readBytes = Math.Max(FlashFile.Page, readBytes);

        var windows = new List<(long, long)>();
        _windowOf = new int[chunks.Length];
        _placeInWindow = new long[chunks.Length];
        var first = 0;
        while (first < chunks.Length)
        {
            var end = first + 1;
            var bytes = (long)chunks[first].Bytes;
            while (end < chunks.Length && chunks[end].Offset == chunks[end - 1].Offset + chunks[end - 1].Bytes && bytes + chunks[end].Bytes <= windowBytes)
            {
                bytes += chunks[end].Bytes;
                end++;
            }
            long place = 0;
            for (var c = first; c < end; c++)
            {
                _windowOf[c] = windows.Count;
                _placeInWindow[c] = place;
                place += chunks[c].Bytes;
            }
            windows.Add((chunks[first].Offset, bytes));
            first = end;
        }
        _windows = windows.ToArray();
        var most = _windows.Length == 0 ? FlashFile.Page : _windows.Max(w => w.Bytes);
        _file = FlashFile.Open(store.FilePath);
        Direct = _file.Direct;
        _buffers = new byte*[2];
        try
        {
            _buffers[0] = FlashFile.Allocate(most);
            _buffers[1] = FlashFile.Allocate(most);
        }
        catch
        {
            FlashFile.Free(_buffers[0]);
            _file.Dispose();
            throw;
        }
        Bytes = 2 * most;
        SurfaceBytes = chunks.Sum(c => (long)c.Bytes);
        if (_windows.Length > 0) Ahead(0, 0); // the first window, on its way before the first chunk is asked for
    }

    /// <inheritdoc/>
    public HeadLayout Layout { get; }

    /// <summary>The scripts it reads.</summary>
    public IReadOnlyList<HeadStore.Group> Groups { get; }

    /// <summary>Tokens it reads (padding rows not counted).</summary>
    public int Rows { get; }

    /// <summary>Bytes it holds in RAM: two windows.</summary>
    public long Bytes { get; }

    /// <summary>Bytes a scoring reads: the whole surface.</summary>
    public long SurfaceBytes { get; }

    /// <summary>Whether its reads go past the OS's cache.</summary>
    public bool Direct { get; }

    /// <inheritdoc/>
    public int ChunkCount => _chunks.Length;

    /// <inheritdoc/>
    public int MostRows { get; }

    /// <summary>Windows a scoring reads in: two or fewer are read once and held.</summary>
    public int Windows => _windows.Length;

    /// <summary>The bytes read and the time reading them took since the last call; the counts start again.</summary>
    public (long Read, TimeSpan Reading) TakeCounts() =>
        (Interlocked.Exchange(ref _read, 0), Stopwatch.GetElapsedTime(0, Interlocked.Exchange(ref _readTicks, 0)));

    /// <inheritdoc/>
    public void Chunk(int index, out ReadOnlySpan<byte> rows, out ReadOnlySpan<float> scales, out ReadOnlySpan<int> tokens)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var window = _windowOf[index];
        var slot = Slot(window);
        var next = (window + 1) % _windows.Length;
        if (next != window && _reading is null && Array.IndexOf(_holds, next) < 0) Ahead(next, 1 - slot);

        var chunk = _chunks[index];
        var start = _buffers[slot] + _placeInWindow[index];
        var cols = _blocks * Ptq1.BlockWeights;
        var weightBytes = Layout == HeadLayout.Paired ? Ptq1PairMatrix.WeightBytes(chunk.Rows, cols) : (long)chunk.Rows * _rowBytes;
        rows = new ReadOnlySpan<byte>(start, checked((int)weightBytes));
        scales = new ReadOnlySpan<float>(start + weightBytes, chunk.Rows * _blocks);
        tokens = new ReadOnlySpan<int>(start + weightBytes + (long)chunk.Rows * _blocks * sizeof(float), chunk.Rows);
    }

    // The buffer holding `window` whole: the one that does, the read in flight for it, or a read now.
    private int Slot(int window)
    {
        if (_reading is not null && _readingWindow == window) return Finish();
        var held = Array.IndexOf(_holds, window);
        if (held >= 0) return held;
        if (_reading is not null) Finish();            // another window in flight: let it land first
        var slot = _holds[0] == -1 || _holds[1] != -1 ? 0 : 1;
        Ahead(window, slot);
        return Finish();
    }

    private void Ahead(int window, int slot)
    {
        _holds[slot] = -1;
        _readingWindow = window;
        _readingSlot = slot;
        var (offset, bytes) = _windows[window];
        var into = (nint)_buffers[slot];
        _reading = Task.Run(() =>
        {
            var started = Stopwatch.GetTimestamp();
            if (_file.ReadChannels((byte*)into, bytes, offset, _channels, _readBytes) < bytes)
                throw new InvalidDataException($"{_file.FilePath} ends inside the head's rows.");
            Interlocked.Add(ref _read, bytes);
            Interlocked.Add(ref _readTicks, Stopwatch.GetTimestamp() - started);
        });
    }

    // The read in flight lands; its buffer holds its window.
    private int Finish()
    {
        var reading = _reading!;
        var slot = _readingSlot;
        var window = _readingWindow;
        _reading = null;
        _readingWindow = -1;
        _readingSlot = -1;
        reading.GetAwaiter().GetResult();
        _holds[slot] = window;
        return slot;
    }

    /// <summary>Lets the windows go: RAM holds none of the rows after this.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _reading?.Wait(); }
        catch (AggregateException) { /* the stream is going */ }
        FlashFile.Free(_buffers[0]);
        FlashFile.Free(_buffers[1]);
        _file.Dispose();
    }
}
