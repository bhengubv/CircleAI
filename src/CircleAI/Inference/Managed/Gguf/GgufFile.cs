// GgufFile.cs
//
// A GGUF model file, read the way the pinned fork reads it (ggml/src/gguf.cpp), and mapped
// into memory so that tensors are views of the file rather than copies: the 2B's 429 MB of
// weights never pass through the managed heap.
//
// Layout, little-endian (versions 2 and 3 are the same here):
//   "GGUF", u32 version, u64 tensor count, u64 metadata count
//   metadata: key (u64 length + UTF-8), u32 value type, value
//             an array is a u32 element type, a u64 count and the elements - never arrays
//   tensor infos: name, u32 dimension count (1 to 4), u64 per dimension (dimension 0 is the
//                 contiguous one), u32 ggml type, u64 offset into the data section
//   padding to general.alignment (32 unless the file sets it), then the data section, each
//   tensor starting where the one before ended, padded to the alignment
//
// Every read is checked against the file's length, so a truncated or damaged download fails
// here with InvalidDataException instead of reading past the mapping. The checks are the
// fork's own: power-of-two alignment, no duplicate keys or tensor names, no arrays of
// arrays, tensors in order and in bounds.
//
// The tensor views stay valid while the file is open; dispose it only when nothing reads
// them any more. While it is mapped, the file cannot be deleted or replaced on Windows.

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace CircleAI.Inference.Managed.Gguf;

/// <summary>One tensor: its name, shape, element type, and where its bytes are in the file.</summary>
/// <param name="Name">The tensor's name, such as <c>blk.0.attn_qkv.weight</c>.</param>
/// <param name="Dimensions">Its shape, dimension 0 first; dimension 0 is contiguous, the length of one row.</param>
/// <param name="Type">Its element type.</param>
/// <param name="Offset">Where its first byte is in the file.</param>
/// <param name="ByteCount">Its size in bytes; -1 when its type is not one this engine sizes.</param>
public sealed record GgufTensor(string Name, IReadOnlyList<long> Dimensions, GgmlType Type, long Offset, long ByteCount)
{
    /// <summary>Elements in all: the product of the dimensions.</summary>
    public long Elements
    {
        get
        {
            var n = 1L;
            foreach (var d in Dimensions) n *= d;
            return n;
        }
    }

    /// <summary>The length of one row: dimension 0.</summary>
    public long RowLength => Dimensions[0];

    /// <summary>Rows: every dimension after the first, multiplied.</summary>
    public long Rows => RowLength == 0 ? 0 : Elements / RowLength;
}

/// <summary>A GGUF file mapped into memory: its metadata, and its tensors as views of the file.</summary>
public sealed unsafe class GgufFile : IDisposable
{
    /// <summary>"GGUF" read as a little-endian u32.</summary>
    public const uint Magic = 0x46554747;

    /// <summary>The data section's alignment when the file does not set <c>general.alignment</c>.</summary>
    public const int DefaultAlignment = 32;

    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly Dictionary<string, GgufValue> _metadata = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GgufTensor> _tensorsByName = new(StringComparer.Ordinal);
    private readonly List<string> _keys = [];
    private readonly List<GgufTensor> _tensors = [];
    private byte* _base;
    private int _disposed;

    private GgufFile(string path)
    {
        FilePath = path;
        Length = new FileInfo(path).Length;
        if (Length < 24) throw new InvalidDataException($"{path} is {Length} bytes, too short for a GGUF header.");

        MemoryMappedFile? map = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
            view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        }
        catch
        {
            view?.Dispose();
            map?.Dispose();
            throw;
        }
        _map = map;
        _view = view;

