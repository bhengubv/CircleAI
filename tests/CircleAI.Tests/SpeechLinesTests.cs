// SpeechLinesTests.cs
//
// Where the ears close a line. The rule that keeps an update the size of one line, whatever came before it:
// 33 s of speech left them 77 s behind on the P30 on 2026-11-08 without it.

using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class SpeechLinesTests
{
    private const float Voice = 0.012f;
    private const float Room = 0.0018f;

    // Talking: loud with the dips between syllables, none of them long enough to be a pause.
    private static float[] Talking(int frames) =>
        [.. Enumerable.Range(0, frames).Select(i => i % 8 == 7 ? Room : Voice)];

    private static float[] Quiet(int frames) => [.. Enumerable.Repeat(Room, frames)];

    [Fact]
    public void A_short_line_is_left_open_whatever_is_in_it()
    {
        float[] levels = [.. Talking(60), .. Quiet(30), .. Talking(40)];

        Assert.Equal(0, SpeechLines.CutIn(levels));
    }

    [Fact]
    public void A_line_long_enough_closes_in_the_middle_of_a_pause()
    {
        float[] levels = [.. Talking(SpeechLines.Least + 10), .. Quiet(20), .. Talking(30)];

        Assert.Equal(SpeechLines.Least + 20, SpeechLines.CutIn(levels));
    }

    [Fact]
    public void A_gap_between_words_is_not_a_pause()
    {
        float[] levels = [.. Talking(SpeechLines.Least + 10), .. Quiet(SpeechLines.Pause - 2), .. Talking(30)];

        Assert.Equal(0, SpeechLines.CutIn(levels));
    }

    [Fact]
    public void A_pause_before_the_shortest_line_is_passed_over_for_the_one_after_it()
    {
        float[] levels = [.. Talking(50), .. Quiet(30), .. Talking(90), .. Quiet(20), .. Talking(20)];

        Assert.Equal(180, SpeechLines.CutIn(levels));
    }

    [Fact]
    public void Somebody_who_never_pauses_is_still_closed_at_the_longest_line_at_its_quietest_place()
    {
        var levels = Talking(SpeechLines.Most);
        for (var i = 250; i < 256; i++) levels[i] = Room * 1.5f;

        var cut = SpeechLines.CutIn(levels);

        Assert.InRange(cut, 250, 256);
    }

    [Fact]
    public void Short_of_the_longest_line_with_no_pause_it_waits_for_one()
    {
        Assert.Equal(0, SpeechLines.CutIn(Talking(SpeechLines.Most - 1)));
    }

    [Fact]
    public void A_level_that_never_moves_is_closed_at_the_longest_line_too()
    {
        // NOTHING TO TELL WORDS FROM QUIET BY, and an update still has to stay the size of one line.
        var cut = SpeechLines.CutIn(Enumerable.Repeat(Voice, SpeechLines.Most).ToArray());

        Assert.InRange(cut, SpeechLines.Least, SpeechLines.Most);
    }

    [Fact]
    public void A_cut_is_always_inside_the_line()
    {
        float[] levels = [.. Talking(SpeechLines.Most - 15), .. Quiet(15)];

        var cut = SpeechLines.CutIn(levels);

        Assert.InRange(cut, SpeechLines.Least, levels.Length - 1);
    }
}
