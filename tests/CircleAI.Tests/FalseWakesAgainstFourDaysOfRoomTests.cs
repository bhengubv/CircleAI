// FalseWakesAgainstFourDaysOfRoomTests.cs
//
// THE OTHER HALF OF A WAKE WORD, MEASURED ON A ROOM NOBODY WAS TALKING TO.
//
// Recall has a number: scaling the owner's own recording down in four steps,
// "Hey Circle" woke 9 times where "Hey B" and "Hey Circle AI" woke 2 each.
//
// That number on its own is not a case for changing anything, and
// BuiltInWakePhrases says why, in a comment written before any of this:
//
//     "Hey Circle" would NOT clear the second test: "hey" and "circle" are
//     both everyday words.
//
// A phrase made of everyday words wakes up mid-conversation, and a phone that
// interrupts people cannot be worked around - so a recall win bought with
// false wakes is not a win. The objection is right to exist and it needs its
// own measurement rather than an argument.
//
// WHAT THIS IS MEASURED AGAINST. The service ran on the P30 for FOUR DAYS AND
// FIVE HOURS continuously - 3,629,376 blocks of audio, 765 utterances decoded,
// 853 stretches cut - in a room with people and media in it, where nobody was
// addressing the phone. Exactly one wake fired in that time:
//
//     10-12 06:43:50  HEARD "Hey B" in "HAY B"
//
// So the shipped phrases' false-wake rate over four days of real room is ZERO,
// and that is the bar any replacement has to clear.
//
// THE CORPUS IS THIN AND THIS FILE SAYS SO RATHER THAN ROUNDING IT UP. The
// continuous log capture was not running for the whole four days, so the
// utterances recoverable from it are a few dozen distinct lines, not 765. A
// few dozen is enough to catch a phrase that fires on ordinary speech and NOT
// enough to prove one that does not. It is evidence, not a clearance.

using System;
using System.IO;
using System.Linq;
using CircleAI.Voice;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public sealed class FalseWakesAgainstFourDaysOfRoomTests
{
    private readonly ITestOutputHelper _out;

    public FalseWakesAgainstFourDaysOfRoomTests(ITestOutputHelper output) => _out = output;

    /// <summary>One decoded utterance per line, off the device's own log.</summary>
    /// <remarks>
    /// NOT IN THE REPOSITORY, on purpose: it is a transcript of a person's
    /// home, taken by a microphone that was listening for four days. It lives
    /// where the owner can delete it, and this test skips without it.
    /// </remarks>
    private static string? Corpus =>
        Environment.GetEnvironmentVariable("CIRCLEAI_ROOM_UTTERANCES") is { Length: > 0 } f && File.Exists(f)
            ? f : null;

    [Fact]
    public void NO_CANDIDATE_MAY_WAKE_ON_A_ROOM_NOBODY_WAS_TALKING_TO()
    {
        var path = Corpus;
        if (path is null)
        {
            _out.WriteLine("skipped: set CIRCLEAI_ROOM_UTTERANCES to the decoded-utterance log");
            return;
        }

        var heard = File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();

        var candidates = new[] { "Hey B", "Hey Circle AI", "Hey Circle", "Circle" };

        _out.WriteLine($"{heard.Length} distinct utterances decoded from a room nobody was addressing.");
        _out.WriteLine(string.Empty);
        _out.WriteLine($"{"phrase",-16} {"false wakes",12}");
        _out.WriteLine(new string('-', 34));

        var offenders = new System.Collections.Generic.List<string>();

        foreach (var phrase in candidates)
        {
            var fired = heard.Where(h => PhraseInSpeech.SaidAnywhere(h, phrase)).ToArray();
            _out.WriteLine($"{phrase,-16} {fired.Length,12}");
            foreach (var f in fired) offenders.Add($"  {phrase} <- \"{f}\"");
        }

        if (offenders.Count > 0)
        {
            _out.WriteLine(string.Empty);
            foreach (var o in offenders) _out.WriteLine(o);
        }

        // THE SHIPPED PHRASES ARE THE BASELINE AND THEY ARE CLEAN. Four days of
        // real room, one wake, and it was the owner saying his phrase. If this
        // ever fails, a change has made the product interrupt people.
        Assert.Empty(heard.Where(h => PhraseInSpeech.SaidAnywhere(h, "Hey B")));
        Assert.Empty(heard.Where(h => PhraseInSpeech.SaidAnywhere(h, "Hey Circle AI")));
    }

    [Fact]
    public void THE_BARE_WORD_IS_WHY_A_GREETING_IS_KEPT_IN_FRONT_OF_IT()
    {
        // "Circle" scored the same recall as "Hey Circle" - 9 wakes - so on
        // recall alone the greeting earns nothing and could be dropped. This is
        // what it earns instead.
        //
        // Not from the device corpus, which is too thin to settle it, but from
        // the thing everyone says in an office: a bare distinctive noun appears
        // in ordinary sentences, and a two-word phrase beginning with a
        // greeting does not.
        foreach (var sentence in new[]
                 {
                     "LET ME CIRCLE BACK TO THAT",
                     "WE SAT IN A CIRCLE",
                     "DRAW A CIRCLE ROUND IT",
                     "THE INNER CIRCLE KNEW",
                     "CIRCLE THE DATE",
                 })
        {
            Assert.True(PhraseInSpeech.SaidAnywhere(sentence, "Circle"),
                $"the bare word fires on ordinary speech: {sentence}");
            Assert.False(PhraseInSpeech.SaidAnywhere(sentence, "Hey Circle"),
                $"the greeting is what stops it: {sentence}");
        }
    }
}
