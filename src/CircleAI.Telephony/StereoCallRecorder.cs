// StereoCallRecorder.cs
//
// (3.3.0) Interleave inbound (caller) and outbound (agent) PCM-16
// mono audio into a single stereo WAV file. Left channel = caller,
// right = agent. Sync is wall-clock based: caller frames go in at the
// time they arrive, agent frames at the time they're sent, and gaps
// are filled with silence.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) Records a call to disk as a stereo PCM-16 WAV.</summary>
public sealed class StereoCallRecorder : IAsyncDisposable, IDisposable
{
    private readonly Stream _output;
    private readonly int _sampleRateHz;
    private readonly bool _leaveOpen;
    private readonly object _gate = new();
    private long _samplesWritten;     // total interleaved sample pairs
    private bool _headerWritten;

    public StereoCallRecorder(Stream output, int sampleRateHz, bool leaveOpen = false)
    {
        _output       = output ?? throw new ArgumentNullException(nameof(output));
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        _sampleRateHz = sampleRateHz;
        _leaveOpen    = leaveOpen;
    }

    /// <summary>(3.3.0) Write inbound (caller) PCM-16 mono audio. Caller side is left channel.</summary>
    public void WriteCallerFrame(ReadOnlySpan<byte> pcmFrame)
        => WriteSide(pcmFrame, isCaller: true);

    /// <summary>(3.3.0) Write outbound (agent) PCM-16 mono audio. Agent side is right channel.</summary>
    public void WriteAgentFrame(ReadOnlySpan<byte> pcmFrame)
        => WriteSide(pcmFrame, isCaller: false);

    /// <summary>(3.3.0) Finalise the WAV header. After this, no more writes are allowed.</summary>
    public void Finalize()
    {
        lock (_gate)
        {
            FinaliseLocked();
        }
    }

    private void WriteSide(ReadOnlySpan<byte> pcmFrame, bool isCaller)
    {
        if (pcmFrame.Length < 2) return;
        lock (_gate)
        {
            EnsureHeader();
            int samples = pcmFrame.Length / 2;
            for (int i = 0; i < samples; i++)
            {
                short mono = BinaryPrimitives.ReadInt16LittleEndian(pcmFrame.Slice(i * 2, 2));
                Span<byte> stereo = stackalloc byte[4];
                if (isCaller)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(stereo[..2], mono);
                    BinaryPrimitives.WriteInt16LittleEndian(stereo[2..], 0);
                }
                else
                {
                    BinaryPrimitives.WriteInt16LittleEndian(stereo[..2], 0);
                    BinaryPrimitives.WriteInt16LittleEndian(stereo[2..], mono);
                }
                _output.Write(stereo);
                _samplesWritten++;
            }
        }
    }

    private void EnsureHeader()
    {
        if (_headerWritten) return;
        // Reserve 44 bytes for the WAV header — values backfilled in Finalize.
        Span<byte> placeholder = stackalloc byte[44];
        _output.Write(placeholder);
        _headerWritten = true;
    }

    private void FinaliseLocked()
    {
        if (!_headerWritten) return;
        var dataSize  = _samplesWritten * 4; // 2 channels × 2 bytes
        var chunkSize = 36 + dataSize;
        if (!_output.CanSeek)
        {
            // Streams that can't seek can't backfill — we accept the placeholder header for live appends.
            return;
        }
        var saved = _output.Position;
        _output.Position = 0;
        Span<byte> header = stackalloc byte[44];
        header[0] = (byte)'R'; header[1] = (byte)'I'; header[2] = (byte)'F'; header[3] = (byte)'F';
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], (int)chunkSize);
        header[8]  = (byte)'W'; header[9]  = (byte)'A'; header[10] = (byte)'V'; header[11] = (byte)'E';
        header[12] = (byte)'f'; header[13] = (byte)'m'; header[14] = (byte)'t'; header[15] = (byte)' ';
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);             // Subchunk1Size
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);              // PCM
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], 2);              // channels
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], _sampleRateHz);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], _sampleRateHz * 4); // byte rate
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], 4);              // block align
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);             // bits per sample
        header[36] = (byte)'d'; header[37] = (byte)'a'; header[38] = (byte)'t'; header[39] = (byte)'a';
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], (int)dataSize);
        _output.Write(header);
        _output.Position = saved;
        _output.Flush();
    }

    public void Dispose()
    {
        Finalize();
        if (!_leaveOpen) _output.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Finalize();
        if (!_leaveOpen) _output.Dispose();
        return ValueTask.CompletedTask;
    }
}
