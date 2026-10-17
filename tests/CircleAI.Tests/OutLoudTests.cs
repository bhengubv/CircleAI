// OutLoudTests.cs
//
// THE ASSISTANT MUST NOT LISTEN TO ITSELF.
//
// This is not a hypothetical. On 2026-10-12 the wake loop's own microphone
// capture was pulled off the P30 and run back through the recogniser, and it
// read:
//
//     " I AM USING THE SMALLER BRAIN THE BIG ONE STOPPED THIS PHONE SO I LEFT
//       IT ALONE"
//
// Nobody said that to the phone. That is the phone's own text-to-speech, out of
// its own speaker, into its own microphone, transcribed perfectly by the wake
// recogniser - which means the wake word was one unlucky sentence away from
// triggering on the assistant saying its own name.
//
// The fact was already known in one place: AndroidAudioPlayer waits out its own
// playback, with a comment saying this is "how a device hears its own voice and
// answers itself". It covered the turn's microphone. The wake loop is a second
// microphone, always open, and nothing told it. One fact, one owner, and a wire
// between them - and these tests are the wire's guarantee, because the five
// previous instances of this defect in this feature were all found on a phone.

using System;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

[Collection("OutLoud")]
public sealed class OutLoudTests : IDisposable
{
    private DateTimeOffset _now = new(2026, 10, 12, 5, 0, 0, TimeSpan.Zero);

    public OutLoudTests()
    {
        OutLoud.Reset();
        OutLoud.Clock = () => _now;
    }

    public void Dispose()
    {
        OutLoud.Reset();
        OutLoud.Clock = () => DateTimeOffset.UtcNow;
        OutLoud.Settle = TimeSpan.FromMilliseconds(400);
    }

    private void Wait(TimeSpan d) => _now += d;

    [Fact]
    public void Nothing_is_out_loud_to_begin_with()
    {
        // Otherwise a phone that has never spoken starts out deaf, and the only
        // symptom is a wake word that never fires.
        Assert.False(OutLoud.Now);
        Assert.Equal(0, OutLoud.Spoken);
    }

    [Fact]
    public void While_it_speaks_the_ears_are_shut()
    {
        using (OutLoud.While())
            Assert.True(OutLoud.Now);
    }

    [Fact]
    public void They_stay_shut_for_a_moment_after_it_stops()
    {
        // THE ROOM KEEPS THE SOUND. The tail of a sentence arrives off the
        // walls after the speaker has gone quiet, and a fragment of the
        // assistant with nothing in front of it is exactly the shape of a wake
        // phrase - so the dangerous moment is just AFTER it finishes.
        using (OutLoud.While()) { }

        Assert.True(OutLoud.Now, "the reverberant tail is still in the room");

        Wait(TimeSpan.FromMilliseconds(200));
        Assert.True(OutLoud.Now);

        Wait(TimeSpan.FromMilliseconds(300));
        Assert.False(OutLoud.Now, "and then the person gets their microphone back");
    }

    [Fact]
    public void A_person_answering_straight_away_is_still_heard()
    {
        // The settle time is a tradeoff and this is the side of it that must
        // not be lost: somebody replying to the assistant immediately is the
        // normal case, not an edge case.
        using (OutLoud.While()) { }
        Wait(OutLoud.Settle + TimeSpan.FromMilliseconds(1));

        Assert.False(OutLoud.Now);
    }

    [Fact]
    public void TWO_OVERLAPPING_SENTENCES_DO_NOT_RE_OPEN_THE_EARS_EARLY()
    {
        // COUNTED, NOT A FLAG. The moment anything speaks two things in
        // parallel, a flag set by the first one's end re-opens the microphone
        // while the second is still playing - and the assistant hears the
        // second half of its own sentence.
        var first = OutLoud.While();
        var second = OutLoud.While();

        first.Dispose();
        Assert.True(OutLoud.Now, "the second sentence is still playing");

        second.Dispose();
        Assert.True(OutLoud.Now);

        Wait(OutLoud.Settle + TimeSpan.FromMilliseconds(1));
        Assert.False(OutLoud.Now);
    }

