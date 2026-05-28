// FederatedAveraging.cs
//
// Sample-size-weighted averaging of model deltas. The reference encoding
// treats each ModelDelta.DeltaPayload as a little-endian IEEE 754 float[].
// Engines using a different encoding should not call this helper; they
// should implement their own aggregator and use the IFederationAggregator
// contract.

namespace Circle.AI.Federation;

using System.Buffers.Binary;

/// <summary>
/// Sample-size-weighted averaging over <see cref="ModelDelta.DeltaPayload"/>
/// arrays interpreted as little-endian IEEE 754 <c>float[]</c>.
/// </summary>
public static class FederatedAveraging
{
    /// <summary>
    /// Computes the sample-size-weighted average of the supplied deltas and
    /// returns the encoded result as little-endian IEEE 754 bytes.
    /// </summary>
    /// <param name="deltas">Non-empty list of deltas to average.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="deltas"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">
    /// When <paramref name="deltas"/> is empty, when payload byte lengths are
    /// inconsistent, when a payload length is not a multiple of 4 bytes, or
    /// when total sample weight is zero.
    /// </exception>
    public static byte[] Average(IReadOnlyList<ModelDelta> deltas)
    {
        ArgumentNullException.ThrowIfNull(deltas);
        if (deltas.Count == 0)
        {
            throw new ArgumentException("Cannot average an empty delta list.", nameof(deltas));
        }

        var expectedBytes = deltas[0].DeltaPayload.Length;
        if (expectedBytes == 0)
        {
            throw new ArgumentException("Delta payloads must be non-empty.", nameof(deltas));
        }
        if (expectedBytes % sizeof(float) != 0)
        {
            throw new ArgumentException(
                $"Delta payload length ({expectedBytes}) must be a multiple of {sizeof(float)} bytes.",
                nameof(deltas));
        }

        for (var i = 1; i < deltas.Count; i++)
        {
            if (deltas[i].DeltaPayload.Length != expectedBytes)
            {
                throw new ArgumentException(
                    $"Delta payload length mismatch: index 0 = {expectedBytes} bytes, " +
                    $"index {i} = {deltas[i].DeltaPayload.Length} bytes.",
                    nameof(deltas));
            }
        }

        var floatCount = expectedBytes / sizeof(float);
        var totalSamples = 0L;
        foreach (var d in deltas)
        {
            if (d.SampleCount < 0)
            {
                throw new ArgumentException(
                    $"SampleCount must be non-negative; delta {d.Id} reported {d.SampleCount}.",
                    nameof(deltas));
            }
            totalSamples += d.SampleCount;
        }
        if (totalSamples == 0)
        {
            throw new ArgumentException(
                "Total sample weight across deltas is zero — cannot perform weighted average.",
                nameof(deltas));
        }

        var accumulator = new double[floatCount];

        foreach (var d in deltas)
        {
            var weight = (double)d.SampleCount / totalSamples;
            var span = d.DeltaPayload.AsSpan();
            for (var i = 0; i < floatCount; i++)
            {
                var value = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(i * sizeof(float), sizeof(float)));
                accumulator[i] += value * weight;
            }
        }

        var output = new byte[expectedBytes];
        var outSpan = output.AsSpan();
        for (var i = 0; i < floatCount; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(outSpan.Slice(i * sizeof(float), sizeof(float)), (float)accumulator[i]);
        }
        return output;
    }

    /// <summary>
    /// Encodes a <see cref="float"/> array as little-endian IEEE 754 bytes.
    /// Convenience for callers and tests; does not allocate intermediate arrays.
    /// </summary>
    public static byte[] EncodeFloats(float[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var output = new byte[values.Length * sizeof(float)];
        var span = output.AsSpan();
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(i * sizeof(float), sizeof(float)), values[i]);
        }
        return output;
    }

    /// <summary>
    /// Decodes little-endian IEEE 754 bytes into a <see cref="float"/> array.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// When <paramref name="payload"/> length is not a multiple of 4 bytes.
    /// </exception>
    public static float[] DecodeFloats(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length % sizeof(float) != 0)
        {
            throw new ArgumentException(
                $"Payload length ({payload.Length}) must be a multiple of {sizeof(float)} bytes.",
                nameof(payload));
        }
        var count = payload.Length / sizeof(float);
        var output = new float[count];
        var span = payload.AsSpan();
        for (var i = 0; i < count; i++)
        {
            output[i] = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(i * sizeof(float), sizeof(float)));
        }
        return output;
    }
}
