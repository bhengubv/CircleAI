// EnergyVadDetectorTests.cs
//
// Written because I told the user CircleAI's voice stack "cannot be tested —
// no weights ship". That was wrong, and it came from repeating my own
// unverified note in capabilities.json back as fact.
//
// EnergyVadDetector is pure managed DSP over PCM: no model, no microphone, no
// device. It is fully testable on a build box, and until now nothing tested it.
// (EnergyWakeWordDetector sits directly on top of this and defaults its wake
// word to "hey b" — so the claim that "Hey B" cannot trigger without a
// keyword-spotting model was also wrong. It transcribes and string-matches.)
//
// What genuinely cannot be tested here is real ASR/TTS quality, which needs
// Whisper and ONNX TTS weights that do not ship. That distinction is the point.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading.Tasks;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class EnergyVadDetectorTests
{
    // 16 kHz / 16-bit / mono → 640 bytes == 320 samples == one 20 ms frame.
    private const int SamplesPerFrame = 320;

    /// <summary>
    /// Builds N frames of PCM at a fixed amplitude. Alternating sign each sample
    /// (a Nyquist square wave) so RMS == amplitude exactly — no averaging down,
    /// which keeps the threshold arithmetic in these tests predictable.
    /// </summary>
    private static byte[] Pcm(short amplitude, int frames)
    {
        var samples = frames * SamplesPerFrame;
        var buf = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var v = (i % 2 == 0) ? amplitude : (short)-amplitude;
            BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(i * 2), v);
        }
        return buf;
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Stream(params byte[][] chunks)
    {
        foreach (var c in chunks)
        {
            yield return c;
            await Task.Yield();
        }
    }

    private static async Task<List<VadSegment>> Collect(IAsyncEnumerable<VadSegment> src)
    {
        var list = new List<VadSegment>();
        await foreach (var s in src) list.Add(s);
        return list;
    }

    [Fact]
    public async Task Silence_ProducesNoSpeech()
    {
        var vad = new EnergyVadDetector();
        var segments = await Collect(vad.DetectAsync(Stream(Pcm(0, frames: 40))));

        Assert.DoesNotContain(segments, s => s.IsSpeech);
    }

    [Fact]
    public async Task LoudAudioThenSilence_ProducesASpeechSegment()
    {
        // ~0.49 RMS, far above the 0.02 default threshold, then 20 frames of
        // silence — more than the 15-frame end-of-speech window.
        var vad = new EnergyVadDetector();
        var segments = await Collect(vad.DetectAsync(
            Stream(Pcm(16000, frames: 10), Pcm(0, frames: 20))));

        var speech = segments.FindAll(s => s.IsSpeech);
        Assert.NotEmpty(speech);
        Assert.All(speech, s => Assert.False(s.Audio.IsEmpty));
    }

    [Fact]
    public async Task BelowThresholdHum_IsNotSpeech()
    {
        // Amplitude 300 → RMS ≈ 0.009, under the 0.02 default. A detector that
        // ignored its threshold would emit this as speech.
        var vad = new EnergyVadDetector();
        var segments = await Collect(vad.DetectAsync(
            Stream(Pcm(300, frames: 10), Pcm(0, frames: 20))));

        Assert.DoesNotContain(segments, s => s.IsSpeech);
    }

    [Fact]
    public async Task ThresholdIsHonoured_SameAudioFlipsWithThreshold()
    {
        // The strongest form of the check: hold the audio constant and move only
        // the threshold. Anything that passes both ways is not really gating.
        var audio = new[] { Pcm(300, frames: 10), Pcm(0, frames: 20) };

        var strict  = new EnergyVadDetector(energyThreshold: 0.02f);
        var lenient = new EnergyVadDetector(energyThreshold: 0.001f);

        var strictSegs  = await Collect(strict.DetectAsync(Stream(audio)));
        var lenientSegs = await Collect(lenient.DetectAsync(Stream(audio)));

        Assert.DoesNotContain(strictSegs, s => s.IsSpeech);
        Assert.Contains(lenientSegs, s => s.IsSpeech);
    }

    [Fact]
    public async Task StreamEndingMidSpeech_StillEmitsTheSegment()
    {
        // Documented behaviour: "A final partial segment is emitted when the
        // stream ends mid-speech." Without this, the last utterance before a
        // hang-up would be silently dropped.
        var vad = new EnergyVadDetector();
        var segments = await Collect(vad.DetectAsync(Stream(Pcm(16000, frames: 10))));

        Assert.Contains(segments, s => s.IsSpeech);
    }
}
