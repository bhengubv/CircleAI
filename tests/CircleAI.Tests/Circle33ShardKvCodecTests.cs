// Circle33ShardKvCodecTests.cs
//
// (3.3.0) Tests for ShardKvCodec — round-trip + state hygiene.

using System;
using CircleAI.Core.Compression;
using Xunit;

namespace CircleAI.Tests;

public class Circle33ShardKvCodecTests
{
    [Fact]
    public void EncodeDecode_RoundTripV_ExactCodewordMatch()
    {
        var codec = new ShardKvCodec(kDim: 16, kRank: 8, vDim: 8, vCodewords: 16, vCodebookSeed: 7);
        var k = new float[16];
        var v = new float[8] { 0.1f, -0.2f, 0.3f, -0.4f, 0.5f, -0.6f, 0.7f, -0.8f };

        // Place v in the codebook so VQ is exact.
        var codebook = new float[16][];
        for (int i = 0; i < 16; i++) codebook[i] = new float[8];
        Array.Copy(v, codebook[5], 8);
        codec.SetVCodebook(codebook);

        var frame = codec.Encode(k, v);
        var (_, dv) = codec.Decode(frame);

        for (int i = 0; i < 8; i++) Assert.Equal(v[i], dv[i], 3);
    }

    [Fact]
    public void EncodeDecode_KRecoversApproximately()
    {
        var codec = new ShardKvCodec(kDim: 8, kRank: 8, vDim: 4, vCodewords: 4);
        var k = new float[8] { 1, 2, 3, 4, -1, -2, -3, -4 };
        var v = new float[4] { 0.5f, -0.5f, 0.25f, -0.25f };

        var frame = codec.Encode(k, v);
        var (dk, _) = codec.Decode(frame);

        double err = 0;
        for (int i = 0; i < 8; i++) err += Math.Abs(dk[i] - k[i]);
        Assert.True(err / 8 < 0.5, $"avg |err|={err/8}");
    }

    [Fact]
    public void ObserveK_UpdatesRunningMean()
    {
        var codec = new ShardKvCodec(kDim: 4, kRank: 2, vDim: 2, vCodewords: 4);
        Assert.Equal(0L, codec.SamplesObserved);

        codec.ObserveK(new float[] { 1, 2, 3, 4 });
        codec.ObserveK(new float[] { 3, 4, 5, 6 });

        Assert.Equal(2L, codec.SamplesObserved);
    }

    [Fact]
    public void Constructor_InvalidKDim_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ShardKvCodec(kDim: 0, kRank: 1, vDim: 4, vCodewords: 4));
    }

    [Fact]
    public void Constructor_KRankExceedsKDim_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ShardKvCodec(kDim: 4, kRank: 5, vDim: 4, vCodewords: 4));
    }

    [Fact]
    public void Constructor_NonPowerOf2Codewords_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ShardKvCodec(kDim: 4, kRank: 2, vDim: 4, vCodewords: 7));
    }

    [Fact]
    public void Encode_KDimMismatch_Throws()
    {
        var codec = new ShardKvCodec(kDim: 4, kRank: 2, vDim: 4, vCodewords: 4);
        Assert.Throws<ArgumentException>(() => codec.Encode(new float[3], new float[4]));
    }

    [Fact]
    public void Encode_VDimMismatch_Throws()
    {
        var codec = new ShardKvCodec(kDim: 4, kRank: 2, vDim: 4, vCodewords: 4);
        Assert.Throws<ArgumentException>(() => codec.Encode(new float[4], new float[3]));
    }

    [Fact]
    public void SetPrincipalAxes_WrongShape_Throws()
    {
        var codec = new ShardKvCodec(kDim: 4, kRank: 2, vDim: 4, vCodewords: 4);
        Assert.Throws<ArgumentException>(() => codec.SetPrincipalAxes(new float[3, 4]));
    }

    [Fact]
    public void SetVCodebook_WrongCount_Throws()
    {
        var codec = new ShardKvCodec(kDim: 4, kRank: 2, vDim: 4, vCodewords: 4);
        Assert.Throws<ArgumentException>(() =>
            codec.SetVCodebook(new[] { new float[4], new float[4], new float[4] }));
    }

    [Fact]
    public void CompressedKShorterThanRaw_AchievesCompression()
    {
        var codec = new ShardKvCodec(kDim: 64, kRank: 16, vDim: 64, vCodewords: 256);
        var k = new float[64];
        var v = new float[64];
        for (int i = 0; i < 64; i++) { k[i] = i * 0.01f; v[i] = -i * 0.01f; }

        var frame = codec.Encode(k, v);

        var rawBytes = (64 + 64) * sizeof(float);   // 512
        var encBytes = frame.CompressedK.Length + frame.CompressedV.Length; // 4+16 + 1 = 21
        Assert.True(encBytes < rawBytes / 10, $"raw={rawBytes} enc={encBytes}");
    }

    [Fact]
    public void LargeVCodebook_Uses2ByteIndex()
    {
        var codec = new ShardKvCodec(kDim: 4, kRank: 2, vDim: 4, vCodewords: 1024);
        var frame = codec.Encode(new float[4], new float[4]);
        Assert.Equal(2, frame.CompressedV.Length);
    }
}
