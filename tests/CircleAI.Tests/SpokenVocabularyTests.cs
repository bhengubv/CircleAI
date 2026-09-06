// SpokenVocabularyTests.cs
//
// The words a small model gets wrong are names and money, not grammar.
//
// Measured on a P30 on 2026-09-07: a four-turn meeting played through a laptop
// speaker into the phone came back with seventy-five of seventy-eight words
// exact. Whole sentences of ordinary English, perfect. The three misses were
// "Thandi" as "Tandy", "Sipho" as "Saifo", and "rand" as "rent" - two names and
// the currency of the country this app is built for.
//
// Priming the decoder with those words fixed all three on the same recording
// with nothing else changed. These tests hold the shape of that table, because
// a prompt is a BIAS and the ways it can go wrong are all about scope.

using System.Linq;
using CircleAI.Samples.It;
using Xunit;

namespace CircleAI.Tests;

public class SpokenVocabularyTests
{
    [Fact]
    public void English_primes_the_words_that_were_actually_wrong()
    {
        // NOT A GLOSSARY SOMEBODY IMAGINED. Each of these is a word the phone
        // got wrong on a real recording, and the reason the entry exists.
        var primed = SpokenVocabulary.For("en");

        Assert.NotNull(primed);
        Assert.Contains("rand", primed!, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Thandi", primed);
        Assert.Contains("Sipho", primed);
    }

    [Fact]
    public void A_regional_tag_finds_its_language()
    {
        // "en-ZA" is the likeliest tag on the handsets this is for, and "en-GB"
        // is a reasonable thing to have stored. Both are English.
        Assert.Equal(SpokenVocabulary.For("en"), SpokenVocabulary.For("en-ZA"));
        Assert.Equal(SpokenVocabulary.For("en"), SpokenVocabulary.For("en_GB"));
    }

    [Fact]
    public void A_language_nobody_measured_is_primed_with_nothing()
    {
        // THE RULE THAT KEEPS THIS HONEST. Shipping a South African word list to
        // a phone in Osaka is the same mistake the download plan made when it
        // fetched eleven South African languages there - and a prompt is worse
        // than a wasted download, because whisper will occasionally emit a primed
        // word that was never said. No measurement, no prompt.
        Assert.Null(SpokenVocabulary.For("ja"));
        Assert.Null(SpokenVocabulary.For("zu"));
        Assert.Null(SpokenVocabulary.For("fr"));
    }

    [Fact]
    public void Nothing_asked_for_is_nothing_primed()
    {
        Assert.Null(SpokenVocabulary.For(null));
        Assert.Null(SpokenVocabulary.For(""));
        Assert.Null(SpokenVocabulary.For("   "));
    }

    [Fact]
    public void Every_entry_reads_as_a_sentence_rather_than_a_word_list()
    {
        // WHISPER'S PROMPT IS TEXT, NOT A DICTIONARY. It primes on how words sit
        // together, so "Amounts are in rand" pulls harder than "rand" - and a
        // bare comma-separated list primes the model to produce bare
        // comma-separated lists, which is a real failure mode and an ugly one.
        foreach (var (tag, primed) in SpokenVocabulary.Phrases)
        {
            Assert.True(primed.Contains(' '), $"'{tag}' is not written as prose");
            Assert.True(primed.TrimEnd().EndsWith('.'), $"'{tag}' does not end a sentence");
            Assert.True(primed.Split(' ').Length >= 8, $"'{tag}' is too short to prime on");
        }
    }

    [Fact]
    public void The_prompt_stays_short_enough_to_be_worth_it()
    {
        // Whisper's prompt shares the decoder's token budget with the audio, so a
        // long one costs context that the words being transcribed need. A few
        // sentences is the useful range; a page is a way of making transcription
        // worse while believing it is being improved.
        foreach (var (tag, primed) in SpokenVocabulary.Phrases)
            Assert.True(primed.Length <= 500,
                $"'{tag}' is {primed.Length} characters — long enough to crowd out the audio");
    }
}
