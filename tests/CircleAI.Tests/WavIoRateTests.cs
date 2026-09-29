// WavIoRateTests.cs
//
// The one duplicated constant the CircleAI.Audio split created, guarded.
//
// WavIo.ReadMono24k exists to turn a reference recording into the samples
// PocketTtsEngine clones a voice from, so its target rate IS that engine's sample
// rate. It used to say so in code - `private const int TargetRate =
// PocketTtsEngine.SampleRate;` - and it cannot any more: PocketTtsEngine is an ONNX
// engine in CircleAI.Voice, which now references CircleAI.Audio. Reading it back
// would be a cycle, and referencing the engine assembly from the audio one would put
// ONNX Runtime into every client that opens a WAV file, which is the whole reason
// the split exists.
//
// So the number is written twice, and a number written twice is a number that
// drifts. This is the check that a comment cannot be.

using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public class WavIoRateTests
{
    [Fact]
    public void Reference_audio_is_read_at_the_rate_the_cloning_engine_wants()
    {
        // 24 kHz worth of silence, read back at PocketTtsEngine's own rate: the
        // sample COUNT is what proves WavIo's target rate, because a mismatch
        // resamples and the length changes with it.
        var seconds = 1;
        var samples = new float[PocketTtsEngine.SampleRate * seconds];
        var wav = System.IO.Path.GetTempFileName() + ".wav";

        try
        {
            System.IO.File.WriteAllBytes(wav, Wave(WavIo.ToPcm16(samples), PocketTtsEngine.SampleRate));

            var read = WavIo.ReadMono24k(wav);

            Assert.Equal(PocketTtsEngine.SampleRate * seconds, read.Length);
        }
        finally
        {
            try { System.IO.File.Delete(wav); } catch { }
        }
    }

    /// <summary>A minimal RIFF/WAVE container around mono PCM-16.</summary>
    private static byte[] Wave(byte[] pcm, int rate)
    {
        using var ms = new System.IO.MemoryStream();
        using var w = new System.IO.BinaryWriter(ms);

        w.Write("RIFF"u8.ToArray());
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);                       // PCM header size
        w.Write((short)1);                 // PCM
        w.Write((short)1);                 // mono
        w.Write(rate);
        w.Write(rate * 2);                 // byte rate: mono, 16-bit
        w.Write((short)2);                 // block align
        w.Write((short)16);                // bits per sample
        w.Write("data"u8.ToArray());
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();

        return ms.ToArray();
    }
}
