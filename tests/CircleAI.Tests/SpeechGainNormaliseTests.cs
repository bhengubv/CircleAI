// SpeechGainNormaliseTests.cs
//
// The wake word learned to hear across a room and the transcriber did not.
//
// SpeechGain went into ZipformerWakeWordDetector on 2026-09-06 and stopped
// there. DeviceConversation opens its OWN microphone and hands the bytes
// straight to Whisper, so that path never touched the class the fix lived in -
// and on the same phone, on the same day, 4,7 seconds of speech came back as
// "A-B.".
//
// A finished recording wants a different tool from a live stream. The follower
// has to guess at the future because it is fed one block at a time; a clip that
// has already been captured has no future to guess at, so one multiplier scales
// the whole thing with no pumping and nothing to hear at the seams.
//
// THE SAFETY RULE IS THE SAME ONE, AND IT MATTERS MORE HERE. A clip of an empty
// room, amplified twelvefold, is a clip of amplified nothing - and Whisper does
// not return nothing for it, it hallucinates words into it. Most of this file is
// about silence.

using System;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public class SpeechGainNormaliseTests
{
    /// <summary>16-bit PCM whose loudest sample is exactly <paramref name="peak"/>.</summary>
    private static byte[] Clip(double peak, int samples = 1600, double dutyCycle = 1.0)
    {
        var bytes = new byte[samples * 2];
        var loud = (int)(samples * dutyCycle);
        for (var i = 0; i < samples; i++)
        {
            // A sine so the RMS is a sensible fraction of the peak, silent
            // outside the duty cycle so "mostly quiet" clips can be built.
            var v = i < loud ? (short)(Math.Sin(i * 0.3) * peak * 32767) : (short)0;
            bytes[i * 2] = (byte)(v & 0xFF);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return bytes;
    }

    private static double PeakOf(ReadOnlySpan<byte> pcm)
    {
        var peak = 0;
        for (var i = 0; i < pcm.Length / 2; i++)
        {
            int v = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            var a = v < 0 ? -v : v;
            if (a > peak) peak = a;
        }
        return peak / 32768.0;
    }

    [Fact]
    public void A_quiet_clip_is_lifted_towards_full_scale()
    {
        // THE BUG. This is roughly what arm's length produced on the P30 before
        // the wake word got its gain - and what the transcriber was still being
        // handed afterwards.
        var clip = Clip(peak: 0.08);

        var gain = SpeechGain.Normalise(clip);

        Assert.True(gain > 5, $"a clip peaking at 0,08 was only lifted x{gain:0.##}");
        Assert.InRange(PeakOf(clip), 0.85, 0.95);
    }

    [Fact]
    public void An_empty_room_is_never_amplified()
    {
        // THE RULE THE WHOLE THING LIVES OR DIES BY, and it bites harder here
        // than on the wake path: Whisper does not return silence for amplified
        // silence, it invents words for it.
        var quiet = Clip(peak: 0.008);
        var before = quiet.Clone() as byte[];

        Assert.Equal(1, SpeechGain.Normalise(quiet));
        Assert.Equal(before, quiet);
    }

    [Fact]
    public void Digital_silence_is_left_exactly_alone()
    {
        // A muted microphone yields exact zeros, and a peak of zero is a divide
        // waiting to happen.
        var silence = new byte[3200];

        Assert.Equal(1, SpeechGain.Normalise(silence));
        Assert.All(silence, b => Assert.Equal(0, b));
    }

    [Fact]
    public void An_empty_buffer_is_not_a_crash()
    {
        Assert.Equal(1, SpeechGain.Normalise([]));
        Assert.Equal(1, SpeechGain.Normalise(new byte[1]));   // half a sample
    }

    [Fact]
    public void A_loud_clip_is_left_alone()
    {
        // BOOST ONLY. Somebody speaking close to the phone already decodes;
        // pulling them down would trade a solved problem for a new one.
        var loud = Clip(peak: 0.95);
        var before = loud.Clone() as byte[];

        Assert.Equal(1, SpeechGain.Normalise(loud));
        Assert.Equal(before, loud);
    }

    [Fact]
    public void Nothing_ever_clips()
    {
        // The scaled value is rounded and clamped, not wrapped. A wrapped sample
        // flips sign and reads to the model as a transient that was never spoken.
        var clip = Clip(peak: 0.2);

        SpeechGain.Normalise(clip);

        Assert.True(PeakOf(clip) <= 1.0);
    }

    [Fact]
    public void The_lift_is_capped()
    {
        // Past the cap there is no more speech to recover, only a louder room.
        var barelyAudible = Clip(peak: 0.02);

        Assert.True(SpeechGain.Normalise(barelyAudible, maxGain: 12) <= 12.0001);
    }

    [Fact]
    public void A_mostly_silent_clip_is_judged_on_its_energy_not_its_loudest_moment()
    {
        // THE CASE THAT SEPARATES PEAK FROM RMS. A single click in a second of
        // silence has a healthy PEAK - half full scale - and no speech in it at
        // all. The floor is checked against the clip's ENERGY so that clip is
        // left alone, while the scaling uses the PEAK so that whatever does get
        // lifted cannot clip.
        //
        // The duty cycle is what makes this a click rather than a cough: six
        // loud samples in sixteen thousand is rms 0,008, below the floor. A
        // genuine cough carries enough energy to clear it and is lifted, which is
        // right - it is only the near-empty clip that must be left alone,
        // because Whisper answers amplified silence with invented words.
        var click = Clip(peak: 0.5, samples: 16000, dutyCycle: 0.0004);

        Assert.Equal(1, SpeechGain.Normalise(click));
    }
}
