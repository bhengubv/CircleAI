// SpeechGain.cs
//
// The phone could only be woken from about five centimetres away.
//
// WHY SOFTWARE HAS TO DO THIS. Android offers AutomaticGainControl as a platform
// effect and AndroidAudio attaches it. On a P30 on 2026-09-06 it attached
// SUCCESSFULLY — dumpsys media.audio_flinger lists "Automatic Gain Control",
// enabled — and lifted nothing measurable:
//
//     ~5 cm         peak 0,40-0,59    8 of 8 tokens, wake confirmed
//     arm's length  peak 0,07-0,10    1 of 8 tokens, nothing
//     empty room    peak 0,035
//
// A platform effect that is present, enabled and inert is indistinguishable from
// one that is absent, and neither can be fixed from here. So the gain is applied
// to the samples on their way to the spotter.
//
// WHY IT MATTERS SO MUCH TO THIS MODEL. Speech falls off with the square of
// distance, so a voice at 50 cm arrives with a hundredth of the power it has at
// 5 cm. The features the spotter scores are log-mel energies, and a waveform
// that quiet produces energies far below anything in the training data — the
// model is not mishearing the phrase, it is being shown something that does not
// look like speech at all.
//
// BOOST ONLY, AND NEVER SILENCE. Two rules keep this from being the cure that is
// worse:
//
//   - gain is never below 1. Loud speech already works, and attenuating it would
//     trade a fixed problem for a new one.
//   - below the noise floor the gain goes to 1 and stays there. An AGC that
//     chases an empty room amplifies a chair moving into something with the
//     amplitude of speech, and the wake word starts firing at nothing — which is
//     a worse failure than not hearing you, because it cannot be worked around.

using System;

namespace CircleAI.Voice;

/// <summary>Lifts quiet speech to the level the wake model was trained on.</summary>
/// <remarks>
/// Stateful and single-threaded by design: it is driven from the capture loop,
/// one block at a time, and its whole purpose is to remember what the last block
/// sounded like.
/// </remarks>
public sealed class SpeechGain
{
    /// <summary>The RMS this aims to bring speech up to.</summary>
    /// <remarks>
    /// Read off the two confirmed wakes on a P30 on 2026-09-06, which measured
    /// rms 0,021 and 0,042 across their five-second windows — and those windows
    /// are mostly silence, so the speech inside them sits higher. 0,05 is the
    /// bottom of what worked rather than an average of it: aiming at the middle
    /// of a range that only just worked would leave half of it still failing.
    /// </remarks>
    public double Target { get; init; } = 0.05;

    /// <summary>The most it will ever multiply by.</summary>
    /// <remarks>
    /// 12x is about 22 dB, which turns the measured arm's-length peak of 0,08
    /// into 0,96 — the top of the useful range and no further. A larger ceiling
    /// does not buy more distance, it just guarantees clipping on the next loud
    /// syllable, and a clipped consonant is worse for a keyword spotter than a
    /// quiet one.
    /// </remarks>
    public double MaxGain { get; init; } = 12;

    /// <summary>Below this RMS, nothing is amplified.</summary>
    /// <remarks>
    /// The empty room on a P30 measures 0,0083 RMS. This sits just above it, so
    /// an idle room is passed through at unity and only something louder than
    /// the room is ever lifted. THIS IS THE SAFETY RULE — see the header.
    /// </remarks>
    public double NoiseFloor { get; init; } = 0.010;

    /// <summary>How fast the gain rises and falls, per block.</summary>
    /// <remarks>
    /// FALLS FASTER THAN IT RISES. Coming up slowly means a sudden loud sound is
    /// not amplified before the gain has noticed it; coming down quickly means
    /// the gain gets out of the way of that sound within a couple of blocks
    /// rather than clipping through it. The asymmetry is what stops the pumping
    /// that a symmetric follower produces on speech, which has a loud syllable
    /// every few hundred milliseconds.
    /// </remarks>
    public double Attack { get; init; } = 0.15;

    /// <inheritdoc cref="Attack"/>
    public double Release { get; init; } = 0.45;

    private double _gain = 1;

    /// <summary>The multiplier last applied.</summary>
    public double Current => _gain;

    /// <summary>Forget everything, as if the microphone had just opened.</summary>
    public void Reset() => _gain = 1;

    /// <summary>Lifts one block in place, and says what it multiplied by.</summary>
    /// <returns>The gain applied to this block.</returns>
    public double Apply(Span<float> pcm)
    {
        if (pcm.Length == 0) return _gain;

        double sum = 0;
        for (var i = 0; i < pcm.Length; i++) sum += pcm[i] * (double)pcm[i];
        var rms = Math.Sqrt(sum / pcm.Length);

        // AN EMPTY ROOM IS LEFT ALONE. Not "gain 1 this block" but a target of 1
        // that the follower moves towards, so coming out of silence does not step
        // the level.
        var wanted = rms < NoiseFloor ? 1 : Math.Clamp(Target / rms, 1, MaxGain);

        _gain += (wanted - _gain) * (wanted < _gain ? Release : Attack);
        if (_gain < 1) _gain = 1;

        // Unity is the common case in a quiet room and multiplying by it is
        // pointless work on the capture thread.
        if (_gain <= 1.0001) return _gain;

        var g = (float)_gain;
        for (var i = 0; i < pcm.Length; i++)
        {
            // HARD LIMIT, NOT WRAP. Float PCM has no natural ceiling, and the
            // fbank front end downstream does not care that a sample reads 1,4 -
            // but the spotter's own peak reporting does, and so does anything
            // that later writes this to 16-bit.
            var v = pcm[i] * g;
            pcm[i] = v > 1f ? 1f : v < -1f ? -1f : v;
        }

        return _gain;
    }
}
