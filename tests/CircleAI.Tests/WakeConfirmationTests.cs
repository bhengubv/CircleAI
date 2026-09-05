// WakeConfirmationTests.cs
//
// Stage two decides whether a spotted phrase was meant as a wake. Its failure
// modes are asymmetric and both are bad in different ways: too strict and the
// assistant is deaf, too loose and it interrupts. The first version written here
// was the deaf kind — it vetoed all six true positives along with all twelve
// false accepts — and it did so while looking entirely reasonable, which is why
// the first test below is the one that would have caught it.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public class WakeConfirmationTests
{
    private const int Rate = 16_000;

    /// <summary>Builds a window: leading quiet, then speech, at 16 kHz.</summary>
    private static float[] Audio(double quietMs, double speechMs, double trailingMs = 300)
    {
        var n = (int)((quietMs + speechMs + trailingMs) * 16);
        var a = new float[n];
        var from = (int)(quietMs * 16);
        var to = from + (int)(speechMs * 16);
        var rnd = new Random(7);                       // deterministic "speech"
        for (var i = from; i < Math.Min(to, n); i++)
            a[i] = (float)(0.35 * Math.Sin(i * 0.09) + 0.05 * (rnd.NextDouble() - 0.5));
        return a;
    }

    /// <summary>A candidate with an HONEST KeywordStart.</summary>
    /// <remarks>
    /// EVERY TEST HERE USED TO PASS 0, and that is why none of them could see the
    /// bug. KeywordStart says where the phrase begins inside the window; pinning
    /// it to the window's own edge makes "speech before the phrase" and "speech
    /// before the end of the phrase" the same measurement, so a confirmer
    /// anchored on the wrong one looked correct.
    /// <para>
    /// Defaults to 450 ms of phrase - roughly "Circle", which is what these
    /// fixtures were written around - so every existing test keeps its intent.
    /// </para>
    /// </remarks>
    private static WakeCandidate Candidate(
        float[] window, double keywordEndMs, string phrase = "Circle", double phraseMs = 450)
        => new(new KwsDetection(phrase, (int)(keywordEndMs / 40), 0.7, (int)(keywordEndMs / 40) - 4),
               window,
               (int)(Math.Max(0, keywordEndMs - phraseMs) * 16),
               (int)(keywordEndMs * 16));

    [Fact]
    public async Task AWakeSpokenOnItsOwnIsConfirmed()
    {
        // THE TEST THAT WOULD HAVE CAUGHT THE FIRST ATTEMPT. 200 ms of quiet, then
        // a 450 ms word — someone saying "Circle" and nothing else. If stage two
        // rejects this, the wake word does not work at all, and a version that
        // rejected every real wake still looked correct by every other measure.
        var a = Audio(quietMs: 200, speechMs: 450);
        var ok = await new UtteranceOnsetConfirmer()
            .ConfirmAsync(Candidate(a, keywordEndMs: 700));
        Assert.True(ok);
    }

    [Fact]
    public async Task ALongWakePhraseSpokenOnItsOwnIsConfirmed()
    {
        // THE BUG, AS A TEST. Same shape as the first case — quiet room, nothing
        // said before — but the phrase takes 1300 ms instead of 450, because it is
        // "Hey Circle AI" rather than "Hey B".
        //
        // Measured on a P30 on 2026-09-06: stage one matched all eight tokens at
        // p=0,566, nearly triple its gate, and stage two threw it away with
        // "had been speaking 1320 ms before the phrase ended (max 600)". Nobody
        // had been speaking. The 1320 ms was the phrase.
        //
        // A confirmer that vetoes a phrase for being long is a confirmer whose
        // budget has to be re-tuned every time somebody renames their assistant —
        // and the app now offers thirty-two languages' worth of wake phrases, most
        // of them greetings, all of them longer than "Hey B".
        var a = Audio(quietMs: 300, speechMs: 1300);
        var c = new UtteranceOnsetConfirmer();

        var ok = await c.ConfirmAsync(
            Candidate(a, keywordEndMs: 1600, phrase: "Hey Circle AI", phraseMs: 1300));

        Assert.True(ok, c.LastReason ?? "vetoed with no reason given");
    }

    [Fact]
    public async Task PhraseLengthDoesNotChangeTheVerdict()
    {
        // THE PROPERTY, NOT THE INSTANCE. The rule is about what came BEFORE the
        // phrase, so two phrases of very different lengths, each spoken into the
        // same quiet room with the same run-up, must be judged the same. This is
        // what stops the budget from silently becoming a phrase-length limit again.
        var c = new UtteranceOnsetConfirmer();

        foreach (var phraseMs in new[] { 300.0, 700.0, 1300.0, 2000.0 })
        {
            var a = Audio(quietMs: 300, speechMs: phraseMs);
            var ok = await c.ConfirmAsync(
                Candidate(a, keywordEndMs: 300 + phraseMs, phraseMs: phraseMs));

            Assert.True(ok, $"a {phraseMs} ms phrase on its own was vetoed: {c.LastReason}");
        }
    }

    [Fact]
    public async Task TheSameWordMidSentenceIsRejected()
    {
        // Two seconds of talking already under way when the word lands: "let us
        // circle back on that". The word is identical; the run-up is not.
        var a = Audio(quietMs: 100, speechMs: 2000);
        var c = new UtteranceOnsetConfirmer();
        var ok = await c.ConfirmAsync(Candidate(a, keywordEndMs: 2000));
        Assert.False(ok);
        Assert.Contains("speaking", c.LastReason);
    }

    [Fact]
    public async Task ShortGapsInsideAWordDoNotEndTheUtterance()
    {
        // "Cir-cle" has a stop in the middle. Without gap tolerance the second
        // syllable looks like a fresh utterance and everything passes, including
        // the mid-sentence case this exists to reject.
        var a = new float[(int)(2600 * 16)];
        var rnd = new Random(11);
        void Speak(double fromMs, double toMs)
        {
            for (var i = (int)(fromMs * 16); i < (int)(toMs * 16) && i < a.Length; i++)
                a[i] = (float)(0.35 * Math.Sin(i * 0.09) + 0.05 * (rnd.NextDouble() - 0.5));
        }
        Speak(100, 1200);
        Speak(1260, 2300);          // 60 ms stop — well inside the 150 ms tolerance

        var ok = await new UtteranceOnsetConfirmer().ConfirmAsync(Candidate(a, 2300));
        Assert.False(ok);           // still one long utterance, so still a rejection
    }

    [Fact]
    public async Task LoudAndQuietSpeechAreJudgedTheSame()
    {
        // The test is RELATIVE. A quiet talker four metres away and a loud one at
        // arm's length must get the same answer, or the wake word works only at
        // the distance it happened to be tuned at.
        var loud = Audio(200, 450);
        var quiet = Audio(200, 450);
        for (var i = 0; i < quiet.Length; i++) quiet[i] *= 0.02f;   // 34 dB down

        var c = new UtteranceOnsetConfirmer();
        Assert.True(await c.ConfirmAsync(Candidate(loud, 700)));
        Assert.True(await c.ConfirmAsync(Candidate(quiet, 700)));
    }

    [Fact]
    public async Task NotEnoughAudioToJudgeLetsTheWakeThrough()
    {
        // Right after start-up there is no history. Refusing on that basis would
        // make the FIRST wake fail — the one someone is most likely to be testing.
        var ok = await new UtteranceOnsetConfirmer()
            .ConfirmAsync(Candidate(new float[160], 10));
        Assert.True(ok);
    }

    [Fact]
    public async Task ATranscriptConfirmerAcceptsThePhraseAtTheStart()
    {
        var c = new TranscriptConfirmer(new FakeTranscriber("circle what is the weather today"));
        Assert.True(await c.ConfirmAsync(Candidate(Audio(200, 450), 700)));
    }

    [Fact]
    public async Task ATranscriptConfirmerRejectsThePhraseBuriedInASentence()
    {
        // The case the cheap confirmer cannot reach: "THE circle is round" begins
        // talking barely sooner than "Circle" does, so only the words separate them.
        var c = new TranscriptConfirmer(new FakeTranscriber("the circle is round and blue"));
        Assert.False(await c.ConfirmAsync(Candidate(Audio(200, 450), 700)));
        Assert.Contains("not how it starts", c.LastReason);
    }

    [Fact]
    public async Task ATranscriptConfirmerToleratesOneWordInFront()
    {
        // "Um, Circle" is unmistakably someone addressing the device, and "The
        // circle is round" is unmistakably not — yet both put exactly ONE word in
        // front of the keyword. A rule that counts words cannot tell them apart;
        // only knowing which word can.
        var filler = new TranscriptConfirmer(new FakeTranscriber("um circle please"));
        Assert.True(await filler.ConfirmAsync(Candidate(Audio(200, 450), 700)));

        var determiner = new TranscriptConfirmer(new FakeTranscriber("the circle is round"));
        Assert.False(await determiner.ConfirmAsync(Candidate(Audio(200, 450), 700)));
    }

    [Fact]
    public async Task ABrokenTranscriberFailsOpen()
    {
        // An assistant that goes deaf because its verifier fell over is worse than
        // one that occasionally wakes when it should not.
        var c = new TranscriptConfirmer(new ThrowingTranscriber());
        Assert.True(await c.ConfirmAsync(Candidate(Audio(200, 450), 700)));
        Assert.Contains("unavailable", c.LastReason);
    }

    private sealed class FakeTranscriber : IVoiceTranscriber
    {
        private readonly string _text;
        public FakeTranscriber(string text) => _text = text;
        public Task<TranscriptionResult> TranscribeAsync(
            ReadOnlyMemory<byte> pcm, System.Threading.CancellationToken ct = default,
            string? language = null) =>
            Task.FromResult(new TranscriptionResult(_text, 0.9f, "en"));
        public IAsyncEnumerable<PartialTranscription> StreamTranscribeAsync(
            IAsyncEnumerable<ReadOnlyMemory<byte>> chunks, System.Threading.CancellationToken ct = default) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingTranscriber : IVoiceTranscriber
    {
        public Task<TranscriptionResult> TranscribeAsync(
            ReadOnlyMemory<byte> pcm, System.Threading.CancellationToken ct = default,
            string? language = null) =>
            throw new InvalidOperationException("model not loaded");
        public IAsyncEnumerable<PartialTranscription> StreamTranscribeAsync(
            IAsyncEnumerable<ReadOnlyMemory<byte>> chunks, System.Threading.CancellationToken ct = default) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
