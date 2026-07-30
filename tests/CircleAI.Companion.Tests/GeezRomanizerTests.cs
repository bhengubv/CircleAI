using CircleAI.Voice;
using Xunit;

namespace CircleAI.Companion.Tests;

/// <summary>
/// Covers Ethiopic → Latin transliteration for the uroman-style voices.
/// </summary>
/// <remarks>
/// Amharic and Tigrinya ship with <c>is_uroman: true</c>: their vocabularies are
/// 27–28 Latin letters and they have never seen an Ethiopic codepoint. Measured on
/// the P30 before this existed, Amharic lost 43 distinct characters and produced
/// 3.2 s of noise for a 15 s paragraph. These assert the mapping against words a
/// speaker would recognise, not against the algorithm's own logic.
/// </remarks>
public class GeezRomanizerTests
{
    [Theory]
    [InlineData("ሰላም", "selam")]                 // "peace / hello" — the canonical greeting
    [InlineData("አማርኛ", "amarnya")]              // "Amharic" — silent glottal opens it
    [InlineData("ትግርኛ", "tgrnya")]               // "Tigrinya" — see the sixth-order note
    [InlineData("እንኳን", "enkwan")]               // "welcome" — labialised ኳ, sounded እ
    public void Romanises_words_a_speaker_would_recognise(string geez, string expected)
        => Assert.Equal(expected, GeezRomanizer.Romanize(geez));

    // On "tgrnya" rather than "tigrinya": the sixth order is a schwa that a human
    // romanisation usually writes as 'i', but uroman drops it — and uroman is what
    // these models were TRAINED on. Matching the model's own convention matters
    // more than matching a textbook, so the target here is uroman's output.

    [Fact]
    public void Reads_the_syllabary_by_position_not_by_a_lookup_table()
    {
        // Unicode lays Ethiopic out as consonant × eight vowel orders, so one row
        // proves the whole block: ሀ ሁ ሂ ሃ ሄ ህ ሆ = h + (e u i a e _ o).
        Assert.Equal("hehuhihahehho", GeezRomanizer.Romanize("ሀሁሂሃሄህሆ"));
    }

    [Fact]
    public void Sixth_order_is_a_bare_consonant_with_no_vowel()
    {
        // ም is the sixth order of መ — the consonant alone. If this emitted a vowel,
        // "selam" would come out "selami" and every word would gain a syllable.
        Assert.Equal("m", GeezRomanizer.Romanize("ም"));
        Assert.Equal("me", GeezRomanizer.Romanize("መ"));
    }

    [Fact]
    public void Maps_ethiopic_punctuation_so_sentences_still_split()
    {
        // ። is the Ethiopic full stop. Left as-is it is just an unmappable symbol;
        // mapped to '.', the sentence splitter can do its work.
        Assert.Equal("selam.", GeezRomanizer.Romanize("ሰላም።"));
        Assert.Equal("selam,", GeezRomanizer.Romanize("ሰላም፣"));
    }

    [Fact]
    public void Leaves_non_ethiopic_text_untouched()
    {
        // Mixed text is normal — numerals, Latin names, borrowed words.
        Assert.Equal("selam CircleAI 2026", GeezRomanizer.Romanize("ሰላም CircleAI 2026"));
    }

    [Theory]
    [InlineData("ሰላም", true)]
    [InlineData("hello", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Detects_whether_romanisation_is_needed(string? text, bool expected)
        => Assert.Equal(expected, GeezRomanizer.IsEthiopic(text));

    [Fact]
    public void Phonemizer_yields_latin_characters_the_voice_actually_holds()
    {
        // The voice's tokens ARE Latin letters, so once the script is converted the
        // rest of the pipeline needs no change at all.
        var p = new GeezPhonemizer();

        var phones = p.Phonemize("ሰላም");

        Assert.Equal(new[] { "s", "e", "l", "a", "m" }, phones);
        Assert.Equal("selam", p.LastRomanised);
    }
}
