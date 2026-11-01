// TurboKvCache.cs
//
// One full-attention layer's working memory held as TurboQuant codes (S3). Each key and value of
// each KV head is turned by HadamardRotation - the transform the weights are folded with - and each
// coordinate kept as its bin in the Lloyd-Max codebook for the head's width, beside the vector's
// norm; keys may instead take the Prod form, one bit narrower plus QJL's sign over the residual,
// whose scores carry no bias. Attention runs on the codes: a query is turned once a head and meets
// each key as a sum of table lookups; the values are summed in the turned space and turned back
// once a head. Codes are packed low bit first, a stream of `bits` bits a coordinate.
//
// Only the compressed modes make one; Off keeps the fp16 cache the fork keeps. One thread at a
// time: it works in buffers it owns.

using CircleAI.Core;
using CircleAI.Core.Compression;

namespace CircleAI.Inference.Managed.Model;

internal sealed class TurboKvCache
{
    private readonly int _capacity;
    private readonly int _heads;
    private readonly int _dim;
    private readonly int _keyBits, _valueBits;
    private readonly int _keyBytes, _valueBytes;
    private readonly BetaCodebook _keyBook, _valueBook;
    private readonly QjlProjection? _qjl;
    private readonly byte[] _keyCodes, _valueCodes;
    private readonly float[] _keyNorms, _valueNorms;
    private readonly byte[]? _keySigns;
    private readonly float[]? _keyResiduals;

    // Working buffers.
    private readonly float[] _unit, _turned, _lut, _projected, _sum;
    private readonly byte[] _codes;

    public TurboKvCache(int capacity, int heads, int dim, KvCompressionMode mode, TurboQuantForm keyForm)
    {
        var bits = mode switch
        {
            KvCompressionMode.TurboQuant4Bit => 4,
            KvCompressionMode.TurboQuant3Bit => 3,
            KvCompressionMode.TurboQuant2Bit => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Off keeps the fp16 cache."),
        };
        Mode = mode;
        KeyForm = keyForm;
        _capacity = capacity;
        _heads = heads;
        _dim = dim;
        _keyBits = keyForm == TurboQuantForm.Prod ? bits - 1 : bits;
        _valueBits = bits;
        _keyBook = BetaLloydMaxCodebook.Get(_keyBits, dim);
        _valueBook = BetaLloydMaxCodebook.Get(_valueBits, dim);
        _keyBytes = (dim * _keyBits + 7) / 8;
        _valueBytes = (dim * _valueBits + 7) / 8;
        _keyCodes = new byte[capacity * heads * _keyBytes];
        _valueCodes = new byte[capacity * heads * _valueBytes];
        _keyNorms = new float[capacity * heads];
        _valueNorms = new float[capacity * heads];
        if (keyForm == TurboQuantForm.Prod)
        {
            _qjl = QjlProjection.For(dim);
            _keySigns = new byte[capacity * heads * _qjl.SignBytes];
            _keyResiduals = new float[capacity * heads];
        }

        _unit = new float[dim];
        _turned = new float[dim];
        _lut = new float[dim << _keyBits];
        _projected = new float[dim];
        _sum = new float[dim];
        _codes = new byte[dim];
    }

    /// <summary>The bits a coordinate takes.</summary>
    public KvCompressionMode Mode { get; }

    /// <summary>The keys' form; values are always the MSE form, which reconstructs closest.</summary>
    public TurboQuantForm KeyForm { get; }

    /// <summary>Encodes one token's keys and values - every KV head, one after another - at <paramref name="position"/>.</summary>
    public void Write(int position, ReadOnlySpan<float> keys, ReadOnlySpan<float> values)
    {
        if ((uint)position >= (uint)_capacity) throw new ArgumentOutOfRangeException(nameof(position));
        for (var head = 0; head < _heads; head++)
        {
            var slot = position * _heads + head;
            _keyNorms[slot] = Quantize(keys.Slice(head * _dim, _dim), _keyBook, _keyBits, _keyCodes.AsSpan(slot * _keyBytes, _keyBytes));
            if (_qjl is not null)
            {
                // _turned still holds the key's turned unit vector: what its codes did not catch.
                double residual = 0;
                for (var j = 0; j < _dim; j++)
                {
                    _turned[j] -= _keyBook.Centroids[_codes[j]];
                    residual += (double)_turned[j] * _turned[j];
                }
                _keyResiduals![slot] = (float)Math.Sqrt(residual);
                _qjl.Signs(_turned, _keySigns!.AsSpan(slot * _qjl.SignBytes, _qjl.SignBytes));
            }
            _valueNorms[slot] = Quantize(values.Slice(head * _dim, _dim), _valueBook, _valueBits, _valueCodes.AsSpan(slot * _valueBytes, _valueBytes));
        }
    }

    /// <summary>
    /// <paramref name="scores"/>[t] = &lt;<paramref name="query"/>, key t of <paramref name="head"/>&gt;
    /// for the first <paramref name="count"/> positions, from the codes.
    /// </summary>
    public void Scores(int head, ReadOnlySpan<float> query, int count, Span<float> scores)
    {
        HadamardRotation.Rotate(query, _turned);
        var levels = 1 << _keyBits;
        var centroids = _keyBook.Centroids;
        for (var j = 0; j < _dim; j++)
        {
            var q = _turned[j];
            for (var c = 0; c < levels; c++) _lut[j * levels + c] = q * centroids[c];
        }
        if (_qjl is not null) _qjl.Project(_turned, _projected);

        for (var t = 0; t < count; t++)
        {
            var slot = t * _heads + head;
            var sum = LookupSum(_keyCodes.AsSpan(slot * _keyBytes, _keyBytes), _keyBits, _lut, _dim);
            if (_qjl is not null) sum += _qjl.Estimate(_projected, _keySigns!.AsSpan(slot * _qjl.SignBytes, _qjl.SignBytes), _keyResiduals![slot]);
            scores[t] = _keyNorms[slot] * sum;
        }
    }

