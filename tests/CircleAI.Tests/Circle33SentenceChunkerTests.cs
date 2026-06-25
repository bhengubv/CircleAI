// Circle33SentenceChunkerTests.cs
//
// (3.3.0) Tests for sentence chunker.

using System.Collections.Generic;
using System.Linq;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33SentenceChunkerTests
{
    [Fact]
    public void PushToken_PunctuationCompletesSentence()
    {
        var c = new SentenceChunker();
        var emitted = new List<string>();
        foreach (var s in c.PushToken("Hello there. ")) emitted.Add(s);
        Assert.Single(emitted);
        Assert.Equal("Hello there.", emitted[0]);
    }

    [Fact]
    public void PushToken_NoPunctuationBuffers()
    {
        var c = new SentenceChunker();
        var emitted = new List<string>();
        foreach (var s in c.PushToken("Hello there"))
            emitted.Add(s);
        Assert.Empty(emitted);
    }

    [Fact]
    public void PushToken_MultipleSentences_StreamsAllComplete()
    {
        var c = new SentenceChunker();
        var emitted = new List<string>();
        foreach (var s in c.PushToken("This is one. This is two. This is three."))
            emitted.Add(s);
        Assert.Equal(3, emitted.Count);
    }

    [Fact]
    public void PushToken_SplitAcrossTokens_AssemblesCorrectly()
    {
        var c = new SentenceChunker();
        var all = new List<string>();
        foreach (var s in c.PushToken("Hello"))    all.Add(s);
        foreach (var s in c.PushToken(" world"))   all.Add(s);
        foreach (var s in c.PushToken(". Bye now.")) all.Add(s);
        Assert.Equal(2, all.Count);
        Assert.Equal("Hello world.", all[0]);
        Assert.Equal("Bye now.",     all[1]);
    }

    [Fact]
    public void PushToken_ShortSentence_BufferedWithNext()
    {
        var c = new SentenceChunker(minSentenceLength: 10);
        var emitted = new List<string>();
        foreach (var s in c.PushToken("1. Long sentence to flush.")) emitted.Add(s);
        // "1." is too short on its own → joined with the next sentence.
        Assert.Single(emitted);
        Assert.Contains("Long sentence", emitted[0]);
    }

    [Fact]
    public void Flush_ReturnsRemainingBuffer()
    {
        var c = new SentenceChunker();
        foreach (var _ in c.PushToken("Incomplete sentence without punctuation")) { }
        var leftover = c.Flush();
        Assert.Equal("Incomplete sentence without punctuation", leftover);
    }

    [Fact]
    public void PushToken_QuestionMark_CompletesSentence()
    {
        var c = new SentenceChunker();
        var emitted = c.PushToken("How are you? Bye.").ToList();
        Assert.Equal(2, emitted.Count);
        Assert.Equal("How are you?", emitted[0]);
    }

    [Fact]
    public void PushToken_ExclamationMark_CompletesSentence()
    {
        var c = new SentenceChunker();
        var emitted = c.PushToken("Hello there! Yes I do!").ToList();
        Assert.Equal(2, emitted.Count);
    }

    [Fact]
    public void PushToken_EmptyToken_EmitsNothing()
    {
        var c = new SentenceChunker();
        Assert.Empty(c.PushToken(""));
        Assert.Empty(c.PushToken(null!));
    }
}
