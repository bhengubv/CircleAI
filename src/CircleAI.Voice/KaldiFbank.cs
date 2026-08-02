#nullable enable

// KaldiFbank.cs
//
// 80-dimensional log-mel filterbank features, bit-compatible with Kaldi.
//
// WHY NOT THE MEL WE ALREADY HAD. OnnxSpeakerIdentity computes a mel
// spectrogram, and it is a perfectly good generic one: Hamming window, plain
// hop, no pre-emphasis, no DC removal, log(max(x, 1e-10)). Feeding that to a
// Kaldi-trained model produces features that are the right SHAPE and the wrong
// NUMBERS — so the model loads, runs, burns battery, and never fires. Nothing
// errors. That failure looks exactly like "the wake word isn't very good", which
// is why this is written out properly rather than approximated.
//
// The five details that actually decide whether this works, each of which is a
// silent killer on its own:
//
//   high_freq = -400   NEGATIVE means nyquist + high_freq. The top of the mel
//                      range is 7600 Hz, not 8000. Getting this wrong shifts
//                      every filter.
//   snip_edges = false Frames are CENTRED, the first one starts at -120, and
//                      out-of-range samples are MIRRORED, not zero-padded. This
//                      changes both the frame count and the first frames'
//                      contents.
//   x 32768            The model was trained on int16-scaled audio. Float
//                      [-1,1] straight in is 90 dB quiet and every energy is
//                      wrong by a constant that log() turns into an offset.
//   povey window       (0.5 - 0.5cos)^0.85, not Hamming, not Hann.
//   preemph + DC       Per frame, in that order: subtract the frame mean, then
//                      pre-emphasise at 0.97, THEN window.
//
// Defaults are read from sherpa-onnx's FeatureExtractorConfig so they match the
// model this ships for rather than a remembered convention.
//
// Streaming by construction: frame f needs samples [f*160-120, +400), which does
// not depend on how much audio arrives later, so frames are emitted as soon as
// their window is complete. Only the very last frames of a finished utterance
// need the mirrored tail, which is what Flush is for.

using System;
using System.Collections.Generic;

namespace CircleAI.Voice;

/// <summary>Kaldi fbank settings. Defaults match sherpa-onnx.</summary>
/// <param name="SampleRateHz">Input rate. The model was trained at 16 kHz.</param>
/// <param name="NumMelBins">Filterbank size — 80 for the zipformer models.</param>
/// <param name="LowFreqHz">Bottom of the mel range.</param>
/// <param name="HighFreqHz">
/// Top of the mel range. NEGATIVE is relative to nyquist: -400 at 16 kHz means
/// 7600 Hz. This is Kaldi's convention, and it is the single easiest thing here
/// to get wrong by assuming it means 400.
/// </param>
/// <param name="FrameLengthMs">Window length. 25 ms = 400 samples at 16 kHz.</param>
/// <param name="FrameShiftMs">Hop. 10 ms = 160 samples, so 100 frames a second.</param>
/// <param name="PreemphasisCoefficient">First-order high-pass applied per frame.</param>
/// <param name="RemoveDcOffset">Subtract the frame mean before pre-emphasis.</param>
/// <param name="SnipEdges">
/// Kaldi's <c>snip_edges</c>. FALSE (sherpa's default) centres frames and mirrors
/// at the boundaries; true starts frame 0 at sample 0 and drops the tail.
/// </param>
/// <param name="ScaleToInt16">Multiply float samples by 32768 before anything else.</param>
public sealed record KaldiFbankOptions(
    int   SampleRateHz            = 16_000,
    int   NumMelBins              = 80,
    float LowFreqHz               = 20.0f,
    float HighFreqHz              = -400.0f,
    float FrameLengthMs           = 25.0f,
    float FrameShiftMs            = 10.0f,
    float PreemphasisCoefficient  = 0.97f,
    bool  RemoveDcOffset          = true,
    bool  SnipEdges               = false,
    bool  ScaleToInt16            = true)
{
    /// <summary>Window length in samples — 400 at the defaults.</summary>
    public int FrameLength => (int)(SampleRateHz * FrameLengthMs / 1000f);

    /// <summary>Hop in samples — 160 at the defaults.</summary>
    public int FrameShift => (int)(SampleRateHz * FrameShiftMs / 1000f);

    /// <summary>FFT size: the window rounded up to a power of two — 512 for 400.</summary>
    public int PaddedWindow
    {
        get { var n = 1; while (n < FrameLength) n <<= 1; return n; }
    }

    /// <summary>The resolved top of the mel range, with Kaldi's negative convention applied.</summary>
    public float ResolvedHighFreq =>
        HighFreqHz > 0 ? HighFreqHz : SampleRateHz / 2f + HighFreqHz;
}