    [Fact]
    public void DISPOSING_TWICE_CANNOT_LEAVE_IT_DEAF_FOR_EVER()
    {
        // THE ONE FAILURE HERE THAT CANNOT BE NOTICED BY LISTENING. A double
        // dispose that decremented twice would drive the count negative, and
        // every later check would say "still speaking" for the life of the
        // process - a phone that is simply deaf, with nothing in any log to
        // say why.
        var mouth = OutLoud.While();
        mouth.Dispose();
        mouth.Dispose();

        Wait(OutLoud.Settle + TimeSpan.FromMilliseconds(1));
        Assert.False(OutLoud.Now);

        using (OutLoud.While())
            Assert.True(OutLoud.Now, "and it still works afterwards");
    }

    [Fact]
    public void It_counts_what_it_said_so_a_zero_can_be_told_apart_from_a_gap()
    {
        // A wake loop reporting "0 blocks skipped while speaking" means either
        // the assistant never spoke or this was never wired, and those two look
        // the same. The count is what separates them.
        Assert.Equal(0, OutLoud.Spoken);

        using (OutLoud.While()) { }
        using (OutLoud.While()) { }

        Assert.Equal(2, OutLoud.Spoken);
    }

    [Fact]
    public void AUDIO_HANDED_TO_ANOTHER_PROCESS_KEEPS_THIS_ONE_DEAF_FOR_ITS_LENGTH()
    {
        // MEASURED ON THE P30 ON 2026-10-16. The service rendered an answer and
        // the app played it - in the app's process, marking the app's copy of
        // this static class. The wake loop lives in the service, saw "0
        // sentences", and transcribed the answer straight back: "I AM HAPPY AND
        // THE TEMPERATURE IS WARM ... TWENTY FIVE DEGREES".
        //
        // The service knows exactly how long the audio it hands over lasts, and
        // the app plays it at once, so that is how long it stays deaf.
        OutLoud.For(TimeSpan.FromSeconds(3));

        Assert.True(OutLoud.Now, "the answer is playing");
        Wait(TimeSpan.FromSeconds(2));
        Assert.True(OutLoud.Now, "and still is");

        // Through the room's settle after it ends, as with local playback.
        Wait(TimeSpan.FromMilliseconds(1200));
        Assert.True(OutLoud.Now, "the tail is still in the room");

        Wait(OutLoud.Settle);
        Assert.False(OutLoud.Now, "and then the person gets the microphone back");
    }

    [Fact]
    public void A_SHORTER_HANDOVER_NEVER_CUTS_A_LONGER_ONE_SHORT()
    {
        // A second sentence handed over while the first is still playing must
        // not shorten the first - the speaker is still talking.
        OutLoud.For(TimeSpan.FromSeconds(5));
        Wait(TimeSpan.FromSeconds(1));
        OutLoud.For(TimeSpan.FromMilliseconds(500));

        Wait(TimeSpan.FromSeconds(2));
        Assert.True(OutLoud.Now, "the first, longer sentence is still playing");
    }

    [Fact]
    public void A_handover_counts_as_something_said_and_reset_forgets_it()
    {
        // Counted, so a "0 sentences" in the wake loop's log means nothing was
        // said rather than that this was never wired.
        OutLoud.For(TimeSpan.FromSeconds(1));
        Assert.Equal(1, OutLoud.Spoken);

        OutLoud.Reset();
        Assert.False(OutLoud.Now);
        Assert.Equal(0, OutLoud.Spoken);
    }

    [Fact]
    public void The_settle_time_is_long_enough_for_a_room_and_short_enough_for_a_person()
    {
        // Pinned so neither side is quietly traded away: too short and the
        // assistant hears its own tail, too long and it stops hearing replies.
        Assert.True(OutLoud.Settle >= TimeSpan.FromMilliseconds(250));
        Assert.True(OutLoud.Settle <= TimeSpan.FromMilliseconds(800));
    }
}
