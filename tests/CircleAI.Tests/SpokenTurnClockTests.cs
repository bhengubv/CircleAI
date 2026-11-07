// SpokenTurnClockTests.cs
//
// A spoken turn is counted from the last word said to the first sound, and the line it leaves in the
// log says where the time between them went.

using System.Diagnostics;
using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public sealed class SpokenTurnClockTests
{
    private long _ticks = 10 * Stopwatch.Frequency;

    private void Pass(int ms) => _ticks += ms * Stopwatch.Frequency / 1000;

    private SpokenTurnClock Clock() => new(() => _ticks);

    [Fact]
    public void A_turn_is_counted_from_the_last_word_not_from_the_microphone_closing()
    {
        var clock = Clock();
        clock.MicrophoneClosed(TimeSpan.FromSeconds(1));
        Pass(500);
        clock.Heard();
        Pass(700);
        clock.FirstText();
        Pass(100);
        clock.FirstPiece(19);
        Pass(300);
        clock.FirstSound();

        Assert.Equal(TimeSpan.FromMilliseconds(2600), clock.ToFirstSound);
        Assert.Equal(
            "spoken turn: 2600 ms from the last word to the first sound: microphone 1000, ears 500, brain 700, to the voice 100, voice 300 (19 chars first); longest silence inside the answer 0 ms",
            clock.Report());
    }

    [Fact]
    public void An_answer_started_on_the_quiet_shows_as_ears_that_finished_before_the_microphone_did()
    {
        var clock = Clock();
        Pass(600);
        clock.Heard();
        Pass(400);
        clock.MicrophoneClosed(TimeSpan.FromSeconds(1));
        Pass(900);
        clock.FirstText();
        clock.FirstPiece(9);
        clock.FirstSound();

        Assert.Equal(TimeSpan.FromMilliseconds(1900), clock.ToFirstSound);
        Assert.Contains("microphone 1000, ears -400, brain 1300", clock.Report());
    }

    [Fact]
    public void A_question_heard_again_counts_its_answer_from_then()
    {
        var clock = Clock();
        clock.Heard();
        clock.MicrophoneClosed(TimeSpan.FromSeconds(1));
        Pass(100);
        clock.FirstText();
        clock.FirstPiece(9);

        Pass(200);
        clock.Heard();
        Pass(700);
        clock.FirstText();
        Pass(100);
        clock.FirstPiece(14);
        Pass(300);
        clock.FirstSound();

        Assert.Equal(TimeSpan.FromMilliseconds(2400), clock.ToFirstSound);
        Assert.Contains("ears 300, brain 700, to the voice 100, voice 300 (14 chars first)", clock.Report());
    }

    [Fact]
    public void The_longest_silence_is_between_the_end_of_one_piece_and_the_start_of_the_next()
    {
        var clock = Clock();
        clock.MicrophoneClosed(TimeSpan.Zero);
        clock.Heard();
        clock.FirstText();
        clock.FirstPiece(4);
        clock.FirstSound();

        clock.Playing(TimeSpan.FromMilliseconds(1000));
        Pass(800);
        clock.Playing(TimeSpan.FromMilliseconds(1000));   // queued behind the first: no silence
        Pass(1600);
        clock.Playing(TimeSpan.FromMilliseconds(500));    // 400 ms after the second ran out
        Pass(700);
        clock.Playing(TimeSpan.FromMilliseconds(500));    // 200 ms after the third

        Assert.EndsWith("longest silence inside the answer 400 ms", clock.Report());
    }

    [Fact]
    public void Only_the_first_piece_and_the_first_sound_count()
    {
        var clock = Clock();
        clock.MicrophoneClosed(TimeSpan.Zero);
        clock.Heard();
        clock.FirstText();
        clock.FirstPiece(4);
        clock.FirstSound();
        Pass(900);
        clock.FirstText();
        clock.FirstPiece(40);
        clock.FirstSound();

        Assert.Equal(TimeSpan.Zero, clock.ToFirstSound);
        Assert.Contains("(4 chars first)", clock.Report());
    }

    [Fact]
    public void A_turn_that_never_made_a_sound_says_so()
    {
        var clock = Clock();
        clock.MicrophoneClosed(TimeSpan.FromSeconds(1));
        clock.Heard();

        Assert.Null(clock.ToFirstSound);
        Assert.Equal("spoken turn: no sound came out", clock.Report());
    }
}
