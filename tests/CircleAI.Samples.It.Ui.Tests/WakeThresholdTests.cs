// WakeThresholdTests.cs
//
// The number that made the wake word impossible.
//
// The wake word never fired on any build. Not unreliably - ARITHMETICALLY: the
// acceptance gate was 0.5 and the phrase "Hey B" scores 0.24 to 0.34 through the
// air on a P30, with all three of its three tokens matched every time. The model
// heard the whole phrase and the bar sat above every score it can produce.
//
// The value that a working wake word was actually measured at is 0.25, and the
// commit that measured it ("6/6 through air") reached that number by passing
// none at all and taking the spotter's default. Two other places then grew their
// own copy of 0.5, which nothing measured and nothing checked against it.
//
// So this pins the number to its one owner rather than to a literal - a test
// asserting 0.25 in three places would be the same bug in test form.

using CircleAI.Voice;

namespace CircleAI.Samples.It.Ui.Tests;

public class WakeThresholdTests
{
    [Fact]
    public void The_default_is_the_measured_one()
        // A DELIBERATE TRIPWIRE. The literal is here so that moving the gate is
        // a conscious act with a reason attached, rather than a quiet edit that
        // nothing notices - which is how 0.5 arrived twice.
        //
        // Lowered from 0.25 to 0.20 on 2026-09-06: the old value sat INSIDE the
        // measured range 0.24-0.34 rather than below it, so the quietest third
        // of real utterances were refused arithmetically.
        => Assert.Equal(0.20, ZipformerKwsSpotter.MeasuredThreshold);

    [Fact]
    public void A_spotter_built_with_no_threshold_uses_it()
    {
        // The path that measured 6/6 through air passed nothing. Whatever that
        // path gets is the number this app is actually justified in shipping.
        Assert.Equal(ZipformerKwsSpotter.MeasuredThreshold,
                     new ZipformerWakeConfig("bundle").Threshold);
    }

    [Fact]
    public void The_gate_is_below_what_the_phrase_actually_scores()
    {
        // MEASURED ON A P30, THROUGH THE AIR, 3/3 TOKENS MATCHED EVERY TIME:
        //
        //     p = 0,244  0,285  0,292  0,304  0,313  0,339
        //
        // A gate above all of these cannot fire. This is the assertion that would
        // have failed the day 0.5 was introduced.
        // 2026-09-06, same phone, same phrase, after the beam and gate fixes:
        //
        //     fired    0,297  0,369  0,371  0,386
        //     refused  0,246  - three tokens of three, missed by 0,004
        double[] measured =
        [
            0.244, 0.246, 0.285, 0.292, 0.297, 0.304, 0.313, 0.339, 0.369, 0.371, 0.386,
        ];

        // EVERY ONE OF THEM, NOT ANY OF THEM.
        //
        // This assertion used to be Any, and that is why 0.25 survived: 0.244 was
        // already in the list above, sitting BELOW the gate, and the test passed
        // because the other five were above it. "At least one utterance can fire"
        // is not the property anybody wants. The property is that a phrase the
        // model fully recognised is not turned away.
        //
        // It cost a real evening. On 2026-09-06 the spotter matched three tokens
        // of three at 0,246 and the gate refused it by four thousandths, which
        // from the outside is a phone that did not hear you.
        Assert.All(measured, p =>
            Assert.True(p >= ZipformerKwsSpotter.MeasuredThreshold,
                $"a real utterance scored {p:0.000} and the gate is "
                + $"{ZipformerKwsSpotter.MeasuredThreshold:0.000} — that one cannot fire"));
    }

    [Fact]
    public void The_gate_still_sits_well_above_the_noise()
    {
        // THE OTHER SIDE OF LOWERING IT. A gate low enough to catch every real
        // utterance is only sane if it is still far from what a quiet room
        // scores - otherwise the fix for "it never hears me" is "it wakes at
        // nothing", which is worse.
        //
        // Measured in a quiet room on a P30: partial sightings of the phrase
        // score 0 to 0,013, an order of magnitude below the gate. The margin
        // being bought is between somebody speaking softly and a chair moving,
        // not between speech and silence.
        const double loudestNoiseSeen = 0.013;

        Assert.True(ZipformerKwsSpotter.MeasuredThreshold > loudestNoiseSeen * 5,
            "the gate has been lowered to within reach of room noise");
    }

    [Fact]
    public void Half_would_have_been_impossible()
    {
        // Kept as a test rather than a comment: it is the falsification of the
        // shipped value, and it stays true however the default moves.
        double[] measured = [0.244, 0.285, 0.292, 0.304, 0.313, 0.339];

        Assert.DoesNotContain(measured, p => p >= 0.5);
    }

    [Fact]
    public void Calibration_still_wins()
    {
        // A phone that has been measured gets its own number. The fallback is
        // only for the phones nobody has measured - which, so far, is all of them.
        Assert.Equal(0.4, new ZipformerWakeConfig("bundle", Threshold: 0.4).Threshold);
    }
}
