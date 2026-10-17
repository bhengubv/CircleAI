// DeepestSightingTests.cs
//
// ONE PHRASE HID THE OTHER FOR AN HOUR, AND THE LOG LOOKED FINE.
//
// Measured on a P30 on 2026-10-11. Two wake phrases were registered - "Hey B"
// and "Hey Circle AI" - and the listener confirmed both:
//
//     listening for: Hey B | Hey Circle AI
//
// Every wake phrase in the book opens with the same word, so both branches sit
// at ONE matched token almost constantly. The rule keeping the deepest sighting
// used `>`, so a tie never displaced the incumbent, and the first phrase in the
// file won every single window. An hour of logs named "Hey B" hundreds of times
// and "Hey Circle AI" NOT ONCE - so the question that mattered, "is the long
// phrase progressing or is nobody saying it", had no answer on the device.
//
// That is the same defect as the silence it was added to cure: a report that
// cannot distinguish two causes is not a report. The fix is a sighting per
// phrase; these pin the comparison it rests on.
//
// AND IT IS TESTED BECAUSE IT WAS MOVED. The rule used to live inside a lambda
// in ConfirmedKeywordSpotter's constructor, which needs three ONNX graphs and a
// tokens file - so no test project could reach it, and the tie case went
// unexamined until a phone showed it.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class DeepestSightingTests
{
    private static ZipformerKwsSpotter.KwsProgress Saw(
        string phrase, int matched, int total, double p) => new(phrase, matched, total, p);

    [Fact]
    public void Anything_beats_having_seen_nothing()
    {
        Assert.True(ConfirmedKeywordSpotter.IsDeeper(null, Saw("Hey B", 1, 3, 0.0001)));
    }

    [Fact]
    public void More_tokens_wins_however_unconvincing()
    {
        // DEPTH IS THE EVIDENCE. Two tokens at a terrible score still says the
        // beam walked further into the phrase than one token at a great one, and
        // walking further is the thing being measured.
        var held = Saw("Hey B", 1, 3, 0.9900);
        var seen = Saw("Hey B", 2, 3, 0.0070);

        Assert.True(ConfirmedKeywordSpotter.IsDeeper(held, seen));
        Assert.False(ConfirmedKeywordSpotter.IsDeeper(seen, held));
    }

    [Fact]
    public void Probability_only_settles_a_draw()
    {
        var held = Saw("Hey B", 2, 3, 0.0553);
        Assert.True(ConfirmedKeywordSpotter.IsDeeper(held, Saw("Hey B", 2, 3, 0.8285)));
        Assert.False(ConfirmedKeywordSpotter.IsDeeper(held, Saw("Hey B", 2, 3, 0.0070)));
    }

    [Fact]
    public void An_exact_draw_keeps_what_is_already_held()
    {
        // Free within one phrase - the two sightings say the same thing. It is
        // only across phrases that this silently loses one, which is why the
        // caller keeps one per phrase instead of one overall.
        var held = Saw("Hey B", 2, 3, 0.5);
        Assert.False(ConfirmedKeywordSpotter.IsDeeper(held, Saw("Hey B", 2, 3, 0.5)));
    }

    [Fact]
    public void A_single_best_loses_a_phrase_that_ties_and_a_per_phrase_one_does_not()
    {
        // THE P30 HOUR, REPRODUCED. Both phrases open with "Hey", so both report
        // one matched token; "Hey B" is first in the file and arrives first.
        // Under one overall best, "Hey Circle AI" is never named - which is
        // exactly what the device log showed.
        var frames = new[]
        {
            Saw("Hey B", 1, 3, 0.0007),
            Saw("Hey Circle AI", 1, 8, 0.0007),
            Saw("Hey B", 1, 3, 0.0004),
            Saw("Hey Circle AI", 1, 8, 0.0004),
        };

        ZipformerKwsSpotter.KwsProgress? single = null;
        var each = new Dictionary<string, ZipformerKwsSpotter.KwsProgress>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var f in frames)
        {
            if (ConfirmedKeywordSpotter.IsDeeper(single, f)) single = f;

            each.TryGetValue(f.Phrase, out var mine);
            if (ConfirmedKeywordSpotter.IsDeeper(mine, f)) each[f.Phrase] = f;
        }

        Assert.Equal("Hey B", single!.Phrase);
        Assert.DoesNotContain("Hey Circle AI", new[] { single.Phrase });

        Assert.Equal(2, each.Count);
        Assert.Contains("Hey Circle AI", each.Keys);
        Assert.Contains("Hey B", each.Keys);
    }

    [Fact]
    public void The_long_phrase_is_reported_the_moment_it_outruns_the_short_one()
    {
        // The whole point: once the eight-token phrase walks past two tokens it
        // must be visible, because that is the evidence that it is worth keeping
        // and the short one is not.
        var each = new Dictionary<string, ZipformerKwsSpotter.KwsProgress>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var f in new[]
                 {
                     Saw("Hey B", 2, 3, 0.8285),
                     Saw("Hey Circle AI", 5, 8, 0.4100),
                 })
        {
            each.TryGetValue(f.Phrase, out var mine);
            if (ConfirmedKeywordSpotter.IsDeeper(mine, f)) each[f.Phrase] = f;
        }

        var deepest = each.Values.OrderByDescending(p => p.Matched).First();
        Assert.Equal("Hey Circle AI", deepest.Phrase);
        Assert.Equal(5, deepest.Matched);
    }

    [Fact]
    public void A_sighting_is_never_null()
    {
        Assert.Throws<ArgumentNullException>(
            () => ConfirmedKeywordSpotter.IsDeeper(null, null!));
    }
}
