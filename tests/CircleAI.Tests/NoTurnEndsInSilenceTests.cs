// NoTurnEndsInSilenceTests.cs
//
// THE ONLY TEST THAT COULD HAVE CAUGHT IT, BECAUSE THE DEFECT WAS IN A FILE NO
// TEST LEG CAN REFERENCE.
//
// WakeTurn and CircleNeuronLinkService are Android-only, so the net9 and net10
// suites cannot load them, cannot call them, and cannot assert one thing about
// them. That is precisely where the product's worst behaviour lived for a month:
//
//     if (node is null || !node.IsReady)
//     {
//         Log.Warn(Tag, "brain is not ready - nothing to ask");
//         return null;        // <- and the caller returns on a null answer
//     }
//
// A person said "Hey B", heard a hello and a beep, asked their question, and the
// phone made no further sound. Measured on a P30 on 2026-10-10, reported as
// "Voice still doesn't respond to me". The link path had been fixed for the same
// fault the day before; the wake path had no way to inherit it, because the rule
// was a private method on a Service.
//
// So these read the SOURCE. A green suite that does not reference the file is
// this repo's named signature defect - "a library that was written, tested,
// committed, and then had no way in from the app" - and CLAUDE.md says it
// outright: a grep for callers is worth more than a green test run when the
// question is "does this work". This is that grep, run every build.
//
// WHAT THEY REFUSE TO ALLOW: a voice surface deciding for itself what to do
// about a cold brain. There is one owner, it is ColdTurn, and it is covered by
// ColdTurnTests on both legs.

using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace CircleAI.Tests;

public sealed class NoTurnEndsInSilenceTests
{
    private static string Repo()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CircleAI.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string Source(params string[] parts)
    {
        var path = Path.Combine(Repo(), Path.Combine(parts));
        Assert.True(File.Exists(path), path + " has moved - this guard is now pointing at nothing");
        return File.ReadAllText(path);
    }

    private static string WakeTurn()
        => Source("src", "CircleAI", "Assistant.Device", "WakeTurn.cs");

    private static string LinkService()
        => Source("src", "CircleAI", "Device", "CircleNeuronLinkService.cs");

