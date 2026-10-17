// TtsEnginesTests.cs
//
// "TRANSLATE AND TRANSCRIBE ARE REAL-TIME BUT VOICE LAGS SO BAD."
//
// The owner said that, and then guessed the cause himself: "something is not
// cached". He was right. From the P30's own log, 2026-10-11/12 - nine of twelve
// syntheses in one session reported "(INCLUDING model open)":
//
//      3 chars ->  487 ms audio in  6672 ms (INCLUDING model open)
//     21 chars -> 1393 ms audio in  2911 ms                  <- session already open
//     29 chars -> 1695 ms audio in 10706 ms (INCLUDING model open)
//     39 chars -> 1950 ms audio in 12182 ms
//
// Three characters cost as much as thirty-nine, which is a fixed cost and not
// synthesis. The speaking path built a new OnnxTtsEngine for every sentence,
// so each reply paid three to nine seconds to open a model it had just closed.
// Translate and Transcribe hold their model; voice did not.
//
// These assert the caching rule without loading a voice - the engine opens no
// file until it synthesises, which is what makes that possible.

using System;
using System.IO;
using System.Linq;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class TtsEnginesTests : IDisposable
{
    // The cache is static, so each test starts from a known state and leaves one.
    public TtsEnginesTests() => TtsEngines.CloseAll();
    public void Dispose() => TtsEngines.CloseAll();

    private const string Voice = "/models/en_US-lessac-medium.onnx";
    private const string Other = "/models/vits-11ZA.onnx";

    [Fact]
    public void The_same_voice_twice_is_the_same_engine()
    {
        // THE WHOLE POINT. Two sentences in one reply must not pay for two
        // model loads - that is the three to nine seconds measured above.
        var a = TtsEngines.For(Voice, null);
        var b = TtsEngines.For(Voice, null);

        Assert.Same(a, b);
        Assert.Equal(1, TtsEngines.Count);
    }

    [Fact]
    public void A_different_voice_is_a_different_engine()
    {
        var a = TtsEngines.For(Voice, null);
        var b = TtsEngines.For(Other, null);

        Assert.NotSame(a, b);
        Assert.Equal(2, TtsEngines.Count);
    }

    [Fact]
    public void A_different_speaker_is_a_different_engine()
    {
        // Caching on the model path alone would hand back an engine set to the
        // wrong speaker, and the wrong voice is worse than a slow one.
        var a = TtsEngines.For(Voice, null, speakerId: 0);
        var b = TtsEngines.For(Voice, null, speakerId: 128);

        Assert.NotSame(a, b);
        Assert.Equal(128, b.SpeakerId);
        Assert.Equal(0, a.SpeakerId);
    }

    [Fact]
    public void A_different_language_is_a_different_engine()
    {
        // "English words spoken with Zulu phonetics" is a failure this codebase
        // already records. A cache must not be able to cause it.
        var a = TtsEngines.For(Voice, null, languageId: 0);
        var b = TtsEngines.For(Voice, null, languageId: 7);

        Assert.NotSame(a, b);
        Assert.Equal(7, b.LanguageId);
    }

    [Fact]
    public void It_does_not_hold_more_voices_than_it_said_it_would()
    {
        // A cache that grows without limit trades the problem it solves for a
        // worse one on a 3,87 GB phone.
        for (var i = 0; i < TtsEngines.MostOpen + 3; i++)
            TtsEngines.For($"/models/voice-{i}.onnx", null);

        Assert.True(TtsEngines.Count <= TtsEngines.MostOpen,
            $"holding {TtsEngines.Count} voices against a stated limit of {TtsEngines.MostOpen}");
    }

    [Fact]
    public void Closing_everything_gives_the_memory_back()
    {
        // FOR MEMORY PRESSURE, WHICH THIS PHONE HAS CONSTANTLY - swap sits at
        // 95-100%. A process told to give memory back can, at the cost of one
        // reload, which is the right trade against being killed.
        TtsEngines.For(Voice, null);
        TtsEngines.For(Other, null);
        Assert.Equal(2, TtsEngines.Count);

        TtsEngines.CloseAll();
        Assert.Equal(0, TtsEngines.Count);

        // And it comes back afterwards rather than staying broken.
        Assert.NotNull(TtsEngines.For(Voice, null));
        Assert.Equal(1, TtsEngines.Count);
    }

    [Fact]
    public void THE_CACHE_IS_NOT_UNDONE_BY_WHOEVER_BORROWS_A_VOICE()
    {
        // THE DEFECT THAT MADE THIS CLASS A NO-OP FOR A WEEK, MEASURED ON THE P30.
        //
        // An English answer on 2026-10-16 still read "39 chars -> 2066 ms audio in
        // 4826 ms (INCLUDING model open)" - an hour after the service had warmed a
        // voice, and after this cache was believed to have ended the reloads.
        //
        // Two things in CircleAISpeaker undid it. The English and lexicon voices
        // were still built with `new OnnxTtsEngine`, so the cache never saw them.
        // And its Dispose disposed the engines it held - which, once they came
        // from here, closed the session of an engine this cache would hand to the
        // very next caller. CircleAITtsProbe makes and disposes a speaker per
        // render, so every turn closed the voice and the next reopened it.
        //
        // It never crashed, which is how it hid: a disposed engine's session is
        // simply rebuilt on the next sentence. Read from source because the
        // speaker cannot be built here without a downloaded voice.
        var repo = AppContext.BaseDirectory;
        while (repo is not null && !File.Exists(Path.Combine(repo, "CircleAI.sln")))
            repo = Path.GetDirectoryName(repo);
        Assert.NotNull(repo);

        var speaker = File.ReadAllText(Path.Combine(
            repo!, "src", "CircleAI", "Assistant.Runtime", "Voice", "CircleAISpeaker.cs"));

        // Comments quote the old defect; strip them so they cannot satisfy or
        // fail the check on their own.
        var code = string.Join('\n', speaker.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.False(code.Contains("new OnnxTtsEngine", StringComparison.Ordinal),
            "every voice the speaker holds must come from TtsEngines, or the cache never sees it");

        var start = code.IndexOf("public void Dispose()", StringComparison.Ordinal);
        Assert.True(start > 0, "the speaker's Dispose has moved - this guard is pointing at nothing");
        var body = code.Substring(start, Math.Min(200, code.Length - start));

        Assert.False(body.Contains("_sa.Dispose", StringComparison.Ordinal),
            "the speaker must not close a voice the cache still owns");
        Assert.False(body.Contains("_en?.Dispose", StringComparison.Ordinal) || body.Contains("_en.Dispose", StringComparison.Ordinal),
            "the speaker must not close a voice the cache still owns");
    }

    [Fact]
    public void A_voice_with_no_path_is_refused_rather_than_cached()
    {
        Assert.Throws<ArgumentException>(() => TtsEngines.For("", null));
        Assert.Throws<ArgumentException>(() => TtsEngines.For("   ", null));
        Assert.Throws<ArgumentNullException>(() => TtsEngines.For(null!, null));
    }
}
