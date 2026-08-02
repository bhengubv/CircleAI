// KaldiFbankTests.cs
//
// The reference values here were produced by kaldi-native-fbank — the actual C++
// implementation the KWS model was trained against — on a deterministic synthetic
// signal, so this test needs no audio file and cannot drift with one.
//
// It exists because this is the component whose failure is INVISIBLE. Features
// that are the right shape and the wrong numbers make a model load, run, consume
// battery and never fire. Nothing throws. There is no log line. It presents as
// "the wake word isn't very good", and the last place anyone looks is the
// arithmetic that ran correctly.
//
// Verified separately over a real 6.6 s utterance: 663 frames x 80 bins against
// the same C++ reference, max absolute difference 0.001084, mean 4.4e-6 — float
// accumulation-order noise, not a semantic gap.

using System;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public class KaldiFbankTests
{
    /// <summary>440 Hz + 1 kHz, half a second — deterministic, no file needed.</summary>
    private static float[] Tone(int samples = 8000, int rate = 16_000)
    {
        var x = new float[samples];
        for (var i = 0; i < samples; i++)
        {
            var t = (double)i / rate;
            x[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * t)
                         + 0.25 * Math.Sin(2 * Math.PI * 1000 * t));
        }
        return x;
    }

    [Fact]
    public void FrameCountMatchesKaldi()
    {
        // snip_edges=false: (samples + shift/2) / shift = (8000 + 80) / 160 = 50.
        // With snip_edges=true it would be 48, and being two frames out shifts
        // every downstream chunk boundary.
        var fb = new KaldiFbank();
        fb.AcceptWaveform(Tone());
        fb.Flush();

        Assert.Equal(50, fb.FramesReady);
        Assert.Equal(80, fb.Dimension);
    }

    [Theory]
    // frame, then the first six mel bins as kaldi-native-fbank computes them.
    [InlineData(0,  16.6147f, 17.1950f, 17.4196f, 17.7260f, 18.1731f, 18.6284f)]
    [InlineData(25,  9.7071f, 10.6730f, 10.1627f,  9.0838f, 10.0228f, 11.3768f)]
    [InlineData(49, 16.6539f, 17.7450f, 17.9574f, 18.1052f, 18.4428f, 18.8257f)]
    public void FeaturesMatchTheCppReference(int frame, params float[] expected)
    {
        var fb = new KaldiFbank();
        fb.AcceptWaveform(Tone());
        fb.Flush();

        var got = fb.GetFrame(frame);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], got[i], 3);   // 3 dp — well inside float noise
    }

    [Fact]
    public void TheHighFrequencyCutoffIsRelativeToNyquist()
    {
        // Kaldi's negative-means-relative convention. -400 at 16 kHz is 7600 Hz,
        // NOT 400 Hz. Read as absolute it would squeeze all 80 filters into the
        // bottom of the spectrum and every feature would be wrong.
        Assert.Equal(7600f, new KaldiFbankOptions().ResolvedHighFreq, 3);

        // A positive value is taken as given.
        Assert.Equal(4000f, new KaldiFbankOptions(HighFreqHz: 4000f).ResolvedHighFreq, 3);
    }

    [Fact]
    public void TheFftIsThePowerOfTwoAboveTheWindow()
    {
        // 25 ms at 16 kHz is 400 samples, padded to 512.
        var o = new KaldiFbankOptions();
        Assert.Equal(400, o.FrameLength);
        Assert.Equal(160, o.FrameShift);
        Assert.Equal(512, o.PaddedWindow);
    }

    [Fact]
    public void StreamingInSmallBitesMatchesOneBigPush()
    {
        // The microphone delivers 10-100 ms at a time. If the streaming path and
        // the whole-buffer path disagree, the thing works in every test and fails
        // on the phone — which is the only place it matters.
        var tone = Tone();

        var whole = new KaldiFbank();
        whole.AcceptWaveform(tone);
        whole.Flush();

        var streamed = new KaldiFbank();
        for (var i = 0; i < tone.Length; i += 1600)
            streamed.AcceptWaveform(tone.AsSpan(i, Math.Min(1600, tone.Length - i)));
        streamed.Flush();

        Assert.Equal(whole.FramesReady, streamed.FramesReady);
        for (var f = 0; f < whole.FramesReady; f++)
        {
            var a = whole.GetFrame(f);
            var b = streamed.GetFrame(f);
            for (var i = 0; i < a.Length; i++) Assert.Equal(a[i], b[i], 4);
        }
    }

    [Fact]
    public void FramesAreWithheldUntilTheirWindowIsComplete()
    {
        // Mid-stream, a frame whose window runs past the audio we hold would be
        // computed from mirrored samples that later arrive for real — so it must
        // not be emitted until Flush says the utterance is over.
        var fb = new KaldiFbank();
        fb.AcceptWaveform(Tone(1600));      // 100 ms

        var midStream = fb.FramesReady;
        fb.Flush();

        Assert.True(fb.FramesReady > midStream,
            "Flush must release the mirrored tail frames that streaming holds back");
    }
}
