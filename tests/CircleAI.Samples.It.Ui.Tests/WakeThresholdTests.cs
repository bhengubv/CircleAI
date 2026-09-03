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
        => Assert.Equal(0.25, ZipformerKwsSpotter.MeasuredThreshold);

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
        double[] measured = [0.244, 0.285, 0.292, 0.304, 0.313, 0.339];

        Assert.True(measured.Any(p => p >= ZipformerKwsSpotter.MeasuredThreshold),
            "the acceptance gate is above every score this phrase produces — it cannot fire");
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