/// <summary>
/// Streaming Kaldi-compatible fbank. Push samples, pull frames.
/// </summary>
public sealed class KaldiFbank
{
    private readonly KaldiFbankOptions _o;
    private readonly float[]   _window;      // povey
    private readonly float[][] _melBanks;    // [bin][fftBin]
    private readonly int[]     _melStart;    // first non-zero fft bin per mel bin

    private readonly List<float> _samples = new();
    private int _framesRead;

    public KaldiFbank(KaldiFbankOptions? options = null)
    {
        _o = options ?? new KaldiFbankOptions();
        _window = PoveyWindow(_o.FrameLength);
        (_melBanks, _melStart) = MelBanks(_o);
    }

    /// <summary>Feature dimension — one value per mel bin.</summary>
    public int Dimension => _o.NumMelBins;

    /// <summary>Frames whose window is fully covered by the audio received so far.</summary>
    public int FramesReady { get; private set; }

    /// <summary>Adds audio. Samples are float in [-1, 1].</summary>
    public void AcceptWaveform(ReadOnlySpan<float> samples)
    {
        // The int16 scaling belongs HERE, once, before anything reads a sample —
        // the model's training pipeline scaled first and everything downstream
        // (DC offset, energies, the log) inherits the factor.
        var scale = _o.ScaleToInt16 ? 32768f : 1f;
        foreach (var s in samples) _samples.Add(s * scale);
        Recount(flush: false);
    }

    /// <summary>
    /// Marks the end of an utterance, releasing the final frames.
    /// </summary>
    /// <remarks>
    /// Only meaningful with <c>SnipEdges = false</c>, where the last frames read
    /// past the end of the audio and Kaldi mirrors to fill them. Mid-stream they
    /// are held back because a mirrored tail computed from audio that has not
    /// arrived yet would be wrong the moment it does.
    /// </remarks>
    public void Flush() => Recount(flush: true);

    private void Recount(bool flush)
    {
        var n = _samples.Count;
        int frames;
        if (_o.SnipEdges)
        {
            frames = n < _o.FrameLength ? 0 : 1 + (n - _o.FrameLength) / _o.FrameShift;
        }
        else if (flush)
        {
            // Kaldi's count for a complete utterance.
            frames = (n + _o.FrameShift / 2) / _o.FrameShift;
        }
        else
        {
            // Mid-stream: only frames whose window is entirely inside the audio we
            // actually hold. The mirrored tail is deliberately withheld.
            frames = 0;
            while (FirstSampleOf(frames) + _o.FrameLength <= n) frames++;
        }
        FramesReady = Math.Max(0, frames);
    }

    private int FirstSampleOf(int frame) =>
        _o.SnipEdges
            ? frame * _o.FrameShift
            // Centred: midpoint of the frame, minus half a window. Frame 0 starts
            // at -120 and is filled by mirroring.
            : frame * _o.FrameShift + _o.FrameShift / 2 - _o.FrameLength / 2;

    /// <summary>Computes one frame's 80 log-mel values.</summary>
    public float[] GetFrame(int index)
    {
        if (index < 0 || index >= FramesReady)
            throw new ArgumentOutOfRangeException(nameof(index),
                $"frame {index} is not ready ({FramesReady} available)");

        var n     = _samples.Count;
        var start = FirstSampleOf(index);
        var buf   = new float[_o.PaddedWindow];      // zero-padded to the FFT size

        for (var i = 0; i < _o.FrameLength; i++)
        {
            var s = start + i;
            // Kaldi mirrors rather than zero-pads. Looping because a very short
            // utterance can reflect off both ends more than once.
            while (s < 0 || s >= n)
            {
                if (s < 0) s = -s - 1;
                else       s = 2 * n - 1 - s;
            }
            buf[i] = _samples[s];
        }

        // Order matters and is Kaldi's: mean, then pre-emphasis, then window.
        if (_o.RemoveDcOffset)
        {
            float sum = 0;
            for (var i = 0; i < _o.FrameLength; i++) sum += buf[i];
            var mean = sum / _o.FrameLength;
            for (var i = 0; i < _o.FrameLength; i++) buf[i] -= mean;
        }

        if (_o.PreemphasisCoefficient != 0f)
        {
            var c = _o.PreemphasisCoefficient;
            for (var i = _o.FrameLength - 1; i > 0; i--) buf[i] -= c * buf[i - 1];
            buf[0] -= c * buf[0];                       // Kaldi repeats sample 0
        }

        for (var i = 0; i < _o.FrameLength; i++) buf[i] *= _window[i];

        var power = PowerSpectrum(buf);

        var outFrame = new float[_o.NumMelBins];
        for (var m = 0; m < _o.NumMelBins; m++)
        {
            var bank  = _melBanks[m];
            var first = _melStart[m];
            float e = 0;
            for (var k = 0; k < bank.Length; k++) e += power[first + k] * bank[k];
            // float.Epsilon is NOT this — C#'s float.Epsilon is denormal-min
            // (1.4e-45); Kaldi uses numeric_limits<float>::epsilon() (1.19e-7),
            // which is a completely different floor and would change every silent
            // frame's value.
            outFrame[m] = MathF.Log(MathF.Max(e, 1.1920929e-7f));
        }
        return outFrame;
    }

