// CapturePolicyTests.cs
//
// THE CAPTURE LOSES ABOUT 30 dB AND NOBODY KNEW.
//
// Measured on a P30 on 2026-10-12. The same person said "Hey B!" into the same
// phone in the same room twice, four minutes apart:
//
//     the phone's recorder app   peak 0,449   median per-second RMS 0,0446
//     our wake loop's own bytes  peak 0,187   median per-second RMS 0,0014
//
// Our loudest second was 0,0080 - below the 0,0083 an empty room measures. The
// recorder's recording fires the shipped keyword EIGHT times through the
// identical decoder; ours could never have fired at all. Four explanations
// were offered and measured wrong before this - the threshold, the phrase
// spelling, the int8 encoder and the amplitude - because every one of them was
// about the half of the system that was working.
//
// This policy does not fix it. It makes the combinations selectable so the
// question is settled by captures and numbers. These pin the reading of the
// markers, because a mis-read marker means four captures that silently all ran
// the same configuration - which is the one outcome that would waste the test.

using System;
using System.IO;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class CapturePolicyTests
{
    private static string Fresh()
    {
        var d = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void An_empty_directory_leaves_the_product_exactly_as_it_ships()
    {
        var dir = Fresh();
        try
        {
            var p = CapturePolicy.From(dir);

            Assert.True(p.IsDefault);
            Assert.False(p.RawMic);
            Assert.False(p.NoEffects);
            Assert.False(p.NoEchoCanceller);
            Assert.Contains("shipping default", p.Describe(), StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(CapturePolicy.RawMicMarker)]
    [InlineData(CapturePolicy.NoEffectsMarker)]
    [InlineData(CapturePolicy.NoEchoMarker)]
    public void Each_marker_changes_exactly_one_thing(string marker)
    {
        // FOUR CAPTURES THAT SILENTLY RAN THE SAME CONFIGURATION IS THE ONE
        // OUTCOME THAT WOULD WASTE THE TEST, so each switch is asserted to move
        // its own field and nothing else.
        var dir = Fresh();
        try
        {
            File.WriteAllText(Path.Combine(dir, marker), string.Empty);
            var p = CapturePolicy.From(dir);

            Assert.False(p.IsDefault);
            Assert.Equal(marker == CapturePolicy.RawMicMarker, p.RawMic);
            Assert.Equal(marker == CapturePolicy.NoEffectsMarker, p.NoEffects);
            Assert.Equal(marker == CapturePolicy.NoEchoMarker, p.NoEchoCanceller);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_missing_or_unreadable_directory_is_the_shipping_default()
    {
        // This runs while the microphone is being opened. A directory that
        // cannot be read must leave the product listening normally, never stop
        // it listening.
        Assert.True(CapturePolicy.From(null).IsDefault);
        Assert.True(CapturePolicy.From(string.Empty).IsDefault);
        Assert.True(CapturePolicy.From("   ").IsDefault);
        Assert.True(CapturePolicy.From(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid())).IsDefault);
    }

    [Fact]
    public void Every_configuration_describes_itself_differently()
    {
        // A MEASUREMENT WHOSE CONFIGURATION IS NOT WRITTEN BESIDE IT IS NOT A
        // MEASUREMENT. Four captures that look alike in the log and differ in
        // one detached effect is exactly how a day gets wasted - and this
        // session has already lost hours to logs that could not tell two causes
        // apart.
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        foreach (var p in new[]
                 {
                     CapturePolicy.Default,
                     new CapturePolicy(true, false, false),
                     new CapturePolicy(false, true, false),
                     new CapturePolicy(false, false, true),
                     new CapturePolicy(true, true, false),
                 })
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Describe()));
            Assert.True(seen.Add(p.Describe()), "two configurations log the same line: " + p.Describe());
        }
    }

    [Fact]
    public void Detaching_everything_says_so_rather_than_naming_the_echo_canceller()
    {
        // NoEffects already removes the echo canceller, so the narrower switch
        // must not claim credit for it - that would read as "AGC and NS are on"
        // when they are not.
        var all = new CapturePolicy(false, true, false).Describe();
        Assert.Contains("NO effects", all, StringComparison.Ordinal);
        Assert.DoesNotContain("AGC/NS", all, StringComparison.Ordinal);
    }
}
