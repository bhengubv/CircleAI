// UtteranceLog.cs
//
// EVERY UTTERANCE THE WAKE LOOP DECODES, KEPT LONG ENOUGH TO BE COUNTED.
//
// WHY A FILE WHEN THERE IS ALREADY A LOG LINE. Because logcat is a ring of a
// few megabytes shared with the whole phone, and on the P30 it rolls in about
// four minutes. The service ran for FOUR DAYS AND FIVE HOURS and decoded 765
// utterances; thirty-six of them survived anywhere, and only because a laptop
// happened to be tailing the log for part of it.
//
// THAT COST A DECISION, WHICH IS THE POINT. "Hey Circle" measures as 4,5 times
// more likely to wake than the two shipped phrases, on the owner's own voice.
// Against that, BuiltInWakePhrases objects - in a comment written long before
// any of this - that "hey" and "circle" are both everyday words, so the phrase
// would wake up mid-conversation. Both halves are needed to choose, and the
// second half needs a corpus of what this room actually says.
//
// Run against the thirty-six that survived, every candidate scored ZERO false
// wakes - and the corpus contains the word "circle" exactly zero times, so the
// four zeros mean the corpus could not tell them apart, not that the phrases
// are safe. A measurement with no discriminating power reads exactly like a
// pass, which is the most expensive kind of wrong.
//
// So the phone keeps its own, where it cannot roll away: a day of listening
// becomes a few hundred lines, and any candidate phrase can then be scored
// against the room it has to live in BEFORE it ships.
//
// BOUNDED AND VISIBLE. It is capped at a few thousand lines and written to the
// external files directory, so the owner can read it, copy it off and delete
// it without adb - and it is a transcript of their home, so that matters more
// than the convenience of hiding it somewhere tidier.
//
// NEVER THROWS. A disk that filled or a directory that vanished must not stop
// a phone listening; this is diagnostics, and diagnostics that can break the
// product are worse than none.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace CircleAI.Voice;

/// <summary>Keeps what the wake loop decoded, so phrases can be scored on it.</summary>
public sealed class UtteranceLog
{
    /// <summary>The file, beside the model bundle in the app's own folder.</summary>
    public const string FileName = "heard.txt";

    /// <summary>How many lines are kept before the oldest are dropped.</summary>
    /// <remarks>
    /// A FEW DAYS OF A LIVED-IN ROOM. The P30 decoded 765 utterances in four
    /// days, so this holds roughly a fortnight - long enough to score a
    /// candidate phrase on, short enough that the file stays a few hundred
    /// kilobytes and the owner can read it in one sitting.
    /// </remarks>
    public const int MostLines = 4000;

    /// <summary>How many lines may be appended before the file is trimmed.</summary>
    /// <remarks>
    /// Trimming rewrites the file, so it is done in batches rather than on
    /// every utterance - at one utterance every few minutes, a hundred is
    /// hours apart.
    /// </remarks>
    private const int TrimEvery = 100;

    private readonly string? _path;
    private int _sinceTrim;

    /// <summary>Where the file is, or null when it could not be opened.</summary>
    public string? Path => _path;

    /// <summary>Opens the log in a directory, or returns a log that does nothing.</summary>
    /// <remarks>
    /// OFF UNLESS SOMEBODY ASKS, AND ASKING IS DELIBERATE - the same rule, and the
    /// same marker file, as CaptureDump beside it.
    /// <para>
    /// This is a TRANSCRIPT OF A ROOM, and it was written on any phone that had the
    /// streaming recogniser on it, from the next start of listening, with nothing said
    /// anywhere and nothing asked. The product's own card promises that the listening
    /// is not kept; a file of what the room said, in the external files directory -
    /// which on Android 10 other apps can read - is the plainest possible breach of
    /// that promise, and it existed to settle a question about candidate wake phrases.
    /// A measurement tool is worth keeping and is worth being switched on for.
    /// </para>
    /// <para>
    /// The marker is CaptureDump.MarkerName because the two answer the same question
    /// and should never be half on: the audio and what it decoded to belong together,
    /// and one deliberate act is better than two switches that can disagree.
    /// </para>
    /// </remarks>
    public UtteranceLog(string? directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
            if (!File.Exists(System.IO.Path.Combine(directory, CaptureDump.MarkerName))) return;

            _path = System.IO.Path.Combine(directory, FileName);
        }
        catch
        {
            _path = null;
        }
    }

    /// <summary>Writes one decoded utterance with the time and its length.</summary>
    /// <remarks>
    /// THE TIME AND THE LENGTH, not just the words. A phrase that only ever
    /// appears inside an eight-second stretch is a television; one that arrives
    /// in a two-second burst at nine in the morning is a person. Scoring a
    /// candidate without that cannot tell the two apart.
    /// </remarks>
    public void Add(string? heard, TimeSpan length, DateTimeOffset when)
    {
        if (_path is null) return;
        if (string.IsNullOrWhiteSpace(heard)) return;

        try
        {
            var line = string.Format(CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss}\t{1:F1}\t{2}",
                when, length.TotalSeconds, heard.Trim().Replace('\t', ' '));

            File.AppendAllText(_path, line + Environment.NewLine);

            if (++_sinceTrim >= TrimEvery)
            {
                _sinceTrim = 0;
                Trim();
            }
        }
        catch
        {
            // Diagnostics never stop the phone listening.
        }
    }

    /// <summary>Drops the oldest lines once the file has grown past its cap.</summary>
    private void Trim()
    {
        try
        {
            if (_path is null || !File.Exists(_path)) return;

            var lines = File.ReadAllLines(_path);
            if (lines.Length <= MostLines) return;

            var keep = new List<string>(MostLines);
            for (var i = lines.Length - MostLines; i < lines.Length; i++) keep.Add(lines[i]);
            File.WriteAllLines(_path, keep);
        }
        catch
        {
            // Same rule: a trim that fails leaves a larger file, not a deaf phone.
        }
    }
}
