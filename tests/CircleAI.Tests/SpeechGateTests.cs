// SpeechGateTests.cs
//
// THE GATE THAT MAKES A 20M MODEL AFFORDABLE ON A PHONE.
//
// The keyword spotter being replaced is 3.3M parameters and can run on every
// frame for ever. Its replacement is six times that, so it only runs on the
// seconds that carry a voice - which on the P30 is a small fraction: measured
// 2026-10-12, the quietest second of that room read 0,00093 RMS and the owner's
// speech read 0,045.
//
// The dangerous half is the hangover. A gate that shuts the moment energy drops
// truncates the last syllable, and a wake phrase losing its final token is
// EXACTLY how this feature failed all day: "Hey B" reached two of its three
// tokens 1,796 times and completed zero. These pin that it stays open.

using System;
using System.Linq;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class SpeechGateTests
{
    private const int Rate = 16_000;
    private const int Block = 1600;            // 100 ms, as the device feeds it

    /// <summary>A block at a stated RMS.</summary>
    private static float[] At(double rms)
    {
        // Alternating +/- gives exactly this RMS and is not silence.
        var b = new float[Block];
        for (var i = 0; i < b.Length; i++) b[i] = (float)(i % 2 == 0 ? rms : -rms);
        return b;
    }

    /// <summary>The P30's own numbers, 2026-10-12.</summary>
    private static float[] QuietRoom() => At(0.00093);
    private static float[] Speech() => At(0.045);

    [Fact]
    public void A_quiet_room_never_reaches_the_model()
    {
        var gate = new SpeechGate();

        for (var i = 0; i < 100; i++)
            Assert.False(gate.Wants(QuietRoom(), Rate));

        Assert.Equal(0, gate.Decoded);
        Assert.Equal(100, gate.Skipped);
        Assert.Equal(0, gate.DutyCycle);
    }

    [Fact]
    public void Speech_opens_it_on_the_very_first_block()
    {
        // THE BLOCK THAT OPENS THE GATE IS ITSELF PASSED. Opening on the NEXT
        // block would drop the attack of the first word, which is the part a
        // recogniser most needs.
        var gate = new SpeechGate();
        Assert.True(gate.Wants(Speech(), Rate));
        Assert.True(gate.Open);
    }

    [Fact]
    public void It_stays_open_through_the_pause_inside_a_phrase()
    {
        // "Hey ... B" has a few hundred milliseconds of nothing in the middle.
        // A gate that shuts there cuts the phrase in half and the transducer
        // never commits its last token - the exact failure measured on the P30.
        var gate = new SpeechGate();

        gate.Wants(Speech(), Rate);
        for (var i = 0; i < 4; i++)                       // 400 ms of pause
            Assert.True(gate.Wants(QuietRoom(), Rate), $"closed after {i * 100} ms of pause");

        Assert.True(gate.Wants(Speech(), Rate));          // "B" still gets through
    }

    [Fact]
    public void It_closes_once_the_hangover_runs_out()
    {
        var gate = new SpeechGate();
        gate.Wants(Speech(), Rate);

        var open = 0;
        for (var i = 0; i < 40; i++)
            if (gate.Wants(QuietRoom(), Rate)) open++;

        Assert.False(gate.Open);
        // One second of hangover at 100 ms a block, give or take the block the
        // sound itself was in.
        Assert.InRange(open, 9, 11);
    }

    [Fact]
    public void A_realistic_minute_costs_a_fraction_of_the_decoding()
    {
        // THE NUMBER THAT SAYS WHETHER THIS WAS WORTH BUILDING. Six wake
        // attempts in a minute, half a second of speech each, in an otherwise
        // quiet room.
        var gate = new SpeechGate();

        for (var second = 0; second < 60; second++)
        {
            var speaking = second % 10 == 0;              // six attempts a minute
            for (var b = 0; b < 10; b++)
                gate.Wants(speaking && b < 5 ? Speech() : QuietRoom(), Rate);
        }

        Assert.True(gate.DutyCycle < 0.70,
            $"the gate passed {gate.DutyCycle:P0} of blocks - it is not saving anything");
        Assert.True(gate.DutyCycle > 0.05,
            $"the gate passed only {gate.DutyCycle:P0} - it is probably cutting speech");
    }

    [Fact]
    public void The_duty_cycle_reports_rather_than_assumes()
    {
        // A gate letting everything through has bought nothing, and that must
        // be visible rather than believed. A silent zero is not a quiet room.
        var gate = new SpeechGate();
        Assert.Equal(0, gate.DutyCycle);                  // nothing seen yet

        for (var i = 0; i < 10; i++) gate.Wants(Speech(), Rate);
        Assert.Equal(1.0, gate.DutyCycle);                // everything passed, and it says so
    }

    [Fact]
    public void An_empty_block_does_not_change_its_mind()
    {
        var gate = new SpeechGate();
        Assert.False(gate.Wants(Array.Empty<float>(), Rate));

        gate.Wants(Speech(), Rate);
        Assert.True(gate.Wants(Array.Empty<float>(), Rate));
    }

    [Fact]
    public void ITS_BOUNDS_BELONG_TO_THE_CAPTURE_PATH_NOT_TO_THE_RECORDER_APP()
    {
        // THE DEFECT THAT ABSOLUTE BOUNDS CAUSED, MEASURED ON 2026-10-12.
        //
        // The bar was clamped between 0,004 and 0,030, from P30 figures of
        // 0,00093 for a quiet room and 0,045 for the owner's speech. Those came
        // through the PHONE'S OWN RECORDER APP. Through the capture path this
        // gate is attached to, the same voice in the same room reads, per-second
        // RMS:
        //
        //     recorder app      room 0,00093   speech median 0,0446   p90 0,0611
        //     our capture path   room 0,0011    speech p90     0,0023  loudest 0,0156
        //
        // So the LOWEST bar the gate would go to sat ABOVE the speech it exists
        // to pass, and nothing anybody said could ever open it. Twelve to
        // twenty-nine decibels, same phone, same voice, different instrument.
        var gate = new SpeechGate();

        Assert.True(gate.QuietestOpening < 0.0023,
            "the bar must be able to go under the quietest speech THIS path delivers");
        Assert.True(gate.QuietestOpening > 0.0011,
            "and must stay over the room it delivers it against");
        Assert.True(gate.LoudestOpening < 0.045,
            "a loud room must never push the bar past speech on the louder instrument either");
    }

    [Fact]
    public void THE_ROOM_AND_THE_VOICE_THIS_PATH_ACTUALLY_DELIVERS_ARE_TOLD_APART()
    {
        // OUR OWN CAPTURE, take2.wav, 2026-10-12: room 0,00143 and speech
        // reaching 0,0053 at its ninetieth percentile. Less than four times
        // between them - which is why NoiseMultiple had to come down from 3.
        var gate = new SpeechGate();
        var room = At(0.00143);
        var voice = At(0.0053);

        for (var i = 0; i < 300; i++) gate.Wants(room, Rate);

        Assert.False(gate.Open, $"the room must not open it (bar {gate.Opening:F5})");
        Assert.True(gate.Wants(voice, Rate), $"the voice must (bar {gate.Opening:F5})");
    }

    [Fact]
    public void A_LOUD_ROOM_RAISES_WHAT_IT_DEMANDS_AND_THAT_IS_THE_WHOLE_POINT()
    {
        // THE DEFECT THIS REPLACED. The first version opened at a hard-coded
        // 0,003 - "three times the quiet room measured on the P30" - and on the
        // P30 at 04:37 that same night it passed 77 to 87 PER CENT of blocks.
        // The room was simply not the room that had been measured, and a
        // constant cannot be two rooms.
        var gate = new SpeechGate();
        var loud = At(0.004);                  // above the old hard-coded 0,003

        // Let it listen to that room for a few seconds with nobody speaking.
        for (var i = 0; i < 300; i++) gate.Wants(loud, Rate);

        Assert.True(gate.Opening > 0.004,
            $"a room at 0,0040 must raise the bar above itself, not sit below it (opens at {gate.Opening:F4})");

        // And the voice still gets through, which is the half that makes it a
        // gate rather than a deaf spot.
        Assert.True(gate.Wants(Speech(), Rate), "the owner's measured voice must still open it");
    }

    [Fact]
    public void A_PERSON_TALKING_NEVER_TEACHES_IT_THAT_TALKING_IS_THE_ROOM()
    {
        // IF IT DID, THE PHONE WOULD GO DEAF TO THE PERSON USING IT - and would
        // report that as a perfectly healthy duty cycle, which is the failure
        // mode this whole class is a reaction to.
        //
        // WHAT SEPARATES A PERSON FROM A TELEVISION IS NOT VOLUME, IT IS THAT
        // THEY STOP. A burst and then quiet is somebody asking for something;
        // sound that never stops is a room. Both can sit at 0,045 RMS, so a
        // rule written on loudness cannot tell them apart and a rule written
        // on duration can.
        var gate = new SpeechGate();
        for (var i = 0; i < 50; i++) gate.Wants(QuietRoom(), Rate);
        var quiet = gate.Opening;

        // Twenty turns of "two seconds of speech, three seconds of quiet".
        for (var turn = 0; turn < 20; turn++)
        {
            for (var i = 0; i < 20; i++) gate.Wants(Speech(), Rate);
            for (var i = 0; i < 30; i++) gate.Wants(QuietRoom(), Rate);
        }

        Assert.Equal(0, gate.Cut);
        Assert.Equal(quiet, gate.Opening, 6);
        Assert.True(gate.Wants(Speech(), Rate), "and it still hears them on the twenty-first");
    }

    [Fact]
    public void THE_ROOM_THE_P30_WAS_ACTUALLY_IN_AT_0452_DOES_NOT_SWAMP_IT()
    {
        // MEASURED, AND IT IS WHY THE FLOOR IS A MEDIAN RATHER THAN A MINIMUM.
        //
        // At 04:52 on 2026-10-12 the adaptive gate reported, correctly:
        //
        //     room 0,0008 opens at 0,0040 | decoded 62 % of them
        //
        // The room really was that quiet at its quietest. It also spent most of
        // its time well above that - a fan, a fridge, distant traffic - so a
        // floor tracking the MINIMUM produced a bar that two blocks in three
        // walked straight over, and the expensive model ran almost
        // continuously. Learning the right number from the wrong statistic
        // looks identical to learning nothing.
        //
        // A median ignores neither the quiet moments nor the loud minority: it
        // lands on what the room usually is.
        var gate = new SpeechGate();

        // That room: typically 0,005, dipping to the measured 0,0008.
        var busy = At(0.005);
        for (var i = 0; i < 900; i++)
            gate.Wants(i % 10 < 3 ? QuietRoom() : busy, Rate);

        var settled = gate.DutyCycle;
        Assert.True(gate.Floor > 0.002,
            $"the floor must find the room it is usually in, not its quietest moment (floor {gate.Floor:F4})");

        // Now measure a fresh minute of that same room with the floor settled.
        gate.Wants(busy, Rate);
        var before = gate.Decoded;
        for (var i = 0; i < 600; i++)
            gate.Wants(i % 10 < 3 ? QuietRoom() : busy, Rate);

        var passed = (gate.Decoded - before) / 600.0;
        Assert.True(passed < 0.15,
            $"this room must cost a fraction of the decoding, not two thirds of it (passed {passed:P0}, was 62 % before)");

        // AND THE OWNER IS STILL HEARD IN IT, which is the half that matters.
        Assert.True(gate.Wants(Speech(), Rate),
            $"measured speech must still open it in this room (opens at {gate.Opening:F4})");
    }

    [Fact]
    public void A_ROOM_THAT_NEVER_STOPS_DOES_TEACH_IT_AND_THAT_IS_THE_WAY_OUT()
    {
        // THE TRAP THE 04:37 MEASUREMENT WAS STUCK IN. In a room loud enough
        // that every block opens the gate, the gate never shuts - so a floor
        // learnt only from quiet blocks can never rise, and 87 per cent of
        // blocks reach the model for ever. Something has to let a continuous
        // room raise the bar, and the stretch that had to be cut is the one
        // honest signal that it IS a room.
        var gate = new SpeechGate();
        for (var i = 0; i < 50; i++) gate.Wants(QuietRoom(), Rate);
        var quiet = gate.Opening;

        // A television left on: thirty unbroken seconds at speech level.
        for (var i = 0; i < 300; i++) gate.Wants(Speech(), Rate);

        Assert.True(gate.Cut > 0, "thirty unbroken seconds must be cut");
        Assert.True(gate.Opening > quiet,
            $"a room that never stops must raise the bar ({quiet:F4} -> {gate.Opening:F4})");

        // AND IT IS STILL NOT DEAF, because the ceiling sits below measured
        // speech. A gate that solved a loud room by refusing every voice has
        // not solved anything.
        Assert.True(gate.Opening < 0.045, "it must still be able to hear a person");
    }

    [Fact]
    public void A_STRETCH_THAT_RUNS_TOO_LONG_IS_CUT_OFF()
    {
        // MEASURED ON THE P30 AT 04:37 ON 2026-10-12. The gate stayed open for
        // minutes in a room that was never quiet, so the recogniser behind it
        // was handed ONE utterance that never ended - and a transducer with no
        // endpoint invents words. It wrote, growing for ever:
        //
        //     YOU SEE THERE YOU ARE TO YOU WHOM MOVED YOU THE DAYS FOLLOWED
        //     UNDER A SPARECT AND TRY YOU ARE MADE ME YOU
        //
        // Nobody said any of that. The danger is not the nonsense: it is that
        // a recogniser inventing words long enough will eventually invent the
        // wake phrase, and a wake word that fires on noise cannot be worked
        // around.
        var gate = new SpeechGate { MostOpen = TimeSpan.FromSeconds(2) };

        // Twelve seconds of unbroken speech-level sound - a television, not a
        // person asking for something.
        var opens = 0;
        for (var i = 0; i < 120; i++)
        {
            gate.Wants(Speech(), Rate);
            if (gate.Opened) opens++;
        }

        Assert.True(gate.Cut >= 5, $"twelve seconds at a two-second limit must be cut (cut {gate.Cut})");

        // AND EACH CUT RE-OPENS, so the audio is still heard - it is broken
        // into utterances rather than thrown away. A gate that cut and then
        // stayed shut would be deaf, which is worse than hallucinating.
        Assert.True(opens >= 5, $"every cut must be followed by a fresh start (opened {opens})");
    }

    [Fact]
    public void IT_REPORTS_THE_BLOCK_THAT_ENDED_A_STRETCH_AND_HOW_LONG_IT_RAN()
    {
        // THE EDGE THAT CARRIES THE ANSWER. A transducer holds its last token
        // until something says the audio has stopped; while blocks keep
        // arriving it is always waiting for more context. So the close is the
        // moment the recogniser must be asked to finish - and if nobody asks,
        // the final token of every phrase stays uncommitted and "Hey B" without
        // its "B" matches nothing.
        //
        // This product has failed that exact way before: the keyword spotter
        // this path replaced reached two of three tokens 1,796 times and
        // completed zero.
        var gate = new SpeechGate();

        for (var i = 0; i < 20; i++)
        {
            gate.Wants(Speech(), Rate);
            Assert.False(gate.Closed, "it is still speaking");
        }

        var closedOn = 0;
        for (var i = 0; i < 30; i++)
        {
            gate.Wants(QuietRoom(), Rate);
            if (gate.Closed) closedOn = i + 1;
        }

        Assert.True(closedOn > 0, "a stretch must report the block that ended it");

        // AND HOW LONG IT RAN, which Passed cannot say because the close zeroes
        // it - a caller reading Passed there prints "0,0 s" for every utterance
        // and the number looks like data rather than an artefact of ordering.
        Assert.True(gate.LastStretch > TimeSpan.Zero);
        Assert.True(gate.LastStretch >= TimeSpan.FromSeconds(2),
            $"two seconds of speech plus a hangover, not {gate.LastStretch.TotalSeconds:F1} s");
    }

    [Fact]
    public void A_cut_stretch_also_reports_that_it_ended()
    {
        // Both ways out of an open stretch are an utterance ending, and only
        // one of them is a person stopping. If a cut did not report the edge,
        // the recogniser would never be asked to finish in exactly the case
        // where it has been fed the most audio.
        var gate = new SpeechGate { MostOpen = TimeSpan.FromSeconds(1) };

        var closes = 0;
        for (var i = 0; i < 60; i++)
        {
            gate.Wants(Speech(), Rate);
            if (gate.Closed) closes++;
        }

        Assert.True(gate.Cut > 0);
        Assert.Equal(gate.Cut, closes);
    }

    [Fact]
    public void A_FRESH_GATE_HEARING_A_QUESTION_MUST_NOT_LEARN_THE_QUESTION_AS_THE_ROOM()
    {
        // THE TURN'S CAPTURE IS NOT THE WAKE LOOP'S, AND THE DIFFERENCE MATTERS.
        //
        // The wake loop listens for hours, so its window is overwhelmingly room
        // and a person speaking for a few seconds never moves the median. The
        // turn opens a gate seconds after "Hi." and the person may start talking
        // at once - so a gate that learns from what it hears fills its window
        // with the QUESTION, takes that as the room, lifts its bar above the
        // speaker, and shuts in the middle of the sentence.
        //
        // That is the 0,64-seconds-of-15 failure measured on the P30 arriving by
        // a different road, so it is pinned before it ships rather than after.
        var gate = new SpeechGate { MostOpen = TimeSpan.FromSeconds(15), LearnsWhileOpen = false };
        var voice = At(0.008);      // this capture path's ordinary speech

        var closedAt = -1;
        for (var i = 0; i < 40; i++)                // four seconds, talking from the first block
        {
            gate.Wants(voice, Rate);
            if (gate.Closed && closedAt < 0) closedAt = i;
        }

        Assert.True(closedAt < 0,
            $"a question still being spoken must not be cut off (closed at block {closedAt}, bar {gate.Opening:F4})");
        Assert.True(gate.Open);

        // And it still ends when they stop, which is the other half.
        for (var i = 0; i < 20; i++) gate.Wants(QuietRoom(), Rate);
        Assert.False(gate.Open, "silence after the question must end it");
    }

    [Fact]
    public void THE_WAKE_LOOP_KEEPS_LEARNING_WHILE_OPEN_BY_DEFAULT()
    {
        // The default stays what the wake loop needs: a room loud enough to hold
        // the gate open must still be able to raise the bar, or 87 per cent of
        // blocks reach the model for ever - the trap measured at 04:37.
        Assert.True(new SpeechGate().LearnsWhileOpen);
    }

    [Fact]
    public void A_CUT_SETS_CLOSED_ON_A_BLOCK_IT_ALSO_PASSED()
    {
        // THE TRAP A CALLER FALLS INTO WITH "else if".
        //
        // A cut happens AFTER the block that reached the limit has been
        // delivered, so on that one block Wants returns TRUE and Closed is
        // true as well. A caller that writes
        //
        //     if (gate.Wants(...)) { feed it }
        //     else if (gate.Closed) { finish the utterance }
        //
        // never finishes a cut stretch - and the cut ones are the LONG ones, so
        // precisely the utterances most likely to contain a wake phrase are the
        // ones silently dropped. Measured on the owner's voice on 2026-10-12:
        // four of seven utterances vanished that way, including "HAY CIRCLED AY
        // HAY CIRCLE A HEY CIRCLE AI".
        var gate = new SpeechGate { MostOpen = TimeSpan.FromSeconds(1) };

        var bothOnOneBlock = false;
        for (var i = 0; i < 40; i++)
        {
            var passed = gate.Wants(Speech(), Rate);
            if (passed && gate.Closed) bothOnOneBlock = true;
        }

        Assert.True(gate.Cut > 0);
        Assert.True(bothOnOneBlock,
            "a cut must report Closed on the block it passed, so callers cannot use else-if safely");
    }

    [Fact]
    public void The_two_edges_never_fire_on_the_same_block()
    {
        // A block cannot both begin and end a stretch, and a caller that both
        // resets and flushes on one block would throw away the utterance it was
        // about to read.
        var gate = new SpeechGate();
        for (var i = 0; i < 200; i++)
        {
            gate.Wants(i % 40 < 10 ? Speech() : QuietRoom(), Rate);
            Assert.False(gate.Opened && gate.Closed);
        }
    }

    [Fact]
    public void It_reports_the_block_that_started_each_stretch()
    {
        // THE SEAM THE RECOGNISER RESETS ON. The silence the gate swallowed
        // never reached the model, so its state describes audio that does not
        // exist and it has to be told to forget. The loop used to infer this
        // edge from two reads of Open, which misses a close and a re-open
        // inside one block.
        var gate = new SpeechGate();

        gate.Wants(Speech(), Rate);
        Assert.True(gate.Opened, "the first loud block starts a stretch");

        gate.Wants(Speech(), Rate);
        Assert.False(gate.Opened, "the second does not start another");
    }

    [Fact]
    public void Reset_closes_it_and_forgets_the_counts()
    {
        var gate = new SpeechGate();
        gate.Wants(Speech(), Rate);
        gate.Reset();

        Assert.False(gate.Open);
        Assert.False(gate.Opened);
        Assert.Equal(0, gate.Decoded);
        Assert.Equal(0, gate.Skipped);
        Assert.Equal(0, gate.Cut);

        // AND THE ROOM, which is the part that is easy to leave behind. A gate
        // carried into a different room still demanding the old one's level is
        // the hard-coded threshold again, wearing a hat.
        Assert.Equal(0, gate.Floor);
    }
}