        byte* start = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref start);
        _base = start + _view.PointerOffset;
        try
        {
            Parse();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Maps <paramref name="path"/> read-only and reads its header; it stays mapped until disposed.</summary>
    public static GgufFile Open(string path) => new(path);

    /// <summary>The file this was opened from.</summary>
    public string FilePath { get; }

    /// <summary>The file's size in bytes.</summary>
    public long Length { get; }

    /// <summary>The format version: 2 or 3.</summary>
    public uint Version { get; private set; }

    /// <summary>The data section's alignment.</summary>
    public int Alignment { get; private set; }

    /// <summary>Where the data section starts in the file.</summary>
    public long DataOffset { get; private set; }

    /// <summary>Every metadata key, in file order.</summary>
    public IReadOnlyList<string> Keys => _keys;

    /// <summary>Every tensor, in file order.</summary>
    public IReadOnlyList<GgufTensor> Tensors => _tensors;

    /// <summary>The value of metadata <paramref name="key"/>.</summary>
    public GgufValue this[string key] => _metadata.TryGetValue(key, out var value)
        ? value
        : throw new KeyNotFoundException($"{Path.GetFileName(FilePath)} has no metadata key {key}.");

    /// <summary>The value of metadata <paramref name="key"/>, when the file has it.</summary>
    public bool TryGetValue(string key, [NotNullWhen(true)] out GgufValue? value) => _metadata.TryGetValue(key, out value);

    /// <summary>The tensor called <paramref name="name"/>, or null when the file has none.</summary>
    public GgufTensor? Tensor(string name) => _tensorsByName.GetValueOrDefault(name);

    /// <summary>A tensor's bytes, as a view of the mapped file.</summary>
    public ReadOnlySpan<byte> Bytes(GgufTensor tensor)
    {
        CheckOwn(tensor);
        if (tensor.ByteCount < 0)
            throw new NotSupportedException($"Tensor {tensor.Name} is ggml type {(uint)tensor.Type}, which this engine does not read.");
        if (tensor.ByteCount > int.MaxValue)
            throw new NotSupportedException($"Tensor {tensor.Name} is {tensor.ByteCount} bytes, more than one span holds; use Pointer.");
        return Span(tensor.Offset, (int)tensor.ByteCount);
    }

    /// <summary>An F32 tensor's values, as a view of the mapped file.</summary>
    public ReadOnlySpan<float> Floats(GgufTensor tensor)
    {
        if (tensor.Type != GgmlType.F32) throw new InvalidOperationException($"Tensor {tensor.Name} is {tensor.Type}, not F32.");
        return MemoryMarshal.Cast<byte, float>(Bytes(tensor));
    }

    /// <summary>A tensor's first byte, for products that walk it with pointers.</summary>
    public byte* Pointer(GgufTensor tensor)
    {
        CheckOwn(tensor);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _base + tensor.Offset;
    }

    /// <summary>Unmaps the file. Every view taken from it is invalid afterwards.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_base != null)
        {
            _base = null;
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
        _view.Dispose();
        _map.Dispose();
    }

    // A checked view of the mapping, for this file's own values and arrays.
    internal ReadOnlySpan<byte> Span(long offset, int length)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (offset < 0 || length < 0 || offset > Length - length)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, $"{length} bytes there would run past the file.");
        return new ReadOnlySpan<byte>(_base + offset, length);
    }

    private void CheckOwn(GgufTensor tensor)
    {
        ArgumentNullException.ThrowIfNull(tensor);
        if (!_tensorsByName.TryGetValue(tensor.Name, out var own) || !ReferenceEquals(own, tensor))
            throw new ArgumentException($"Tensor {tensor.Name} does not belong to {Path.GetFileName(FilePath)}.", nameof(tensor));
    }

    private void Parse()
    {
        var at = 0L;
        if (U32(ref at) != Magic) throw Damaged("it does not start with GGUF");
        Version = U32(ref at);
        if (Version is not (2 or 3)) throw Damaged($"it is version {Version}; versions 2 and 3 are read");

        // Every key and every tensor takes at least 8 bytes, so a count the file cannot hold
        // is damage, not a size to allocate for.
        var tensorCount = U64(ref at);
        var keyCount = U64(ref at);
        if (tensorCount > (ulong)Length / 8 || keyCount > (ulong)Length / 8)
            throw Damaged($"it claims {keyCount} keys and {tensorCount} tensors in {Length} bytes");

        for (var i = 0UL; i < keyCount; i++)
        {
            var key = Text(ref at);
            var value = Value(ref at, (GgufType)U32(ref at), key);
            if (!_metadata.TryAdd(key, value)) throw Damaged($"the key {key} appears twice");
            _keys.Add(key);
        }

        Alignment = DefaultAlignment;
        if (_metadata.TryGetValue("general.alignment", out var alignment))
        {
            if (alignment.Type != GgufType.UInt32) throw Damaged($"general.alignment is {alignment.Type}, not UInt32");
            var n = alignment.AsInt64();
            if (n <= 0 || (n & (n - 1)) != 0) throw Damaged($"general.alignment {n} is not a power of two");
            Alignment = (int)n;
        }

        var infos = new List<(string Name, long[] Shape, GgmlType Type, ulong Offset)>();
        for (var i = 0UL; i < tensorCount; i++)
        {
            var name = Text(ref at);
            var dimensions = U32(ref at);
            if (dimensions is 0 or > 4) throw Damaged($"tensor {name} has {dimensions} dimensions; ggml allows 1 to 4");
            var shape = new long[dimensions];
            for (var d = 0; d < shape.Length; d++)
            {
                var n = U64(ref at);
                if (n > long.MaxValue) throw Damaged($"tensor {name} has a dimension of {n}");
                shape[d] = (long)n;
            }
            var type = (GgmlType)U32(ref at);
            infos.Add((name, shape, type, U64(ref at)));
        }

        DataOffset = AlignUp(at, Alignment);
        if (DataOffset > Length) throw Damaged("its data section would start past its end");
        var dataLength = Length - DataOffset;

        // As the fork lays them out: each tensor starts where the one before ended, padded to
        // the alignment. A type this engine cannot size ends that check (its size is unknown),
        // but not the bounds check of the rest.
        var expected = 0L;
        var inOrder = true;
        foreach (var (name, shape, type, offset) in infos)
        {
            if (_tensorsByName.ContainsKey(name)) throw Damaged($"the tensor {name} appears twice");
            if (offset > (ulong)dataLength) throw Damaged($"tensor {name} starts past the end of the file");
            if (offset % (ulong)Alignment != 0) throw Damaged($"tensor {name} at {offset} is not aligned to {Alignment}");

            var bytes = -1L;
            if (GgmlTypes.IsKnown(type))
            {
                var (blockElements, blockBytes) = GgmlTypes.Layout(type);
                if (shape[0] % blockElements != 0)
                    throw Damaged($"tensor {name}: a row of {shape[0]} is not whole {type} blocks of {blockElements}");
                bytes = SizeOf(name, shape, blockElements, blockBytes);
                if (bytes > dataLength - (long)offset) throw Damaged($"tensor {name} runs past the end of the file");
                if (inOrder && (long)offset != expected) throw Damaged($"tensor {name} starts at {offset}, not at {expected}");
                expected = AlignUp((long)offset + bytes, Alignment);
            }
            else
            {
                inOrder = false;
            }

            var tensor = new GgufTensor(name, shape, type, DataOffset + (long)offset, bytes);
            _tensors.Add(tensor);
            _tensorsByName.Add(name, tensor);
        }
    }

    private GgufValue Value(ref long at, GgufType type, string key) => type switch
    {
        GgufType.UInt8 => new(type, (long)Take(ref at, 1)[0]),
        GgufType.Int8 => new(type, (long)(sbyte)Take(ref at, 1)[0]),
        GgufType.UInt16 => new(type, (long)BinaryPrimitives.ReadUInt16LittleEndian(Take(ref at, 2))),
        GgufType.Int16 => new(type, (long)BinaryPrimitives.ReadInt16LittleEndian(Take(ref at, 2))),
        GgufType.UInt32 => new(type, (long)U32(ref at)),
        GgufType.Int32 => new(type, (long)BinaryPrimitives.ReadInt32LittleEndian(Take(ref at, 4))),
        GgufType.Float32 => new(type, BinaryPrimitives.ReadSingleLittleEndian(Take(ref at, 4))),
        GgufType.Bool => new(type, Take(ref at, 1)[0] != 0),
        GgufType.String => new(type, Text(ref at)),
        GgufType.Array => new(type, Array(ref at, key)),
        GgufType.UInt64 => new(type, U64(ref at)),
        GgufType.Int64 => new(type, BinaryPrimitives.ReadInt64LittleEndian(Take(ref at, 8))),
        GgufType.Float64 => new(type, BinaryPrimitives.ReadDoubleLittleEndian(Take(ref at, 8))),
        _ => throw Damaged($"the key {key} has value type {(uint)type}, which GGUF does not define"),
    };

    private GgufArray Array(ref long at, string key)
    {
        var elementType = (GgufType)U32(ref at);
        var count = U64(ref at);
        if (count > int.MaxValue) throw Damaged($"the array {key} claims {count} elements");
        var n = (int)count;
        var start = at;

        if (elementType == GgufType.String)
        {
            // Each string takes at least its 8-byte length, which bounds the count before it
            // becomes an allocation.
            if ((long)n * 8 > Length - at) throw Damaged($"the array {key} claims {n} strings in the bytes left");
            var strings = new long[n];
            for (var i = 0; i < n; i++)
            {
                var length = U64(ref at);
                if (length > int.MaxValue || length > (ulong)(Length - at)) throw Damaged($"string {i} of {key} runs past the end of the file");
                strings[i] = at;
                at += (long)length;
            }
            return new GgufArray(this, elementType, n, start, strings);
        }

        var size = elementType switch
        {
            GgufType.UInt8 or GgufType.Int8 or GgufType.Bool => 1,
            GgufType.UInt16 or GgufType.Int16 => 2,
            GgufType.UInt32 or GgufType.Int32 or GgufType.Float32 => 4,
            GgufType.UInt64 or GgufType.Int64 or GgufType.Float64 => 8,
            GgufType.Array => throw Damaged($"the array {key} holds arrays, which GGUF does not allow"),
            _ => throw Damaged($"the array {key} holds value type {(uint)elementType}, which GGUF does not define"),
        };
        if ((long)n * size > Length - at) throw Damaged($"the array {key} runs past the end of the file");
        at += (long)n * size;
        return new GgufArray(this, elementType, n, start, null);
    }

    private ReadOnlySpan<byte> Take(ref long at, int count)
    {
        if (count > Length - at) throw Damaged($"it ends at byte {Length}, inside a value at byte {at}");
        var span = new ReadOnlySpan<byte>(_base + at, count);
        at += count;
        return span;
    }

    private uint U32(ref long at) => BinaryPrimitives.ReadUInt32LittleEndian(Take(ref at, 4));

    private ulong U64(ref long at) => BinaryPrimitives.ReadUInt64LittleEndian(Take(ref at, 8));

    private string Text(ref long at)
    {
        var length = U64(ref at);
        if (length > int.MaxValue || length > (ulong)(Length - at)) throw Damaged($"a string at byte {at} runs past the end of the file");
        return Encoding.UTF8.GetString(Take(ref at, (int)length));
    }

    private long SizeOf(string name, long[] shape, int blockElements, int blockBytes)
    {
        try
        {
            var elements = 1L;
            foreach (var d in shape) elements = checked(elements * d);
            return checked(elements / blockElements * blockBytes);
        }
        catch (OverflowException)
        {
            throw Damaged($"tensor {name} is too large to address");
        }
    }

    private static long AlignUp(long value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private InvalidDataException Damaged(string why) => new($"{Path.GetFileName(FilePath)} is not a readable GGUF file: {why}.");
}