    /// <summary>Drops frames already consumed, so a long session does not grow forever.</summary>
    /// <remarks>
    /// Samples before the earliest still-needed window can go. Kept simple: the
    /// caller says how many frames it has finished with.
    /// </remarks>
    public void Consume(int frames)
    {
        if (frames <= 0) return;
        _framesRead += frames;
        var keepFrom = Math.Max(0, FirstSampleOf(_framesRead));
        if (keepFrom <= 0) return;
        _samples.RemoveRange(0, Math.Min(keepFrom, _samples.Count));
        // Indices are relative to the buffer, so shift the frame origin with it.
        _framesRead = 0;
        Recount(flush: false);
    }

    /// <summary>Clears all audio and frame state, for a new utterance.</summary>
    public void Reset()
    {
        _samples.Clear();
        _framesRead = 0;
        FramesReady = 0;
    }

    // ── the maths ────────────────────────────────────────────────────────────

    private static float[] PoveyWindow(int n)
    {
        var w = new float[n];
        var a = 2 * Math.PI / (n - 1);
        for (var i = 0; i < n; i++)
            w[i] = (float)Math.Pow(0.5 - 0.5 * Math.Cos(a * i), 0.85);
        return w;
    }

    private static float MelScale(float hz) => 1127.0f * MathF.Log(1.0f + hz / 700.0f);

    private static (float[][], int[]) MelBanks(KaldiFbankOptions o)
    {
        var fftBins = o.PaddedWindow / 2;
        var binWidth = (float)o.SampleRateHz / o.PaddedWindow;

        var melLow  = MelScale(o.LowFreqHz);
        var melHigh = MelScale(o.ResolvedHighFreq);
        var delta   = (melHigh - melLow) / (o.NumMelBins + 1);

        var banks = new float[o.NumMelBins][];
        var start = new int[o.NumMelBins];

        for (var m = 0; m < o.NumMelBins; m++)
        {
            var left   = melLow + m * delta;
            var centre = melLow + (m + 1) * delta;
            var right  = melLow + (m + 2) * delta;

            var weights = new List<float>();
            var first = -1;
            for (var i = 0; i < fftBins; i++)
            {
                var mel = MelScale(binWidth * i);
                if (mel <= left || mel >= right)
                {
                    if (first >= 0) break;      // past the triangle
                    continue;
                }
                if (first < 0) first = i;
                weights.Add(mel <= centre
                    ? (mel - left) / (centre - left)
                    : (right - mel) / (right - centre));
            }
            banks[m] = weights.ToArray();
            start[m] = first < 0 ? 0 : first;
        }
        return (banks, start);
    }

    /// <summary>|X[k]|² for k in [0, N/2), via a radix-2 FFT.</summary>
    private static float[] PowerSpectrum(float[] frame)
    {
        var n  = frame.Length;
        var re = (float[])frame.Clone();
        var im = new float[n];

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            var wRe = (float)Math.Cos(ang);
            var wIm = (float)Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                float curRe = 1, curIm = 0;
                for (var j = 0; j < len / 2; j++)
                {
                    var uRe = re[i + j];
                    var uIm = im[i + j];
                    var vRe = re[i + j + len / 2] * curRe - im[i + j + len / 2] * curIm;
                    var vIm = re[i + j + len / 2] * curIm + im[i + j + len / 2] * curRe;
                    re[i + j] = uRe + vRe;  im[i + j] = uIm + vIm;
                    re[i + j + len / 2] = uRe - vRe;  im[i + j + len / 2] = uIm - vIm;
                    var nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }

        var power = new float[n / 2 + 1];
        for (var k = 0; k <= n / 2; k++) power[k] = re[k] * re[k] + im[k] * im[k];
        return power;
    }
}
