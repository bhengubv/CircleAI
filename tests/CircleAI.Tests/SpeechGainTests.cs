// SpeechGainTests.cs
//
// The phone could only be woken from about five centimetres away.
//
// Measured on a P30 on 2026-09-06, with Android's own AutomaticGainControl
// attached and enabled (dumpsys media.audio_flinger lists it by name):
//
//     ~5 cm         peak 0,40-0,59    8 of 8 tokens, wake confirmed
//     arm's length  peak 0,07-0,10    1 of 8 tokens, nothing
//     empty room    peak 0,035        rms 0,0083
//
// A platform effect that is present, enabled and inert cannot be fixed from the
// app, so the gain is applied to the samples instead.
//
// THE RISK IS NOT THE BOOST, IT IS THE FLOOR. An AGC that chases an empty room
// lifts a chair scraping into something with the amplitude of speech, and a wake
// word that fires at nothing is worse than one that needs you close — you can
// walk closer, but you cannot make it stop. Most of this file is about silence.

using System;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public class SpeechGainTests
{
    /// <summary>A block of "speech" at a given RMS, deterministic.</summary>
    private static float[] Block(double rms, int n = 1600)
    {
        var a = new float[n];
        var rnd = new Random(13);
        for (var i = 0; i < n; i++)
            a[i] = (float)(Math.Sin(i * 0.07) + 0.2 * (rnd.NextDouble() - 0.5));

        // Scale to exactly the RMS asked for, so the assertions are about the
        // gain and not about the shape.
        double sum = 0;
        for (var i = 0; i < n; i++) sum += a[i] * (double)a[i];
        var have = Math.Sqrt(sum / n);
        var k = (float)(rms / have);
        for (var i = 0; i < n; i++) a[i] *= k;
        return a;
    }

    private static double Rms(ReadOnlySpan<float> a)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += a[i] * (double)a[i];
        return Math.Sqrt(sum / a.Length);
    }

    /// <summary>Runs enough blocks for the follower to settle.</summary>
    private static double Settle(SpeechGain g, double rms, int blocks = 60)
    {
        var last = 1.0;
        for (var i = 0; i < blocks; i++) last = g.Apply(Block(rms).AsSpan());
        return last;
    }

    [Fact]
    public void An_empty_room_is_never_amplified()
    {
        // THE RULE THE WHOLE CLASS LIVES OR DIES BY. A P30 in a quiet room
        // measures 0,0083 RMS. If that gets lifted twelvefold it arrives at the
        // spotter looking like somebody talking, and the wake word starts firing
        // at furniture.
        var g = new SpeechGain();

        Assert.Equal(1.0, Settle(g, rms: 0.0083), precision: 6);
        Assert.Equal(1.0, g.Current, precision: 6);
    }

    [Fact]
    public void Digital_silence_stays_silent()
    {
        // Not the same test: a muted microphone yields exact zeros, and a
        // divide-by-rms would produce infinity rather than a large number.
        var g = new SpeechGain();
        var quiet = new float[1600];

        for (var i = 0; i < 20; i++) g.Apply(quiet.AsSpan());

        Assert.Equal(1.0, g.Current, precision: 6);
        Assert.All(quiet.ToArray(), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Speech_at_arms_length_is_lifted_towards_the_target()
    {
        // THE MEASURED CASE. Arm's length on the P30 produced 1 of 8 tokens; the
        // confirmed wakes sat around 0,05 RMS. This is the whole point.
        var g = new SpeechGain();
        var applied = Settle(g, rms: 0.008 * 1.5);   // just above the floor, still far too quiet

        Assert.True(applied > 2,
            $"quiet speech was only lifted x{applied:0.##}, which does not buy any distance");

        var block = Block(0.012);
        g.Apply(block.AsSpan());
        Assert.True(Rms(block) > 0.03,
            $"after gain the block is still at rms {Rms(block):0.####}, below what the model wants");
    }

    [Fact]
    public void It_never_makes_anything_quieter()
    {
        // BOOST ONLY. Loud speech already woke the phone, and an AGC that pulled
        // it back down would trade a solved problem for a new one.
        var g = new SpeechGain();

        Assert.True(Settle(g, rms: 0.30) >= 1.0);
        Assert.True(g.Current >= 1.0);
    }

    [Fact]
    public void The_boost_is_capped()
    {
        // Beyond the cap there is no more distance to buy, only clipping — and a
        // clipped consonant is worse for a keyword spotter than a quiet one.
        var g = new SpeechGain { MaxGain = 12 };

        Assert.True(Settle(g, rms: 0.0101) <= 12.0001);
    }

    [Fact]
    public void Nothing_ever_leaves_the_valid_range()
    {
        // Float PCM has no natural ceiling, so a gain applied to an already-loud
        // block could hand the fbank front end samples outside [-1, 1].
        var g = new SpeechGain { MaxGain = 12 };
        Settle(g, rms: 0.011);                    // wind the gain up on quiet audio

        var sudden = Block(0.45);                 // then somebody speaks loudly
        g.Apply(sudden.AsSpan());

        Assert.All(sudden, v => Assert.InRange(v, -1f, 1f));
    }

    [Fact]
    public void The_gain_comes_down_faster_than_it_goes_up()
    {
        // WHY THE FOLLOWER IS ASYMMETRIC. Speech has a loud syllable every few
        // hundred milliseconds. A gain that rises as fast as it falls pumps
        // audibly and, worse, is still winding down through the loudest part of
        // the phrase.
        var up = new SpeechGain();
        var down = new SpeechGain();

        var rose = Settle(up, rms: 0.011, blocks: 4);          // four blocks of climbing
        Settle(down, rms: 0.011);                              // fully wound up
        var beforeFall = down.Current;
        for (var i = 0; i < 4; i++) down.Apply(Block(0.30).AsSpan());
        var fell = beforeFall - down.Current;

        Assert.True(fell > rose - 1,
            $"gain rose {rose - 1:0.##} in four blocks and fell only {fell:0.##} in four");
    }

    [Fact]
    public void Reset_forgets_the_room()
    {
        // The microphone reopening is a new room: whatever was learned about the
        // last one is not evidence about this one.
        var g = new SpeechGain();
        Settle(g, rms: 0.011);
        Assert.True(g.Current > 1);

        g.Reset();
        Assert.Equal(1.0, g.Current, precision: 6);
    }

    // ── The clipping the follower's lag caused ──────────────────────────────
    //
    // Measured on a P30 on 2026-09-08. The follower rides up to x3,6 in a quiet
    // room; somebody then speaks, and Release is per BLOCK - 100 ms at 16 kHz -
    // so it is still carrying x1,8 when the loudest part of the phrase arrives.
    // 0,719 x 1,8 = 1,29, hard limited to 1,0, and the wake model scored the
    // resulting flat-topped waveform 1 token of 8. The same voice reaches 8 of 8
    // through the wake screen, which applies no gain at all.

    [Fact]
    public void A_loud_block_is_never_driven_into_the_limiter()
    {
        // THE BUG, AS A TEST. Ride the gain up on a quiet room, then hand it a
        // loud block the way a person actually arrives: suddenly.
        var g = new SpeechGain();
        for (var i = 0; i < 30; i++) g.Apply(Block(0.012));

        Assert.True(g.Current > 2, $"the follower did not ride up; it is at {g.Current:0.00}");

        var loud = Block(0.05);
        Scale(loud, 0.72);                       // the measured peak of a spoken phrase
        g.Apply(loud);

        Assert.True(Peak(loud) <= 1f,
            $"the block was clipped: peak {Peak(loud):0.000}");
    }

    [Fact]
    public void Nothing_is_flat_topped_at_the_ceiling()
    {
        // Peak <= 1 is not enough on its own: the limiter produces exactly 1,0,
        // so a clipped block passes a peak test while being a square wave. What
        // matters is that no sample was PINNED there.
        var g = new SpeechGain();
        for (var i = 0; i < 30; i++) g.Apply(Block(0.012));

        var loud = Block(0.05);
        Scale(loud, 0.72);
        g.Apply(loud);

        var pinned = 0;
        foreach (var v in loud) if (v >= 0.9999f || v <= -0.9999f) pinned++;

        Assert.True(pinned == 0, $"{pinned} samples were flat-topped by the limiter");
    }

    [Fact]
    public void The_reported_gain_is_the_one_that_was_applied()
    {
        // The log line prints this number. It used to report the follower's
        // internal state, so a block capped from x3,6 to x1,3 still said x3,6 -
        // and the one line that could have shown the clipping hid it instead.
        var g = new SpeechGain();
        for (var i = 0; i < 30; i++) g.Apply(Block(0.012));

        var loud = Block(0.05);
        Scale(loud, 0.72);
        var before = Peak(loud);
        var reported = g.Apply(loud);

        Assert.True(reported * before <= 1.0 + 1e-6,
            $"reported x{reported:0.00} on a peak of {before:0.000} would clip");
    }

    [Fact]
    public void Quiet_speech_is_still_lifted()
    {
        // THE HALF THAT MUST NOT BREAK. The ceiling exists to stop clipping, not
        // to stop the class doing its job - arm's length was the whole reason
        // the gain was added.
        var g = new SpeechGain();

        // BELOW THE TARGET, which is what "quiet" means to this class. An earlier
        // version of this test built a block at the target RMS and then rescaled
        // its PEAK to the measured arm's-length 0,08 - which leaves the RMS above
        // target, where declining to lift is the correct answer. The test was
        // wrong, not the follower.
        var quiet = Block(0.015);
        var applied = g.Apply(quiet);

        Assert.True(applied > 1.0, $"quiet speech was not lifted at all (x{applied:0.00})");
        Assert.True(Peak(quiet) <= 1f, "lifting it clipped it");
    }

    /// <summary>Rescales a block so its loudest sample is exactly <paramref name="peak"/>.</summary>
    private static void Scale(float[] a, double peak)
    {
        var now = Peak(a);
        if (now <= 0) return;
        var k = (float)(peak / now);
        for (var i = 0; i < a.Length; i++) a[i] *= k;
    }

    private static float Peak(float[] a)
    {
        var p = 0f;
        foreach (var v in a) { var x = v < 0 ? -v : v; if (x > p) p = x; }
        return p;
    }
}