    /// <summary>The file with every comment line removed.</summary>
    /// <remarks>
    /// BOTH FILES NOW QUOTE THE OLD DEFECT IN THEIR OWN DOCUMENTATION - that is
    /// the point of the header comments, so the next person reads why rather than
    /// rediscovering it on a phone. A pattern test that cannot tell the code from
    /// the warning about the code fails on the warning, which would teach somebody
    /// to delete the explanation to get the suite green.
    /// </remarks>
    private static string Code(string text)
    {
        var lines = text.Split('\n');
        var kept = new System.Text.StringBuilder(text.Length);

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal)) continue;
            kept.Append(line).Append('\n');
        }

        return kept.ToString();
    }

    [Fact]
    public void The_wake_turn_puts_its_question_through_the_one_owner()
    {
        Assert.Contains("ColdTurn.AnswerAsync", WakeTurn(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_link_turn_puts_its_question_through_the_same_one()
    {
        // TWO SURFACES, ONE RULE. They had two, and one of them was silence.
        Assert.Contains("ColdTurn.AnswerAsync", LinkService(), StringComparison.Ordinal);
    }

    [Fact]
    public void Neither_surface_keeps_its_own_readiness_branch()
    {
        // THE EXACT LINE THAT WENT QUIET. A readiness check that yields its own
        // answer is a second owner, and a second owner is how these drifted apart
        // in the first place: `!node.IsReady` followed by a return.
        // Allowed inside a lambda handed TO ColdTurn - that is the platform half,
        // and it answers a question rather than ending a turn. What is refused is
        // the statement form that decides the turn itself.
        var branch = new Regex(@"if\s*\([^)]*IsReady[^)]*\)\s*(\r?\n\s*)*\{?\s*(\r?\n\s*)*(return|Log\.)");

        Assert.DoesNotMatch(branch, Code(WakeTurn()));
        Assert.DoesNotMatch(branch, Code(LinkService()));
    }

    [Fact]
    public void The_wake_turn_speaks_as_the_answer_is_written()
    {
        // SpokenReply was built in September for the measured complaint "big gaps
        // between speech input and Circle AI response" and wired into the app's
        // turn loop ONLY. The screen-off turn - the one used without picking the
        // phone up - still waited for the whole answer, then rendered the whole of
        // it, then played it: 21,7 s of thinking and then a render before any
        // sound, on a P30 on 2026-09-09.
        var text = WakeTurn();

        Assert.Contains("new SpokenReply(", text, StringComparison.Ordinal);
        Assert.Contains("mouth.Push(chunk)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_wake_turn_starts_the_brain_on_the_name_and_not_on_the_question()
    {
        // THE CHEAPEST SECONDS IN THE TURN. The greeting, the microphone, the
        // person speaking and Whisper transcribing are 4 to 8 s that were passing
        // with the brain untouched, while a cold load is 13 to 23. Starting at the
        // wake word runs one underneath the other.
        var text = WakeTurn();
        var woke = text.IndexOf("Earcon.Woke()", StringComparison.Ordinal);
        var started = text.IndexOf("CircleNeuronService.Start(", StringComparison.Ordinal);
        var heard = text.IndexOf("HearAsync(ct)", StringComparison.Ordinal);

        Assert.True(woke > 0 && started > 0 && heard > 0, "the turn has been restructured");
        Assert.True(started > woke, "the brain is started before the tone that says it heard you");
        Assert.True(started < heard, "the brain is still only started once the question is in hand");
    }

    [Fact]
    public void Every_sentence_the_voice_says_is_cleaned_first()
    {
        // An asterisk is a syllable. A P30 answered "Tell me a joke" with
        // "**Joke:** Why did the lobster..." on 2026-10-02, and the voice would
        // have said the stars. The cleaning moved to the sentence when the answer
        // started streaming, because there is no later moment at which to do it.
        Assert.Contains("PlainReply.Clean(sentence)", WakeTurn(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_sentence_for_a_brain_that_never_came_up_exists_and_is_sayable()
    {
        // The branch that used to be `return null` now has words.
        Assert.False(string.IsNullOrWhiteSpace(CircleAI.Assistant.AssistantPersona.CouldNotStart));

        // AND ONE PLACE OWNS THEM. The first version of this test asserted that
        // WakeTurn contained the literal "CouldNotStart" and failed - correctly,
        // because WakeTurn does NOT name it and must not. ColdTurn chooses the
        // sentence; a surface that spelled it out again would be the second owner
        // this whole change exists to remove. What each surface owes is that it
        // does not DROP the excuse on the floor, which is a different assertion.
        Assert.Contains("CouldNotStart", Code(Source("src", "CircleAI", "Assistant", "ColdTurn.cs")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Neither_surface_drops_the_excuse_on_the_floor()
    {
        // THE SILENCE CAME BACK IF THIS WERE EVER FALSE. ColdTurn can return an
        // answer OR an excuse, and a caller that reads only the answer and
        // returns on null is exactly the code that was here before - it would
        // compile, pass every other test, and say nothing on the phone.
        Assert.Contains("result.Excuse", Code(WakeTurn()), StringComparison.Ordinal);
        Assert.Contains("served.Excuse", Code(LinkService()), StringComparison.Ordinal);
    }

    [Fact]
    public void The_wake_phrase_file_is_refreshed_before_the_listener_reads_it()
    {
        // A FIX IN THE APK THAT CANNOT REACH THE PHONE IS NOT A FIX.
        //
        // DeviceWakePhrases.EnsureCurrent is documented "Called at every start of
        // resident listening. Idempotent and cheap." It had ZERO callers, so the
        // keyword file was rewritten only when somebody added or chose a phrase,
        // and a device kept whatever spelling the build of that moment produced.
        //
        // Measured on 2026-10-11: the spelling of the owner's own phrase was
        // corrected, built, signed and installed on the P30 - and the log still
        // read `wants ▁B`, because the file had been written by the older build
        // forty minutes earlier and nothing re-wrote it.
        var rlc = Code(Source("src", "CircleAI", "Assistant.Device", "ResidentListeningControl.cs"));

        var refresh = rlc.IndexOf("EnsureCurrent", StringComparison.Ordinal);
        var read = rlc.IndexOf("DeviceWakePhrases.KeywordFile", StringComparison.Ordinal);

        Assert.True(refresh > 0, "nothing refreshes the wake phrase file before it is read");
        Assert.True(read > 0, "the listener no longer reads the keyword file - this guard has moved");
        Assert.True(refresh < read,
            "the file is read before it is refreshed, so the first listen uses the stale spelling");
    }

    [Fact]
    public void The_capture_dump_keeps_exactly_what_the_spotter_was_given()
    {
        // THE ONE THING NEVER TESTED, AND THE REASON FOUR EXPLANATIONS WERE
        // WRONG. A 55,6 s recording of the owner saying "Hey B!", made on the
        // P30 itself, fires EIGHT times through the real decoder in the
        // device's exact configuration - int8 encoder, two-stage confirmer,
        // the shipped keyword - and at the level the wake loop reports, and
        // with or without gain. On the phone it fires zero.
        //
        // Everything that could be varied on the bench has been. What has never
        // been tested is the audio OUR capture produces, because every bench
        // run used a recording made by the phone's recorder app.
        //
        // So the dump must keep the SAME span the spotter is handed, on the
        // same line. A copy taken anywhere else - before a conversion, after a
        // filter, from a second buffer - answers a question nobody asked and
        // sends us round this loop again.
        var text = Code(Source("src", "CircleAI", "Assistant.Device", "ScreenUpWakeWord.cs"));

        // THE DUMP NEAREST THE SPOTTER, not the first in the file. The
        // transcribing loop has its own dump now - deliberately ahead of its
        // gate, so it records what the gate refused as well as what it passed -
        // and a first-occurrence search found that one and measured its distance
        // to a different loop's spotter.
        var scored = text.IndexOf("kws.AcceptWaveform(pcm.AsSpan(0, samples))", StringComparison.Ordinal);
        Assert.True(scored > 0, "the loop no longer feeds the spotter - this guard has moved");

        var kept = text.LastIndexOf("dump?.Keep(pcm.AsSpan(0, samples))", scored, StringComparison.Ordinal);
        Assert.True(kept > 0, "the wake loop keeps no copy of what it heard");
        Assert.True(scored - kept < 400,
            "the dump and the spotter are no longer fed from the same place, so the file "
            + "may not be what was actually scored");
    }

    [Fact]
    public void Recording_a_microphone_is_off_unless_somebody_asks()
    {
        // IT WRITES A MICROPHONE TO DISK. That is defensible as a diagnostic
        // the owner switches on, and indefensible as a default. The switch is
        // the presence of a marker file, which takes a deliberate act to create
        // and leaves visible evidence that it exists.
        var text = Code(Source("src", "CircleAI", "Assistant.Device", "ScreenUpWakeWord.cs"));

        Assert.Contains("CaptureDump.IfAsked", text, StringComparison.Ordinal);
        Assert.DoesNotContain("new CaptureDump(", text, StringComparison.Ordinal);

        // And it goes somewhere the owner can reach without adb, so they can
        // hear it, copy it off, or delete it themselves.
        Assert.Contains("GetExternalFilesDir", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_link_no_longer_describes_itself_in_the_third_person()
    {
        // "Circle AI could not get its brain started on this phone" was written
        // ABOUT the assistant by the assistant, and read aloud by the app on the
        // other end. Every spoken sentence belongs to AssistantPersona, in its own
        // voice - see persona-must-live-where-the-answer-is-built.
        Assert.DoesNotContain("Circle AI could not get its brain started",
            Code(LinkService()), StringComparison.Ordinal);
    }
}
