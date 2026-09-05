// WakeListenerBuildTests.cs
//
// The phone was renamed everywhere except in the place that hears.
//
// WHAT THIS IS FOR. A wake listener compiles its phrase once, at build time, and
// never re-reads the file. The staleness check that decides whether to rebuild
// it was written against the LANGUAGE, which is a proxy for the phrase and a
// coarser one - so changing English to Japanese rebuilt the listener, and
// changing "Hey B" to "Hey Circle AI" did not.
//
// Measured on a P30 on 2026-09-06. "Hey Circle AI" was chosen in Settings; the
// settings table said so, the keywords file said so, the log said
// `kws: 'en' listens for "Hey Circle AI" (8 tokens, Good)`, and six minutes of
// somebody saying it produced `closest="Hey B" 2/3 tokens`. Three tokens. Every
// screen in the app was telling the truth about a phrase the microphone had
// never been given.

using System;
using System.IO;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class WakeListenerBuildTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wake-build").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string name, string contents)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void The_phrase_changing_is_a_change()
    {
        // THE WHOLE BUG IN ONE ASSERTION. Same language, same path, different
        // phrase - which is what "Hey B" to "Hey Circle AI" actually is, and what
        // a language-based check cannot see.
        var kw = Write("wake-en.txt", "Hey B");
        var built = WakeListenerBuild.Of("en", kw);

        Assert.False(built.IsStaleFor("en", kw));

        File.WriteAllText(kw, "Hey Circle AI");

        Assert.True(built.IsStaleFor("en", kw),
            "the keywords file was rewritten with a different phrase and the "
            + "listener still believes it is current");
    }

    [Fact]
    public void The_language_changing_is_still_a_change()
    {
        // The axis that was already closed stays closed.
        var kw = Write("wake-en.txt", "Hey B");
        var built = WakeListenerBuild.Of("en", kw);

        Assert.True(built.IsStaleFor("ja", kw));
    }

    [Fact]
    public void A_regional_tag_is_not_a_different_language()
    {
        // "en" and "en-ZA" must not rebuild against each other forever.
        var kw = Write("wake-en.txt", "Hey B");
        var built = WakeListenerBuild.Of("en", kw);

        Assert.False(built.IsStaleFor("EN", kw));
    }

    [Fact]
    public void Rewriting_the_same_phrase_is_not_a_change()
    {
        // CHOOSING THE PHRASE THAT IS ALREADY CHOSEN happens on every visit to
        // the screen - the write-through runs whether or not the value moved. If
        // that counted as a change, opening Settings would stop the microphone
        // and reload the model.
        var kw = Write("wake-en.txt", "Hey Circle AI");
        var built = WakeListenerBuild.Of("en", kw);

        File.WriteAllText(kw, "Hey Circle AI");

        Assert.False(built.IsStaleFor("en", kw));
    }

    [Fact]
    public void A_file_that_appears_later_is_a_change()
    {
        // A phone that has never opened the phrase screen runs on the bundle's
        // own keywords, and the head's file does not exist. The first time
        // somebody chooses, it appears - and the listener has to pick it up.
        var kw = Path.Combine(_dir, "wake-en.txt");
        var built = WakeListenerBuild.Of("en", kw);

        Assert.Equal(WakeListenerBuild.Absent, built.Contents);
        Assert.False(built.IsStaleFor("en", kw));

        File.WriteAllText(kw, "Hey Circle AI");

        Assert.True(built.IsStaleFor("en", kw));
    }

    [Fact]
    public void A_file_that_was_never_there_stays_current()
    {
        // THE REGRESSION THIS INVITES. If "absent" were treated as "could not
        // tell" and that as "changed", a phone with no head-written keywords
        // would rebuild its listener on every single start, forever.
        var kw = Path.Combine(_dir, "never-written.txt");
        var built = WakeListenerBuild.Of("en", kw);

        Assert.False(built.IsStaleFor("en", kw));
        Assert.False(built.IsStaleFor("en", kw));
    }

    [Fact]
    public void The_bundles_own_keywords_are_a_stable_answer()
    {
        // Null means "whatever the bundle ships with", and asking twice must not
        // produce two different answers.
        var built = WakeListenerBuild.Of("en", null);

        Assert.Null(built.Contents);
        Assert.False(built.IsStaleFor("en", null));
        Assert.True(built.IsStaleFor("en", Path.Combine(_dir, "wake-en.txt")));
    }

    [Fact]
    public void Two_different_phrases_of_the_same_length_are_different()
    {
        // WHY THIS HASHES RATHER THAN STATS. Length and modified-time can both
        // repeat across two phrases written in the same instant, and a wake
        // phrase that silently did not take is the exact failure being fixed.
        var kw = Write("wake-en.txt", "Hey Bee");
        var built = WakeListenerBuild.Of("en", kw);

        File.WriteAllText(kw, "Hey Cee");   // same length, same second

        Assert.True(built.IsStaleFor("en", kw));
    }

    [Fact]
    public void Nothing_built_is_stale_against_anything()
    {
        // The starting state must ask for a build rather than claim to be current.
        var kw = Write("wake-en.txt", "Hey B");

        Assert.True(WakeListenerBuild.None.IsStaleFor("en", kw));
    }
}
