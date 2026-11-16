// Q4.cs
//
// SIXTEEN LEVELS A WEIGHT, for a row that is not ternary. One block holds 128 weights in 66 bytes:
//
//   qs[0..63]  two 4-bit levels per byte: weight j in the low half of qs[j], weight 64 + j in its high half (j = 0..63)
//   d          fp16 scale at byte 64: the largest size among the block's weights
//
// A level is 8 + round(7 * weight / d), so 1 to 15, and the weight it stands for is (level - 8) * d / 7.
//
// WHY IT EXISTS. The indexed mind keeps ffn_down by neuron (CachedIndex): a piece holds each of its neurons' own column
// as a row. Where the file folds a Hadamard rotation into ffn_down's input, that column - the fold undone - is a sum of
// 128 or 512 signed trits a weight, close to normally spread, and a ternary row keeps about three quarters of its
// direction. Sixteen levels keep it to about one part in a hundred (Q4KernelTests measures both on such values). The two
// halves of a byte are weights 64 apart, so sixteen bytes are thirty-two weights in two runs with no shuffling.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace CircleAI.Inference.Managed.Kernels;

/// <summary>Q4: 128 weights in 66 bytes at sixteen levels a weight, and the sum of a scaled row into an output.</summary>
public static class Q4
{
    /// <summary>Weights per block.</summary>
    public const int BlockWeights = 128;

    /// <summary>Bytes per block: 64 of levels, then the scale as a half.</summary>
    public const int BlockBytes = 66;

    /// <summary>Steps from zero to the block's largest weight: a level is -7 to +7 of them.</summary>
    public const int Levels = 7;

    private const int ScaleOffset = 64;

    /// <summary>The scale of one block: the largest size among its weights.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Scale(ReadOnlySpan<byte> block) => (float)BinaryPrimitives.ReadHalfLittleEndian(block.Slice(ScaleOffset, 2));

    /// <summary>Writes 128 weights to one block: the scale is their largest size, each weight the nearest of the fifteen levels.</summary>
    public static void Pack(ReadOnlySpan<float> weights, Span<byte> block)
    {
        if (weights.Length != BlockWeights) throw new ArgumentException($"A block is {BlockWeights} weights, not {weights.Length}.", nameof(weights));
        if (block.Length < BlockBytes) throw new ArgumentException($"A block is {BlockBytes} bytes, not {block.Length}.", nameof(block));
        var max = 0f;
        for (var i = 0; i < BlockWeights; i++)
        {
            var a = MathF.Abs(weights[i]);
            if (a > max) max = a;
        }

        // THE SCALE AS IT WILL BE READ BACK - a half - is what the levels are taken against, so each weight lands on the
        // level nearest it in the numbers the decode uses.
        var half = (Half)max;
        if (Half.IsInfinity(half)) half = Half.MaxValue;
        var scale = (float)half;
        BinaryPrimitives.WriteHalfLittleEndian(block.Slice(ScaleOffset, 2), half);
        var per = scale > 0 ? Levels / scale : 0f;
        for (var j = 0; j < BlockWeights / 2; j++)
            block[j] = (byte)(Level(weights[j] * per) | (Level(weights[BlockWeights / 2 + j] * per) << 4));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Level(float steps) => Math.Clamp((int)MathF.Round(steps), -Levels, Levels) + 8;

    /// <summary>Decodes one block's 128 weights, in weight order.</summary>
    public static void Unpack(ReadOnlySpan<byte> block, Span<float> weights)
    {
        if (block.Length < BlockBytes) throw new ArgumentException($"A block is {BlockBytes} bytes, not {block.Length}.", nameof(block));
        if (weights.Length < BlockWeights) throw new ArgumentException($"A block is {BlockWeights} weights, not {weights.Length}.", nameof(weights));
        var step = Scale(block) / Levels;
        for (var j = 0; j < BlockWeights / 2; j++)
        {
            weights[j] = ((block[j] & 15) - 8) * step;
            weights[BlockWeights / 2 + j] = ((block[j] >> 4) - 8) * step;
        }
    }

    /// <summary>
    /// Adds <paramref name="a"/> times the block's 128 weights to <paramref name="output"/>[0..127]. The vector path does
    /// the scalar path's multiply and add a lane each, so both give the same floats.
    /// </summary>
    public static unsafe void AddScaled(byte* block, float a, float* output)
    {
        var c = a * ((float)BinaryPrimitives.ReadHalfLittleEndian(new ReadOnlySpan<byte>(block + ScaleOffset, 2)) / Levels);
        if (c == 0) return;
        if (Vector128.IsHardwareAccelerated)
        {
            var scale = Vector128.Create(c);
            var low = Vector128.Create((byte)15);
            var eight = Vector128.Create((sbyte)8);
            for (var k = 0; k < BlockWeights / 2; k += 16)
            {
                var bytes = Vector128.Load(block + k);
                Add16((bytes & low).AsSByte() - eight, scale, output + k);
                Add16(Vector128.ShiftRightLogical(bytes, 4).AsSByte() - eight, scale, output + BlockWeights / 2 + k);
            }
            return;
        }
        for (var j = 0; j < BlockWeights / 2; j++)
        {
            output[j] += c * ((block[j] & 15) - 8);
            output[BlockWeights / 2 + j] += c * ((block[j] >> 4) - 8);
        }
    }

    // Sixteen levels widened to floats, four at a time: output += scale * level.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Add16(Vector128<sbyte> levels, Vector128<float> scale, float* output)
    {
        var lower = Vector128.WidenLower(levels);
        var upper = Vector128.WidenUpper(levels);
        var a = Vector128.WidenLower(lower);
        var b = Vector128.WidenUpper(lower);
        var c = Vector128.WidenLower(upper);
        var d = Vector128.WidenUpper(upper);
        Vector128.Store(Vector128.Load(output) + scale * Vector128.ConvertToSingle(a), output);
        Vector128.Store(Vector128.Load(output + 4) + scale * Vector128.ConvertToSingle(b), output + 4);
        Vector128.Store(Vector128.Load(output + 8) + scale * Vector128.ConvertToSingle(c), output + 8);
        Vector128.Store(Vector128.Load(output + 12) + scale * Vector128.ConvertToSingle(d), output + 12);
    }
}