    /// <summary>
    /// <paramref name="output"/> = the sum over the first <paramref name="count"/> positions of
    /// <paramref name="weights"/>[t] times value t of <paramref name="head"/>, from the codes.
    /// </summary>
    public void Mix(int head, ReadOnlySpan<float> weights, int count, Span<float> output)
    {
        Array.Clear(_sum);
        var centroids = _valueBook.Centroids;
        var mask = (1u << _valueBits) - 1;
        for (var t = 0; t < count; t++)
        {
            var slot = t * _heads + head;
            var w = weights[t] * _valueNorms[slot];
            if (w == 0f) continue;
            var packed = _valueCodes.AsSpan(slot * _valueBytes, _valueBytes);
            uint buffer = 0;
            int have = 0, at = 0;
            for (var j = 0; j < _dim; j++)
            {
                if (have < _valueBits)
                {
                    buffer |= (uint)packed[at++] << have;
                    have += 8;
                }
                _sum[j] += w * centroids[buffer & mask];
                buffer >>= _valueBits;
                have -= _valueBits;
            }
        }
        HadamardRotation.Unrotate(_sum, output);
    }

    // Sessions: the mode and the first `count` positions, as they are held.
    public void WriteTo(BinaryWriter writer, int count)
    {
        writer.Write((int)Mode);
        writer.Write((int)KeyForm);
        var slots = count * _heads;
        writer.Write(_keyCodes.AsSpan(0, slots * _keyBytes));
        writer.Write(_valueCodes.AsSpan(0, slots * _valueBytes));
        writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_keyNorms.AsSpan(0, slots)));
        writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_valueNorms.AsSpan(0, slots)));
        if (_qjl is not null)
        {
            writer.Write(_keySigns!.AsSpan(0, slots * _qjl.SignBytes));
            writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_keyResiduals!.AsSpan(0, slots)));
        }
    }

    public void ReadFrom(BinaryReader reader, int count)
    {
        if (reader.ReadInt32() != (int)Mode || reader.ReadInt32() != (int)KeyForm)
            throw new InvalidDataException("The session holds its working memory in another form.");
        var slots = count * _heads;
        Fill(reader, _keyCodes.AsSpan(0, slots * _keyBytes));
        Fill(reader, _valueCodes.AsSpan(0, slots * _valueBytes));
        Fill(reader, System.Runtime.InteropServices.MemoryMarshal.AsBytes(_keyNorms.AsSpan(0, slots)));
        Fill(reader, System.Runtime.InteropServices.MemoryMarshal.AsBytes(_valueNorms.AsSpan(0, slots)));
        if (_qjl is not null)
        {
            Fill(reader, _keySigns!.AsSpan(0, slots * _qjl.SignBytes));
            Fill(reader, System.Runtime.InteropServices.MemoryMarshal.AsBytes(_keyResiduals!.AsSpan(0, slots)));
        }
    }

    private static void Fill(BinaryReader reader, Span<byte> destination)
    {
        while (destination.Length > 0)
        {
            var n = reader.Read(destination);
            if (n == 0) throw new InvalidDataException("The session ends early.");
            destination = destination[n..];
        }
    }

    // Norm, then the turned unit vector's bins, packed; leaves the turned vector in _turned and
    // the bins in _codes. A zero vector keeps zero codes and a zero norm, which weighs nothing.
    private float Quantize(ReadOnlySpan<float> x, BetaCodebook book, int bits, Span<byte> packed)
    {
        double sum = 0;
        for (var j = 0; j < _dim; j++) sum += (double)x[j] * x[j];
        var norm = (float)Math.Sqrt(sum);
        if (norm < 1e-20f)
        {
            packed.Clear();
            Array.Clear(_codes);
            Array.Clear(_turned);
            return 0f;
        }

        var inverse = 1f / norm;
        for (var j = 0; j < _dim; j++) _unit[j] = x[j] * inverse;
        HadamardRotation.Rotate(_unit, _turned);
        for (var j = 0; j < _dim; j++) _codes[j] = (byte)BetaLloydMaxCodebook.BinFor(_turned[j], book.Boundaries);
        Pack(_codes, bits, packed);
        return norm;
    }

    private static void Pack(ReadOnlySpan<byte> codes, int bits, Span<byte> packed)
    {
        packed.Clear();
        uint buffer = 0;
        int have = 0, at = 0;
        foreach (var code in codes)
        {
            buffer |= (uint)code << have;
            have += bits;
            while (have >= 8)
            {
                packed[at++] = (byte)buffer;
                buffer >>= 8;
                have -= 8;
            }
        }
        if (have > 0) packed[at] = (byte)buffer;
    }

    // Sum over the coordinates of the table entry each one's code picks.
    private static float LookupSum(ReadOnlySpan<byte> packed, int bits, ReadOnlySpan<float> lut, int dim)
    {
        var mask = (1u << bits) - 1;
        var levels = 1 << bits;
        float sum = 0f;
        uint buffer = 0;
        int have = 0, at = 0;
        for (var j = 0; j < dim; j++)
        {
            if (have < bits)
            {
                buffer |= (uint)packed[at++] << have;
                have += 8;
            }
            sum += lut[j * levels + (int)(buffer & mask)];
            buffer >>= bits;
            have -= bits;
        }
        return sum;
    }
}
