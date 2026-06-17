// TurboVecEmbeddingIndex.cs
//
// (RT-09b) IEmbeddingIndex backed by turbovecbridge. SIMD-blocked
// quantised search via the vendored turbovec Rust crate.
//
// Threading: this wraps a single TurboQuantIndex handle. The turbovec
// docs say `search` is &self-safe; `add` requires &mut. The .NET
// wrapper takes a SemaphoreSlim(1, 1) to serialise mutation and allow
// concurrent search (the Rust crate already gates internally).

using System;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Embeddings.Local;

/// <summary>
/// (RT-09b) Default <see cref="IEmbeddingIndex"/> backed by the turbovec
/// Rust crate via the <c>turbovecbridge</c> cdylib.
/// </summary>
public sealed class TurboVecEmbeddingIndex : IEmbeddingIndex
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly int _bitWidth;
    private IntPtr _handle;
    private long _count;
    private bool _disposed;

    /// <inheritdoc/>
    public int Dimension { get; }

    /// <inheritdoc/>
    public long Count
    {
        get
        {
            ThrowIfDisposed();
            return Volatile.Read(ref _count);
        }
    }

    /// <summary>Bit-width used to quantise vectors. One of {2, 3, 4}.</summary>
    public int BitWidth => _bitWidth;

    /// <summary>
    /// Construct a fresh index. <paramref name="dimension"/> must be > 0
    /// and a multiple of 8. <paramref name="bitWidth"/> must be 2, 3, or 4.
    /// </summary>
    public TurboVecEmbeddingIndex(int dimension, int bitWidth = 4)
    {
        if (dimension <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimension), "Dimension must be positive.");
        if (dimension % 8 != 0)
            throw new ArgumentException("Dimension must be a multiple of 8.", nameof(dimension));
        if (bitWidth is < 2 or > 4)
            throw new ArgumentOutOfRangeException(nameof(bitWidth), "BitWidth must be 2, 3, or 4.");

        Dimension = dimension;
        _bitWidth = bitWidth;
        _handle   = TurboVecInterop.IndexNew(dimension, bitWidth);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException(
                "turbovecbridge: tvb_index_new returned NULL. Native library load failed or arguments rejected.");
    }

    private TurboVecEmbeddingIndex(IntPtr handle, int dimension, int bitWidth, long count)
    {
        Dimension = dimension;
        _bitWidth = bitWidth;
        _handle   = handle;
        _count    = count;
    }

    /// <inheritdoc/>
    public async ValueTask<long> AddAsync(ReadOnlyMemory<float> vector, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (vector.Length != Dimension)
            throw new ArgumentException(
                $"Vector length {vector.Length} != index dimension {Dimension}.",
                nameof(vector));

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int status;
            unsafe
            {
                fixed (float* ptr = vector.Span)
                {
                    status = TurboVecInterop.IndexAdd(_handle, ptr, count: 1);
                }
            }
            if (status != TurboVecInterop.TVB_OK)
                throw new InvalidOperationException(
                    "turbovecbridge.add failed: " + TurboVecInterop.DescribeStatus(status));
            return Interlocked.Increment(ref _count) - 1;
        }
        finally { _writeGate.Release(); }
    }

    /// <inheritdoc/>
    public ValueTask<EmbeddingIndexHit[]> SearchAsync(
        ReadOnlyMemory<float> queryVector, int topK, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (queryVector.Length != Dimension)
            throw new ArgumentException(
                $"Query length {queryVector.Length} != index dimension {Dimension}.",
                nameof(queryVector));
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));
        if (Volatile.Read(ref _count) == 0)
            return ValueTask.FromResult(Array.Empty<EmbeddingIndexHit>());

        var indices = new long[topK];
        var scores  = new float[topK];

        int status;
        unsafe
        {
            fixed (float* qPtr = queryVector.Span)
            fixed (long*  iPtr = indices)
            fixed (float* sPtr = scores)
            {
                status = TurboVecInterop.IndexSearch(_handle, qPtr, topK, iPtr, sPtr);
            }
        }
        if (status != TurboVecInterop.TVB_OK)
            throw new InvalidOperationException(
                "turbovecbridge.search failed: " + TurboVecInterop.DescribeStatus(status));

        // Pack: turbovec emits -1 in the id slot when fewer than topK hits exist.
        var valid = 0;
        for (var i = 0; i < topK; i++)
            if (indices[i] >= 0) valid++;

        var hits = new EmbeddingIndexHit[valid];
        var j = 0;
        for (var i = 0; i < topK; i++)
            if (indices[i] >= 0)
                hits[j++] = new EmbeddingIndexHit(indices[i], scores[i]);
        return ValueTask.FromResult(hits);
    }

    /// <inheritdoc/>
    public async ValueTask SaveAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ThrowIfDisposed();
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            var status = TurboVecInterop.IndexSave(_handle, path);
            if (status != TurboVecInterop.TVB_OK)
                throw new InvalidOperationException(
                    "turbovecbridge.save failed: " + TurboVecInterop.DescribeStatus(status));
        }
        finally { _writeGate.Release(); }
    }

    /// <inheritdoc/>
    public async ValueTask LoadAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ThrowIfDisposed();
        if (!System.IO.File.Exists(path))
            throw new System.IO.FileNotFoundException("Index file not found.", path);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var newHandle = TurboVecInterop.IndexLoad(path);
            if (newHandle == IntPtr.Zero)
                throw new InvalidOperationException(
                    "turbovecbridge.load returned NULL. File may be corrupt, version-mismatched, or unreadable.");
            var loadedDim = TurboVecInterop.IndexDim(newHandle);
            if (loadedDim != Dimension)
            {
                TurboVecInterop.IndexFree(newHandle);
                throw new System.IO.InvalidDataException(
                    $"Loaded index dim {loadedDim} != configured dim {Dimension}.");
            }
            // Swap handles atomically.
            var old = _handle;
            _handle = newHandle;
            _count  = TurboVecInterop.IndexLen(newHandle);
            if (old != IntPtr.Zero) TurboVecInterop.IndexFree(old);
        }
        finally { _writeGate.Release(); }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            try { TurboVecInterop.IndexFree(_handle); } catch { /* swallow */ }
            _handle = IntPtr.Zero;
        }
        _writeGate.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TurboVecEmbeddingIndex));
    }

    /// <summary>
    /// ABI version reported by the loaded <c>turbovecbridge</c> native
    /// library. Bumps when the C ABI changes; .NET callers can compare
    /// against the value they were compiled against.
    /// </summary>
    public static int NativeAbiVersion() => TurboVecInterop.AbiVersion();
}
