// WakePhraseTests.cs
//
// The phrase book is where a session's worth of measurements turns into advice
// somebody gets while they are typing, instead of a disappointment they meet
// weeks later. These tests pin that advice to the numbers it came from.

using System;
using System.IO;
using System.Linq;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public class WakePhraseTests
{
    /// <summary>A tiny hand-built vocabulary — no model file, no fixture to fetch.</summary>
    /// <remarks>
    /// The real bpe.model is verified separately against Google's sentencepiece.
    /// What is under test HERE is the judgement, so the tokeniser is stubbed to a
    /// handful of pieces and the tests stay fast and offline.
    /// </remarks>
    private static SentencePieceTokenizer Vocab()
    {
        // Minimal sentencepiece protobuf: repeated piece { string=1, float=2, int=3 }
        var ms = new MemoryStream();
        void Piece(string text, float score, int kind)
        {
            var body = new MemoryStream();
            var utf8 = System.Text.Encoding.UTF8.GetBytes(text);
            body.WriteByte(0x0A); WriteVarint(body, (ulong)utf8.Length); body.Write(utf8);
            body.WriteByte(0x15); body.Write(BitConverter.GetBytes(score));
            body.WriteByte(0x18); WriteVarint(body, (ulong)kind);
            var bytes = body.ToArray();
            ms.WriteByte(0x0A); WriteVarint(ms, (ulong)bytes.Length); ms.Write(bytes);
        }
        static void WriteVarint(Stream s, ulong v)
        {
            while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
            s.WriteByte((byte)v);
        }

        Piece("<blk>", 0, 3);
        Piece("<unk>", 0, 2);
        foreach (var (p, sc) in new (string, float)[]
                 {
                     ("▁C", -3f), ("IR", -3f), ("C", -4f), ("LE", -3f),
                     ("▁HE", -3f), ("Y", -4f), ("▁B", -4f), ("▁BE", -3f), ("E", -4f),
                     ("▁LI", -3f), ("S", -4f), ("T", -4f), ("EN", -3f),
                     ("▁ZE", -3f), ("BRA", -3f), ("▁ST", -3f), ("RI", -3f), ("PE", -3f),
                 })
            Piece(p, sc, 1);

        return new SentencePieceTokenizer(ms.ToArray());
    }

    [Fact]
    public void AShortPhraseIsAllowedButWarnedAbout()
    {
        // "Hey B" is three tokens and was heard 1 time in 10 through a room, while
        // four-token "Circle" — the same two syllables — was heard 12 of 12. The
        // book must not forbid it (it is the owner's phrase) and must not stay
        // quiet either.
        var book = new WakePhraseBook(Vocab());
        var p = book.Evaluate("hey b");

        Assert.Equal(3, p.Tokens.Count);
        Assert.Equal(WakePhraseVerdict.Caution, p.Verdict);
        Assert.Contains("across a room", p.Advice);
    }

    [Fact]
    public void AFourTokenPhraseOfEverydayWordsWarnsAboutSelfTriggering()
    {
        // "Circle" is four tokens and reliable, and it also fired on 21 of 30 clips
        // of ordinary speech — every one a sentence containing the word.
        var book = new WakePhraseBook(Vocab());
        var p = book.Evaluate("circle");

        Assert.Equal(4, p.Tokens.Count);
        Assert.Equal(WakePhraseVerdict.Caution, p.Verdict);
        Assert.Contains("talking to someone else", p.Advice);
    }

    [Fact]
    public void ADistinctiveLongEnoughPhraseIsSimplyGood()
    {
        var book = new WakePhraseBook(Vocab());
        var p = book.Evaluate("zebra stripe");

        Assert.Equal(WakePhraseVerdict.Good, p.Verdict);
        Assert.Equal(string.Empty, p.Advice);
    }

    [Fact]
    public void APhraseAnotherOneStartsWithIsRefused()
    {
        // Measured: across eighteen recordings of "Hey Circle AI", every single
        // detection reported "Hey Circle". Registering both is not a trade-off,
        // it is one phrase that silently never works.
        var book = new WakePhraseBook(Vocab());
        Assert.True(book.TryAdd("circle", out _));

        Assert.False(book.TryAdd("circle listen", out var second));
        Assert.Equal(WakePhraseVerdict.Unusable, second.Verdict);
        Assert.Contains("could never work", second.Advice);
    }

    [Fact]
    public void APhraseThatWouldBreakAnExistingOneIsAlsoRefused()
    {
        // The other direction: adding the SHORTER phrase second would silently
        // kill the longer one already in the book.
        var book = new WakePhraseBook(Vocab());
        Assert.True(book.TryAdd("circle listen", out _));

        Assert.False(book.TryAdd("circle", out var second));
        Assert.Contains("would stop working", second.Advice);
    }

    [Fact]
    public void SoundsTheListenerDoesNotKnowAreRefusedNotSilentlyMangled()
    {
        var book = new WakePhraseBook(Vocab());
        var p = book.Evaluate("xyzzy");

        Assert.Equal(WakePhraseVerdict.Unusable, p.Verdict);
        Assert.Contains("does not know", p.Advice);
    }

    [Fact]
    public void SavingAndLoadingKeepsThePhraseAndItsOverrides()
    {
        var book = new WakePhraseBook(Vocab());
        Assert.True(book.TryAdd("zebra stripe", out _, threshold: 0.42, boost: 1.5));

        var path = Path.Combine(Path.GetTempPath(), $"kw-{Guid.NewGuid():N}.txt");
        try
        {
            book.Save(path);
            var text = File.ReadAllText(path);
            // The label is underscored: the format splits on spaces, so a two-word
            // phrase written plainly comes back as only its first word.
            Assert.Contains("@zebra_stripe", text);
            Assert.Contains("#0.42", text);
            Assert.Contains(":1.5", text);

            var reloaded = WakePhraseBook.Load(path, Vocab());
            var p = Assert.Single(reloaded.Phrases);
            Assert.Equal("zebra stripe", p.Text);
            Assert.Equal(0.42, p.Threshold);
            Assert.Equal(1.5, p.Boost);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void AnUpperCaseVocabularyFoldsTheInput()
    {
        // This model was trained on upper-case transcripts, so "circle" typed by a
        // person has to become "CIRCLE" or every piece misses and the phrase turns
        // silently into unknowns. Detected from the vocabulary, not configured.
        var vocab = Vocab();
        Assert.True(vocab.VocabularyIsUpperCase);
        Assert.Equal(new[] { "▁C", "IR", "C", "LE" }, vocab.Encode("circle").ToArray());
    }
}
