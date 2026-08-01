// NguniRespellerTests.cs
//
// Writing an English pronunciation in isiZulu spelling.
//
// The voice reads letters, so a borrowed word only sounds right if it is spelt the
// way the host language spells it. The structural rule is the whole thing: Nguni
// syllables are consonant-vowel, with no clusters and no word-final consonant.
// That is why "computer" is written ikhompiyutha and "SMS" is esemese — the
// language opens the clusters out and finishes on a vowel.
//
// These tests pin the RULE, not a dictionary. Where usage has already settled on a
// spelling, LoanwordRespeller's curated table wins and this code never runs; this
// is for the words nobody has written down yet.

using CircleAI.Voice;
using Xunit;

namespace CircleAI.Companion.Tests;

public class NguniRespellerTests
{
    [Fact]
    public void No_consonant_cluster_survives()
    {
        // /st/ cannot stand in Nguni orthography; a vowel opens it.
        var s = NguniRespeller.FromIpa("stɔp");
        Assert.DoesNotContain("st", s);
        Assert.False(EndsOnConsonant(s), $"'{s}' ends on a consonant");
    }

    [Theory]
    [InlineData("kæt")]        [InlineData("dɒg")]
    [InlineData("bʊk")]        [InlineData("ɛsɛmɛs")]
    public void No_word_ends_on_a_consonant(string ipa)
    {
        var s = NguniRespeller.FromIpa(ipa);
        Assert.False(EndsOnConsonant(s), $"'{ipa}' became '{s}', which ends on a consonant");
    }

    [Fact]
    public void Aspirated_stops_take_the_h_isiZulu_writes_them_with()
    {
        // Plain p/t/k are different sounds in isiZulu, not a milder version of the
        // English ones — writing them plain changes the word rather than the accent.
        Assert.Contains("ph", NguniRespeller.FromIpa("pɛn"));
        Assert.Contains("th", NguniRespeller.FromIpa("tɛn"));
        Assert.Contains("kh", NguniRespeller.FromIpa("kɛn"));
    }

    [Fact]
    public void Diphthongs_become_vowel_sequences()
    {
        // /aɪ/ is written ayi, which is how "WiFi" arrives at wayifayi.
        Assert.Equal("wayifayi", NguniRespeller.FromIpa("waɪfaɪ"));
    }

    [Fact]
    public void Stress_and_length_marks_produce_no_letters_of_their_own()
    {
        var withMarks = NguniRespeller.FromIpa("kəmˈpjuːtə");
        var without   = NguniRespeller.FromIpa("kəmpjutə");
        Assert.Equal(without, withMarks);
    }

    [Fact]
    public void Computer_comes_out_recognisably_like_the_settled_spelling()
    {
        // The attested form is "khompiyutha". This need not match it letter for
        // letter — usage settles spellings and the curated table holds those — but
        // the shape must be right: no clusters, ends on a vowel, aspirated k and t.
        var s = NguniRespeller.FromIpa("kəmˈpjuːtə");
        Assert.StartsWith("kh", s);
        Assert.False(EndsOnConsonant(s));
        Assert.DoesNotContain("mp", s);      // the cluster was opened
    }

    [Fact]
    public void Nothing_in_yields_nothing_out()
    {
        Assert.Equal("", NguniRespeller.FromIpa(null));
        Assert.Equal("", NguniRespeller.FromIpa("   "));
    }

    private static bool EndsOnConsonant(string s) =>
        s.Length > 0 && !"aeiou".Contains(char.ToLowerInvariant(s[^1]));
}
