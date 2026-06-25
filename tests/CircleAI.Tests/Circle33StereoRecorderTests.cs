// Circle33StereoRecorderTests.cs
//
// (3.3.0) Tests for stereo call recorder.

using System;
using System.Buffers.Binary;
using System.IO;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33StereoRecorderTests
{
    [Fact]
    public void Constructor_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new StereoCallRecorder(null!, 16000));
    }

    [Fact]
    public void Constructor_InvalidSampleRate_Throws()
    {
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => new StereoCallRecorder(ms, 0));
    }

    [Fact]
    public void WriteCallerFrame_ProducesStereoWavLeftChannel()
    {
        using var ms = new MemoryStream();
        using (var rec = new StereoCallRecorder(ms, 16000, leaveOpen: true))
        {
            var mono = new byte[] { 0x10, 0x20, 0x30, 0x40 }; // 2 samples
            rec.WriteCallerFrame(mono);
        }
        var bytes = ms.ToArray();
        Assert.Equal(44 + 2 * 4, bytes.Length); // header + 2 stereo samples
        // First sample: left = 0x2010, right = 0
        Assert.Equal(0x10, bytes[44]);
        Assert.Equal(0x20, bytes[45]);
        Assert.Equal(0,    bytes[46]);
        Assert.Equal(0,    bytes[47]);
    }

    [Fact]
    public void WriteAgentFrame_LandsInRightChannel()
    {
        using var ms = new MemoryStream();
        using (var rec = new StereoCallRecorder(ms, 16000, leaveOpen: true))
        {
            var mono = new byte[] { 0x10, 0x20 }; // 1 sample
            rec.WriteAgentFrame(mono);
        }
        var bytes = ms.ToArray();
        Assert.Equal(0,    bytes[44]);
        Assert.Equal(0,    bytes[45]);
        Assert.Equal(0x10, bytes[46]);
        Assert.Equal(0x20, bytes[47]);
    }

    [Fact]
    public void Finalize_WritesValidWavHeader()
    {
        using var ms = new MemoryStream();
        using (var rec = new StereoCallRecorder(ms, 16000, leaveOpen: true))
        {
            rec.WriteCallerFrame(new byte[] { 0x10, 0x20, 0x30, 0x40 });
            rec.WriteAgentFrame(new byte[]  { 0x50, 0x60 });
            rec.Finalize();
        }
        var bytes = ms.ToArray();
        Assert.Equal((byte)'R', bytes[0]);
        Assert.Equal((byte)'I', bytes[1]);
        Assert.Equal((byte)'F', bytes[2]);
        Assert.Equal((byte)'F', bytes[3]);
        // channels at offset 22, expected 2 (stereo).
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(22, 2)));
        // sample rate at offset 24, expected 16000.
        Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24, 4)));
    }

    [Fact]
    public void EmptyFrames_DoNotProduceData()
    {
        using var ms = new MemoryStream();
        using (var rec = new StereoCallRecorder(ms, 16000, leaveOpen: true))
        {
            rec.WriteCallerFrame(ReadOnlySpan<byte>.Empty);
            rec.WriteAgentFrame(ReadOnlySpan<byte>.Empty);
        }
        // No header written either since neither side actually wrote samples.
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void Recorder_LeaveOpen_DoesNotDisposeStream()
    {
        using var ms = new MemoryStream();
        var rec = new StereoCallRecorder(ms, 16000, leaveOpen: true);
        rec.WriteCallerFrame(new byte[] { 0x10, 0x20 });
        rec.Dispose();

        // Should still be usable.
        ms.WriteByte(0xFF);
        Assert.Equal(44 + 4 + 1, ms.Length);
    }
}
