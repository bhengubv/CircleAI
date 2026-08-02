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
//
// AND IT STILL MISSED THE BUG THAT MATTERED, which is worth recording. Every
// number below was originally generated with the input scaled by 32768 — the
// same wrong assumption the implementation made — so the reference agreed with
// the code and the suite was green while the zipformer produced blank on every
// frame of real speech. A test written from the same misunderstanding as the code
// checks the arithmetic and certifies the mistake.
//
// What was missing was a test of the CONVENTION rather than the computation, so
// TheDefaultIsUnscaledAudio is now here and is the one that would have caught it.
// The general lesson: reference values are only worth what the thing that
// produced them was fed.

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
    // frame, then the first six mel bins as kaldi-native-fbank computes them,
    // fed the SAME [-1, 1] samples this class is given.
    [InlineData(0,   -4.1797f,  -3.5994f,  -3.3748f,  -3.0684f,  -2.6213f,  -2.1660f)]
    [InlineData(25, -11.0873f, -10.1215f, -10.6317f, -11.7107f, -10.7716f,  -9.4176f)]
    [InlineData(49,  -4.1405f,  -3.0494f,  -2.8370f,  -2.6892f,  -2.3516f,  -1.9687f)]
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
    public void TheDefaultIsUnscaledAudio()
    {
        // THE TEST THAT WOULD HAVE CAUGHT IT. sherpa's normalize_samples = true
        // means "these samples are already in [-1, 1], use them as they are" —
        // the x32768 is what happens when that flag is FALSE. Read the other way
        // round, every mel bin gains a constant 2*ln(32768) = 20.794. A uniform
        // offset leaves the features looking entirely plausible — right shape,
        // right dynamic range, right contours — and makes the zipformer emit
        // blank on every single frame of real speech.
        Assert.False(new KaldiFbankOptions().ScaleToInt16);

        var plain = new KaldiFbank();
        plain.AcceptWaveform(Tone());
        plain.Flush();

        var scaled = new KaldiFbank(new KaldiFbankOptions(ScaleToInt16: true));
        scaled.AcceptWaveform(Tone());
        scaled.Flush();

        // Pin the SIZE of the mistake, not just its direction, so this fails
        // loudly rather than drifting if the scaling is ever reintroduced.
        var a = plain.GetFrame(25);
        var b = scaled.GetFrame(25);
        var offset = 2 * Math.Log(32768);       // 20.7944
        // NOT float.Epsilon — that is .NET's smallest denormal (1.4e-45), a
        // different quantity from C's FLT_EPSILON (2^-23) that Kaldi floors on.
        var floor  = Math.Log(Math.Pow(2, -23));   // -15.9424

        // Only the bins that are NOT clamped carry the clean offset. At [-1, 1]
        // a quiet frame pushes a good share of the filterbank onto Kaldi's log
        // floor, where the difference is smaller because the unscaled side has
        // been truncated — kaldi-native-fbank does exactly the same, so this is
        // agreement with the reference and not an approximation of it.
        var clamped = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] <= floor + 1e-4) { clamped++; Assert.True(b[i] - a[i] < offset); }
            else Assert.Equal(offset, b[i] - a[i], 3);
        }
        Assert.True(clamped > 0, "expected some bins on the log floor at [-1,1] scale");

        // And an absolute anchor: [-1, 1] audio lands NEGATIVE through the log.
        Assert.True(a[0] < 0, $"unscaled features must be log-negative, got {a[0]}");
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
