// Ptq1PairRepackTests.cs
//
// THE REPACK A SOURCE BYTE AT A TIME WRITES WHAT THE REPACK A WEIGHT AT A TIME WROTE (2026-11-15). On the P30 every layer
// read in place from the model's own file is repacked into the vector kernel's tiles as it streams, and that took 43 of
// the 52 seconds a pass of the 9B spent loading its layers, flash taking the other 9. The repack was rewritten to work a
// source byte at a time. These hold it to the bytes the old one wrote, worked out here again from the layout's own
// definition: column `col` of a block and row `lane` of a tile go to byte col / 8 * 32 + col % 2 * 8 + lane / 8 * 16 +
// lane % 8 of the tile's block, at field (col % 8) / 2, as the weight plus one - every byte cleared first, then ORed.

using CircleAI.Inference.Managed.Kernels;
using Xunit;

namespace CircleAI.Tests;

public sealed class Ptq1PairRepackTests
{
    private const int TileBlockBytes = 512;   // a tile's block: sixteen rows of 128 weights at two bits

    // Every byte value in the source, the ones no ternary file writes as well, and output buffers that held other bytes
    // before: what the repack leaves is the whole of what it was asked for, byte for byte and scale for scale.
    [Theory]
    [InlineData(16, 128, 1, 11)]
    [InlineData(32, 384, 1, 12)]
    [InlineData(48, 1024, 1, 13)]
    [InlineData(128, 256, 4, 14)]   // shared over four threads
    [InlineData(160, 384, 4, 15)]
    public void Each_byte_of_the_tiles_is_what_the_weight_at_a_time_repack_wrote(int rows, int cols, int degree, int seed)
    {
        var blocks = cols / Ptq1.BlockWeights;
        var source = new byte[rows * blocks * Ptq1.BlockBytes];
        new Random(seed).NextBytes(source);
        var weights = new byte[Ptq1PairMatrix.WeightBytes(rows, cols)];
        var scales = new float[Ptq1PairMatrix.ScaleCount(rows, cols)];
        new Random(seed + 100).NextBytes(weights);
        Array.Fill(scales, 12345.678f);

        Ptq1PairMatrix.Repack(source, rows, cols, weights, scales, degree);
        Same(source, rows, cols, weights, scales);

        // And on a pool of the repack's own, as a stream read in place shares it out.
        using var pool = new CircleAI.Inference.Managed.Runtime.WorkerPool(degree);
        new Random(seed + 200).NextBytes(weights);
        Array.Fill(scales, 12345.678f);
        Ptq1PairMatrix.Repack(source, rows, cols, weights, scales, pool);
        Same(source, rows, cols, weights, scales);
    }

    // The tiles and scales a repack left, against what the weight-at-a-time repack wrote.
    private static void Same(byte[] source, int rows, int cols, byte[] weights, float[] scales)
    {
        var blocks = cols / Ptq1.BlockWeights;
        var (expectedWeights, expectedScales) = WeightAtATime(source, rows, cols);
        Assert.Equal(expectedWeights.Length, weights.Length);
        for (var i = 0; i < weights.Length; i++)
            if (weights[i] != expectedWeights[i])
                Assert.Fail($"tile byte {i} (tile {i / TileBlockBytes / blocks}, block {i / TileBlockBytes % blocks}, offset {i % TileBlockBytes}) is {weights[i]:X2}, the weight-at-a-time repack wrote {expectedWeights[i]:X2}");
        Assert.Equal(expectedScales.Select(BitConverter.SingleToInt32Bits), scales.Select(BitConverter.SingleToInt32Bits));
    }

    // The repack as it was: each tile block cleared, then every weight of every row decoded and its code ORed in.
    private static (byte[] Weights, float[] Scales) WeightAtATime(byte[] source, int rows, int cols)
    {
        var blocks = cols / Ptq1.BlockWeights;
        var tiles = rows / Ptq1PairMatrix.TileRows;
        var weights = new byte[(long)tiles * blocks * TileBlockBytes];
        var scales = new float[(long)rows * blocks];
        var trits = new sbyte[Ptq1.BlockWeights];
        for (var tile = 0; tile < tiles; tile++)
            for (var b = 0; b < blocks; b++)
                for (var lane = 0; lane < Ptq1PairMatrix.TileRows; lane++)
                {
                    var row = tile * Ptq1PairMatrix.TileRows + lane;
                    var block = source.AsSpan((row * blocks + b) * Ptq1.BlockBytes, Ptq1.BlockBytes);
                    Ptq1.DecodeScalar(block, trits);
                    var tileBlock = ((long)tile * blocks + b) * TileBlockBytes;
                    for (var col = 0; col < Ptq1.BlockWeights; col++)
                    {
                        var at = tileBlock + col / 8 * 32 + col % 2 * 8 + lane / 8 * 16 + lane % 8;
                        weights[at] |= (byte)((trits[col] + 1) << (col % 8 / 2 * 2));
                    }
                    scales[((long)tile * blocks + b) * Ptq1PairMatrix.TileRows + lane] = Ptq1.Scale(block);
                }
        return (weights, scales);
    }
}
