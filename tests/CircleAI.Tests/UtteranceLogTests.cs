// UtteranceLogTests.cs
//
// THE CORPUS THE NEXT WAKE-PHRASE DECISION RESTS ON.
//
// Choosing a wake phrase needs two numbers and only one of them existed. Recall
// is measurable from a recording of somebody saying the phrase. The other -
// how often it fires on a room that is NOT talking to the phone - needs a
// corpus of what the room says, and the P30 produced 765 utterances over four
// days of which thirty-six survived, because logcat rolls in about four
// minutes. Scored against those thirty-six, all four candidates got zero false
// wakes - and the corpus contains the word "circle" zero times, so the zeros
// meant the corpus could not tell them apart.
//
// These pin the behaviour that stops that happening again: it keeps what was
// said, it stays bounded, and it never breaks the thing it is observing.
//
// AND IT IS OFF UNLESS SOMEBODY ASKED, since the review of 2026-10-23. This is a
// TRANSCRIPT OF A ROOM, and it was being written on any phone that had the streaming
// recogniser on it, from the next start of listening, with nothing said and nothing
// asked - in the external files directory, which on Android 10 other apps can read,
// beside a card promising that the listening is not kept. The marker file that turns
// the audio dump on turns this on too: one deliberate act for the audio and for what
// it decoded to, because they answer the same question and must never be half on.

using System;
using System.IO;
using System.Linq;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class UtteranceLogTests
{
    /// <summary>A directory somebody has asked for speech diagnostics in.</summary>
    private static string Fresh()
    {
        var dir = Unmarked();
        File.WriteAllText(Path.Combine(dir, CaptureDump.MarkerName), "");
        return dir;
    }

    /// <summary>A directory with no marker in it: the state every phone ships in.</summary>
    private static string Unmarked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "utterlog-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void NOTHING_IS_WRITTEN_DOWN_UNLESS_SOMEBODY_ASKED_FOR_IT()
    {
        // The marker is a file a person or an adb command creates on purpose, and its
        // absence is the state every phone ships in.
        var dir = Unmarked();
        try
        {
            var log = new UtteranceLog(dir);
            Assert.Null(log.Path);

            log.Add("WHAT THE ROOM SAID", TimeSpan.FromSeconds(2), When);
            Assert.False(File.Exists(Path.Combine(dir, UtteranceLog.FileName)),
                "a transcript of somebody's home was written with nothing asked");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void WITH_THE_MARKER_IT_RECORDS_AND_SAYS_WHERE()
    {
        var dir = Fresh();
        try
        {
            var log = new UtteranceLog(dir);
            Assert.NotNull(log.Path);
            Assert.Equal(Path.Combine(dir, UtteranceLog.FileName), log.Path);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static readonly DateTimeOffset When =
        new(2026, 10, 16, 12, 46, 42, TimeSpan.FromHours(2));

    [Fact]
    public void It_keeps_what_was_said_with_when_and_how_long()
    {
        var dir = Fresh();
        try
        {
            var log = new UtteranceLog(dir);
            log.Add("HAY B", TimeSpan.FromSeconds(1.9), When);

            var line = File.ReadAllLines(Path.Combine(dir, UtteranceLog.FileName)).Single();

            // THE TIME AND THE LENGTH, not just the words. A phrase that only
            // ever appears inside an eight-second stretch is a television; one
            // that arrives in a two-second burst is a person, and a corpus that
            // cannot tell them apart cannot score a candidate.
            Assert.Contains("2026-10-16 12:46:42", line);
            Assert.Contains("1,9", line.Replace('.', ','));
            Assert.Contains("HAY B", line);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_empty_utterance_is_not_worth_a_line()
    {
        // The loop only calls this when it decoded something, but a gate that
        // closes on silence produces empty strings by the thousand and they
        // would bury the corpus in blanks.
        var dir = Fresh();
        try
        {
            var log = new UtteranceLog(dir);
            log.Add(null, TimeSpan.FromSeconds(1), When);
            log.Add("", TimeSpan.FromSeconds(1), When);
            log.Add("   ", TimeSpan.FromSeconds(1), When);

            Assert.False(File.Exists(Path.Combine(dir, UtteranceLog.FileName)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void IT_STAYS_BOUNDED_ON_A_PHONE_THAT_LISTENS_FOR_MONTHS()
    {
        // This runs for ever by construction. A diagnostic that grows without
        // limit on a device with 3,87 GB and a full swap is a defect, however
        // useful its contents - the last unbounded thing in this feature was
        // the feature buffer, and it reached 904 MB.
        var dir = Fresh();
        try
        {
            var log = new UtteranceLog(dir);
            for (var i = 0; i < UtteranceLog.MostLines + 500; i++)
                log.Add($"UTTERANCE {i}", TimeSpan.FromSeconds(2), When);

            var lines = File.ReadAllLines(Path.Combine(dir, UtteranceLog.FileName));
            Assert.True(lines.Length <= UtteranceLog.MostLines + 100,
                $"{lines.Length} lines kept, cap is {UtteranceLog.MostLines}");

            // AND IT IS THE NEWEST THAT SURVIVE. A corpus trimmed from the
            // wrong end describes a room from a fortnight ago.
            Assert.Contains($"UTTERANCE {UtteranceLog.MostLines + 499}", lines[^1]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_DIRECTORY_IT_CANNOT_WRITE_LEAVES_THE_PHONE_LISTENING()
    {
        // THE RULE FOR EVERY DIAGNOSTIC IN THIS FEATURE. A disk that filled or
        // a path that vanished must not stop a wake word working, and must not
        // throw into a capture loop that has no handler for it.
        var log = new UtteranceLog(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid()));

        Assert.Null(log.Path);
        log.Add("HAY B", TimeSpan.FromSeconds(2), When);   // must not throw

        var nowhere = new UtteranceLog(null);
        Assert.Null(nowhere.Path);
        nowhere.Add("HAY B", TimeSpan.FromSeconds(2), When);
    }

    [Fact]
    public void What_it_writes_can_be_scored_against_a_candidate_phrase()
    {
        // THE WHOLE REASON IT EXISTS. The file has to be readable back into the
        // matcher, or it is a log rather than a corpus.
        var dir = Fresh();
        try
        {
            var log = new UtteranceLog(dir);
            foreach (var heard in new[] { "LET ME CIRCLE BACK TO THAT", "ALL RIGHT", "HAY CIRCLE" })
                log.Add(heard, TimeSpan.FromSeconds(2), When);

            var said = File.ReadAllLines(Path.Combine(dir, UtteranceLog.FileName))
                .Select(l => l.Split('\t')[^1])
                .ToArray();

            Assert.Equal(1, said.Count(h => PhraseInSpeech.SaidAnywhere(h, "Hey Circle")));
            Assert.Equal(2, said.Count(h => PhraseInSpeech.SaidAnywhere(h, "Circle")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
