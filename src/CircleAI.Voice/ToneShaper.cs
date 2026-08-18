#nullable enable

// ToneShaper.cs
//
// Warmth, after the model has finished.
//
// THE VOICE WAS REPORTED AS TINNY, AND THE SPEAKER COULD NOT FIX IT. Choosing a
// speaker by how well the recogniser understands it has a bias nobody costed:
// word error rate rewards crisp consonants and a bright top end, which is what
// "tinny" describes. Swapping speaker 129 for 128 cut the word error rate from
// 0.47 to 0.09 and moved the spectral centre of mass from 298 Hz to 437 Hz,
// taking the energy below 500 Hz from 92% to 64%. Clearer, and thinner.
//
// Measured across all 130 speakers in the bundle, warmth and intelligibility are
// inversely related: the darkest voices (sid 19 at 277 Hz, 94% body) are the ones
// the recogniser loses (WER 0.49-0.64). Only sid 66 cleared both bars, and it is
// 23 Hz warmer than 128 — not enough to hear.
//
// So the speaker is not the lever. The waveform is, and it is entirely ours once
// the model hands it over. Measured on sid 128, five takes each:
//
//     variant             centroid    body    WER
//     baseline               437 Hz   64.0%   0.12
//     low shelf +5 dB        351 Hz   74.4%   0.13
//     presence dip -5 dB     402 Hz   66.1%   0.12
//     BOTH, gentler          346 Hz   73.9%   0.12
//     10% slower             424 Hz   64.0%   0.16
//
// The pair together recovers most of the warmth of the voice that was replaced —
// 346 Hz against 129's 325 Hz — and costs nothing the recogniser can detect.
// Slowing the speech was tried and only hurt.
//
// WHY A DIP AND NOT JUST A BOOST. A phone speaker cannot move enough air to
// reproduce a low-shelf boost; on a P30 the bass simply is not there to lift.
// Cutting 2-5 kHz, where harshness lives, works on hardware that cannot do bass,
// which is most of the hardware this ships to. The boost is for headphones. Both
// are applied because the product is used on both.

using System;

namespace CircleAI.Voice;

/// <summary>Gentle tone correction applied to synthesised speech.</summary>
/// <remarks>
/// Two RBJ biquads — a low shelf and a peaking dip — run in series over the
/// float waveform before it becomes PCM. Cheap enough not to matter: two
/// multiply-accumulates per sample against a vocoder that just spent seconds
/// producing it.
/// </remarks>
public sealed class ToneShaper
{
    /// <summary>Where the low shelf starts lifting, in Hz.</summary>
    public double LowShelfHz { get; init; } = 320;

    /// <summary>How much to lift the bottom, in dB.</summary>
    public double LowShelfDb { get; init; } = 4.0;

    /// <summary>Centre of the harshness dip, in Hz.</summary>
    public double PresenceHz { get; init; } = 3200;

    /// <summary>How much to cut there, in dB. Negative cuts.</summary>
    public double PresenceDb { get; init; } = -4.0;

    /// <summary>Width of the dip. Lower is wider.</summary>
    public double PresenceQ { get; init; } = 0.8;

    /// <summary>The measured setting: warmer, with no cost to intelligibility.</summary>
    public static ToneShaper Warm { get; } = new();

    /// <summary>Filters <paramref name="waveform"/> in place.</summary>
    /// <remarks>
    /// PEAK IS RESTORED AFTERWARDS. Lifting the low shelf adds energy, and a
    /// waveform that already peaked near full scale would clip — which is heard
    /// as crackle and would be blamed on the quantised model rather than on this.
    /// Scaling back to the original peak keeps the tone change audible and the
    /// level unchanged.
    /// </remarks>
    public void Apply(float[] waveform, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(waveform);
        if (waveform.Length == 0 || sampleRate <= 0) return;

        var before = Peak(waveform);
        if (before <= 0f) return;

        LowShelf(sampleRate, out var b, out var a);
        Biquad(waveform, b, a);

        Peaking(sampleRate, out b, out a);
        Biquad(waveform, b, a);

        var after = Peak(waveform);
        if (after > 0f && after > before)
        {
            var g = before / after;
            for (var i = 0; i < waveform.Length; i++) waveform[i] *= g;
        }
    }

    static float Peak(float[] x)
    {
        var p = 0f;
        foreach (var v in x) { var a = Math.Abs(v); if (a > p) p = a; }
        return p;
    }

    /// <summary>Direct-form-I biquad, in place.</summary>
    static void Biquad(float[] x, double[] b, double[] a)
    {
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (var i = 0; i < x.Length; i++)
        {
            double xn = x[i];
            var yn = b[0] * xn + b[1] * x1 + b[2] * x2 - a[1] * y1 - a[2] * y2;
            x2 = x1; x1 = xn;
            y2 = y1; y1 = yn;
            x[i] = (float)yn;
        }
    }

    // Coefficients from the RBJ audio cookbook, normalised by a0.
    void LowShelf(int rate, out double[] b, out double[] a)
    {
        const double slope = 0.9;
        var A = Math.Pow(10, LowShelfDb / 40);
        var w0 = 2 * Math.PI * LowShelfHz / rate;
        var alpha = Math.Sin(w0) / 2 * Math.Sqrt((A + 1 / A) * (1 / slope - 1) + 2);
        var c = Math.Cos(w0);
        var s2 = 2 * Math.Sqrt(A) * alpha;

        b = [A * ((A + 1) - (A - 1) * c + s2),
             2 * A * ((A - 1) - (A + 1) * c),
             A * ((A + 1) - (A - 1) * c - s2)];
        a = [(A + 1) + (A - 1) * c + s2,
             -2 * ((A - 1) + (A + 1) * c),
             (A + 1) + (A - 1) * c - s2];
        Normalise(b, a);
    }

    void Peaking(int rate, out double[] b, out double[] a)
    {
        var A = Math.Pow(10, PresenceDb / 40);
        var w0 = 2 * Math.PI * PresenceHz / rate;
        var alpha = Math.Sin(w0) / (2 * PresenceQ);
        var c = Math.Cos(w0);

        b = [1 + alpha * A, -2 * c, 1 - alpha * A];
        a = [1 + alpha / A, -2 * c, 1 - alpha / A];
        Normalise(b, a);
    }

    static void Normalise(double[] b, double[] a)
    {
        var a0 = a[0];
        for (var i = 0; i < 3; i++) { b[i] /= a0; a[i] /= a0; }
    }
}
