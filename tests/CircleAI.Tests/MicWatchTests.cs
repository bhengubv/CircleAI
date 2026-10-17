// MicWatchTests.cs
//
// 720 LINES AN HOUR, FOREVER, AND IT PUSHED EVERY OTHER DIAGNOSTIC OUT.
//
// The wake word logged "mic alive: N chunks, peak 0.015, mean 0.0018" every five
// seconds. On a P30 with 637 running tasks on 2026-10-04 it was the only thing left
// in the ring buffer: reading a setup line printed thirty seconds earlier took
// clearing the log and restarting the service twice.
//
// The question it answers is still worth answering - without it, a phone listening
// and hearing nothing logs the same as a phone not listening at all, which is the
// state the other head was in when the wake word went deaf and every structural
// check said it was fine. So this backs off instead of going away, and a CHANGE is
// reported immediately.
//
// AND IT IS TESTED BECAUSE IT WAS MOVED. ScreenUpWakeWord is net10.0-android and no
// test project can reference one of those - docs/OPEN-GAPS.md records the same thing
// about ModelChoice: "the one rule that decides what a person is offered was the one
// rule nothing asserted."

using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public sealed class MicWatchTests
{
    private const int Rate = 16_000;
    private const float QuietRoom = 0.015f;   // measured on a P30: peak 0.012-0.023
    private const float DeadMic = 0f;

    /// <summary>Feeds <paramref name="seconds"/> of audio in 100 ms chunks.</summary>
    private static MicSay[] Feed(MicWatch w, double seconds, float peak)
    {
        var said = new System.Collections.Generic.List<MicSay>();
        var chunk = Rate / 10;
        for (var i = 0; i < seconds * 10; i++)
        {
            var s = w.Observe(chunk, peak);
            if (s != MicSay.Nothing) said.Add(s);
        }
        return [.. said];
    }

    [Fact]
    public void The_first_word_comes_within_five_seconds()
    {
        // The prompt proof that the microphone works is the whole point of the line.
        var w = new MicWatch();

        var said = Feed(w, 5.1, QuietRoom);

        Assert.Equal([MicSay.Alive], said);
    }

    [Fact]
    public void An_hour_of_nothing_happening_costs_six_lines_not_seven_hundred()
    {
        // 5 s, 30 s, 2 min, then 10 min: the whole reason for this change.
        var w = new MicWatch();

        var said = Feed(w, 3600, QuietRoom);

        Assert.InRange(said.Length, 4, 12);
        Assert.Equal(MicSay.Alive, said[0]);
        Assert.All(said[1..], s => Assert.Equal(MicSay.StillAlive, s));
    }

    [Fact]
    public void The_old_cadence_would_have_said_it_seven_hundred_times()
    {
        // The number this replaces, stated so the comparison is not folklore.
        const int oldLinesPerHour = 3600 / 5;
        var w = new MicWatch();

        var said = Feed(w, 3600, QuietRoom);

        Assert.Equal(720, oldLinesPerHour);
        Assert.True(said.Length < oldLinesPerHour / 50,
            $"{said.Length} lines an hour is not a fix for {oldLinesPerHour}");
    }

    [Fact]
    public void A_dead_microphone_is_reported_at_once()
    {
        // THE FAILURE THE LINE EXISTS FOR. Zeros arriving is the case that used to
        // be indistinguishable from not listening at all.
        var w = new MicWatch();

        var said = Feed(w, 5.1, DeadMic);

        Assert.Equal([MicSay.WentSilent], said);
    }

    [Fact]
    public void Going_silent_mid_session_is_reported_within_five_seconds()
    {
        // A transition resets the cadence, so a microphone taken away by another app
        // after an hour is not reported ten minutes later.
        var w = new MicWatch();
        Feed(w, 3600, QuietRoom);

        var said = Feed(w, 5.1, DeadMic);

        Assert.Equal([MicSay.WentSilent], said);
    }

    [Fact]
    public void Coming_back_is_reported_within_five_seconds_too()
    {
        var w = new MicWatch();
        Feed(w, 5.1, DeadMic);

        var said = Feed(w, 5.1, QuietRoom);

        Assert.Equal([MicSay.CameBack], said);
    }

    [Fact]
    public void A_microphone_that_stays_dead_keeps_saying_so()
    {
        // "It went quiet an hour ago and has not come back" is the useful reading,
        // and one line an hour earlier would have scrolled away.
        var w = new MicWatch();

        var said = Feed(w, 1800, DeadMic);

        Assert.Equal(MicSay.WentSilent, said[0]);
        Assert.Contains(MicSay.StillSilent, said);
        Assert.All(said[1..], s => Assert.Equal(MicSay.StillSilent, s));
    }

    [Fact]
    public void One_loud_chunk_in_a_window_is_a_working_microphone()
    {
        // THE PEAK IS CARRIED ACROSS THE WINDOW. Asking per chunk would report
        // silence every time somebody stopped talking for a tenth of a second.
        var w = new MicWatch();
        var chunk = Rate / 10;

        // 50 chunks of 1600 samples IS the 5 s probe window, so the loud one goes
        // in early and the 50th call is the one that decides.
        for (var i = 0; i < 49; i++) w.Observe(chunk, i == 17 ? QuietRoom : DeadMic);

        Assert.Equal(MicSay.Alive, w.Observe(chunk, DeadMic));
    }

    [Fact]
    public void A_quiet_room_is_not_mistaken_for_a_dead_microphone()
    {
        // Measured two orders of magnitude apart, so this is not a close call - but
        // it is the call the whole thing rests on.
        Assert.True(QuietRoom > MicWatch.SilenceThreshold * 10);
        Assert.True(DeadMic < MicWatch.SilenceThreshold);
    }

    [Fact]
    public void Nothing_is_said_before_a_window_is_full()
    {
        var w = new MicWatch();

        Assert.Equal(MicSay.Nothing, w.Observe(Rate, QuietRoom));   // 1 s
        Assert.Equal(MicSay.Nothing, w.Observe(Rate, QuietRoom));   // 2 s
    }

    // ── telling "nobody spoke" from "the model heard nothing" ────────────────
    //
    // THE HEARTBEAT ABOVE ANSWERS THE WRONG HALF OF THE QUESTION. It proves the
    // microphone is alive; it cannot say whether the spotter heard anything, and
    // on 2026-10-11 that gap cost a whole device session. A person said "Hey B"
    // several times into a P30, the capture recorded peak 0.453 against a 0.063
    // quiet baseline - so the audio unquestionably arrived - and 157,000 lines of
    // logcat contained no score, no near miss and no detection. Nothing on the
    // phone could say whether the model reached 2 of 3 tokens just under the gate
    // or matched nothing at all, and those two call for opposite fixes.

    /// <summary>Measured on a P30 on 2026-10-11, in the room where it failed.</summary>
    private const float ThatRoomQuiet = 0.063f;

    /// <summary>The same phone, the same room, a person saying the wake phrase.</summary>
    private const float SomebodySpeaking = 0.453f;

    [Fact]
    public void Speech_is_told_apart_from_the_room_it_was_spoken_in()
    {
        Assert.True(SomebodySpeaking > MicWatch.SpeechPeak,
            "a person saying the wake phrase must register as speech");
        Assert.True(ThatRoomQuiet < MicWatch.SpeechPeak,
            "the room that phone was sitting in must not");
    }

    [Fact]
    public void The_speech_line_is_drawn_from_that_rooms_numbers_and_not_the_other_ones()
    {
        // A DIFFERENT NUMBER FROM SilenceThreshold, DELIBERATELY, and somebody
        // tidying them into one would reintroduce the bug. SilenceThreshold
        // separates a dead microphone from a quiet room (0 against 0.015, two
        // orders of magnitude). This separates a quiet room from a voice (0.063
        // against 0.453, less than one). The second line cannot be derived from
        // the first, and that room was four times noisier than the one
        // SilenceThreshold's remarks were measured in.
        Assert.True(MicWatch.SpeechPeak > MicWatch.SilenceThreshold * 50);
        Assert.True(ThatRoomQuiet > MicWatch.SilenceThreshold * 10,
            "the room itself already sits well above the dead-microphone line");
    }

    [Fact]
    public void A_near_miss_is_reported_every_time_rather_than_backed_off()
    {
        // THE BACKOFF IS ON THE REASSURANCE, as this file's own header says. A
        // near miss is the event, not reassurance: a person trying the phrase six
        // times in a minute must leave six lines, and MicWatch's own schedule
        // would have left one. So the gap is short and fixed, and strictly
        // shorter than even the FIRST heartbeat interval.
        var firstHeartbeat = TimeSpan.FromSeconds(new MicWatch().Probe / (double)Rate);

        Assert.True(MicWatch.NearMissGap < firstHeartbeat,
            $"{MicWatch.NearMissGap.TotalSeconds:N1}s is not shorter than the "
            + $"{firstHeartbeat.TotalSeconds:N1}s heartbeat it must not inherit");
        Assert.True(MicWatch.NearMissGap >= TimeSpan.FromSeconds(1),
            "shorter than one attempt at a phrase would split an utterance in two");
    }
}
