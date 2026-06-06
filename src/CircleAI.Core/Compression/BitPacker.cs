// BitPacker.cs
//
// Packs and unpacks small unsigned integer indices at arbitrary bit widths.
// Used by TurboQuant to pack the per-coordinate quantizer indices into a
// dense byte array — e.g. dim=1536 at 2 bits = 384 bytes (16× shrink from
// the original 1536×4 = 6144 bytes).

using System;

namespace CircleAI.Core.Compression;

/// <summary>
/// Bit-packing primitives for arbitrary widths (1..16 bits/index).
/// </summary>
public static class BitPacker
{
    /// <summary>
    /// Packs <paramref name="indices"/> at <paramref name="bitsPerIndex"/>
    /// into a new byte array. Indices are written least-significant-bit first.
    /// </summary>
    public static byte[] Pack(ReadOnlySpan<ushort> indices, int bitsPerIndex)
    {
        ValidateWidth(bitsPerIndex);
        var totalBits = indices.Length * bitsPerIndex;
        var packed = new byte[(totalBits + 7) / 8];

        int bitPos = 0;
        for (int i = 0; i < indices.Length; i++)
        {
            uint value = indices[i];
            if (bitsPerIndex < 16 && value >= (1u << bitsPerIndex))
                throw new ArgumentException(
                    $"Index {value} at position {i} exceeds {bitsPerIndex}-bit range.");

            int remaining = bitsPerIndex;
            int byteIdx = bitPos >> 3;
            int bitOffset = bitPos & 7;

            while (remaining > 0)
            {
                int take = Math.Min(remaining, 8 - bitOffset);
                int shift = bitsPerIndex - remaining;
                byte chunk = (byte)((value >> shift) & ((1u << take) - 1));
                packed[byteIdx] |= (byte)(chunk << bitOffset);

                remaining -= take;
                bitOffset = 0;
                byteIdx++;
            }
            bitPos += bitsPerIndex;
        }
        return packed;
    }

    /// <summary>
    /// Unpacks <paramref name="count"/> indices of <paramref name="bitsPerIndex"/>
    /// each from <paramref name="packed"/>.
    /// </summary>
    public static ushort[] Unpack(ReadOnlySpan<byte> packed, int count, int bitsPerIndex)
    {
        ValidateWidth(bitsPerIndex);
        var requiredBytes = (count * bitsPerIndex + 7) / 8;
        if (packed.Length < requiredBytes)
            throw new ArgumentException(
                $"Packed buffer too small: need {requiredBytes} bytes, got {packed.Length}.");

        var result = new ushort[count];
        int bitPos = 0;
        for (int i = 0; i < count; i++)
        {
            int remaining = bitsPerIndex;
            int byteIdx = bitPos >> 3;
            int bitOffset = bitPos & 7;
            uint value = 0;

            while (remaining > 0)
            {
                int take = Math.Min(remaining, 8 - bitOffset);
                int shift = bitsPerIndex - remaining;
                uint chunk = ((uint)packed[byteIdx] >> bitOffset) & ((1u << take) - 1);
                value |= chunk << shift;

                remaining -= take;
                bitOffset = 0;
                byteIdx++;
            }
            result[i] = (ushort)value;
            bitPos += bitsPerIndex;
        }
        return result;
    }

    private static void ValidateWidth(int bitsPerIndex)
    {
        if (bitsPerIndex is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(bitsPerIndex),
                "Bits per index must be 1..16.");
    }
}
