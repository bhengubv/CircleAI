using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

/// <summary>
/// The Japanese prosody tokeniser, against labels Open JTalk actually produced.
/// </summary>
/// <remarks>
/// WRITTEN BECAUSE THIS SHIPPED UNTESTED. The tokeniser was validated only
/// through a Python mirror of the same algorithm — which proves the two agree,
/// not that either is right, and the C# had never executed against a known
/// vector at all. The model was trained on exactly this tokenisation, so a
/// disagreement is heard as a DIFFERENT SENTENCE rather than a worse one.
/// <para>
/// The labels below are real output from libopenjtalk_g2p on the P30, not
/// hand-written: a fabricated label can be made to satisfy whatever the code
/// currently does, which is the opposite of a test.
/// </para>
/// </remarks>
public class OpenJTalkProsodyTokeniserTests
{
    /// <summary>
    /// Full-context labels for これは, as emitted on-device. Trimmed to the
    /// fields the tokeniser reads (phoneme, /A:, /F:, !) — the rest is HTS
    /// context a VITS model never sees.
    /// </summary>
    private static string[] KoreWa() =>
    [
        "xx^xx-sil+k=o/A:xx+xx+xx/F:xx_xx/!0_xx",
        "xx^sil-k+o=r/A:-1+1+3/F:3_0/!0_xx",
        "sil^k-o+r=e/A:-1+1+3/F:3_0/!0_xx",
        "k^o-r+e=w/A:0+2+2/F:3_0/!0_xx",
        "o^r-e+w=a/A:0+2+2/F:3_0/!0_xx",
        "r^e-w+a=sil/A:1+3+1/F:3_0/!0_xx",
        "e^w-a+sil=xx/A:1+3+1/F:3_0/!0_xx",
        "w^a-sil+xx=xx/A:xx+xx+xx/F:xx_xx/!0_xx",
    ];

    [Fact]
    public void Encode_WrapsTheUtteranceInStartAndEndSymbols()
    {
        // '^' and '$' are not decoration: they are how the model was told where
        // an utterance begins and that it ended as a STATEMENT. A missing '$'
        // is a sentence with no final contour.
        var t = new OpenJTalkProsodyTokeniser();
        t.Encode(KoreWa());

        Assert.Equal("^", t.LastSymbols[0]);
        Assert.Equal("$", t.LastSymbols[^1]);
    }

    [Fact]
    public void Encode_ProducesThePhonemesOfKoreWa_AndNothingUnknown()
    {
        var t = new OpenJTalkProsodyTokeniser();
        t.Encode(KoreWa());

        // は as a topic particle is read 'wa', not 'ha' — the whole reason a
        // morphological analyser is here rather than a character table.
        Assert.Equal(["k", "o", "r", "e", "w", "a"],
            t.LastSymbols.Where(s => s is not ("^" or "$" or "#" or "[" or "]" or "_")).ToArray());

        Assert.Empty(t.LastUnknown);
    }

    [Fact]
    public void Encode_MapsEverySymbolToAKnownId()
    {
        // The device log line this mirrors is "18 tokens, 0 unknown". An unknown
        // symbol becomes <unk>, which the model still renders as SOMETHING, so
        // counting them is the only way to notice.
        var t = new OpenJTalkProsodyTokeniser();
        var ids = t.Encode(KoreWa());

        Assert.Equal(t.LastSymbols.Count, ids.Length);
        Assert.DoesNotContain(OpenJTalkProsodyTokeniser.UnkId, ids);
        foreach (var id in ids)
            Assert.NotEqual("<oob>", OpenJTalkProsodyTokeniser.SymbolFor(id));
    }

    [Fact]
    public void Encode_LowercasesDevoicedVowels()
    {
        // Open JTalk writes devoiced vowels as capitals (the U of -masu, -desu).
        // The vocabulary has only lowercase, so without the fold every
        // sentence-final polite form becomes <unk> — and Japanese politeness is
        // sentence-final.
        var t = new OpenJTalkProsodyTokeniser();
        t.Encode(
        [
            "xx^xx-sil+m=a/A:xx+xx+xx/F:xx_xx/!0_xx",
            "xx^sil-m+a=s/A:-1+1+2/F:2_0/!0_xx",
            "sil^m-a+s=U/A:-1+1+2/F:2_0/!0_xx",
            "m^a-s+U=sil/A:0+2+1/F:2_0/!0_xx",
            "a^s-U+sil=xx/A:0+2+1/F:2_0/!0_xx",
            "s^U-sil+xx=xx/A:xx+xx+xx/F:xx_xx/!0_xx",
        ]);

        Assert.Contains("u", t.LastSymbols);
        Assert.DoesNotContain("U", t.LastSymbols);
        Assert.Empty(t.LastUnknown);
    }

    [Fact]
    public void Encode_MarksAQuestionDifferentlyFromAStatement()
    {
        // The interrogative flag lives in the FINAL label's !-field, so a
        // question and a statement differ only there. Getting this wrong is a
        // rising contour on a statement, or a flat one on a question.
        var statement = new OpenJTalkProsodyTokeniser();
        statement.Encode(KoreWa());

        var asked = KoreWa();
        asked[^1] = "w^a-sil+xx=xx/A:xx+xx+xx/F:xx_xx/!1_xx";
        var question = new OpenJTalkProsodyTokeniser();
        question.Encode(asked);

        Assert.Equal("$", statement.LastSymbols[^1]);
        Assert.Equal("?", question.LastSymbols[^1]);
    }

    [Fact]
    public void Encode_TreatsPauAsAPauseAndDropsBoundarySilence()
    {
        // 'pau' is real phrasing and is kept; 'sil' is an utterance boundary and
        // is a label, not a sound. Passing sil through made the voice speak its
        // own padding.
        var t = new OpenJTalkProsodyTokeniser();
        t.Encode(
        [
            "xx^xx-sil+k=o/A:xx+xx+xx/F:xx_xx/!0_xx",
            "xx^sil-k+o=pau/A:-1+1+1/F:1_0/!0_xx",
            "sil^k-o+pau=a/A:-1+1+1/F:1_0/!0_xx",
            "k^o-pau+a=sil/A:xx+xx+xx/F:xx_xx/!0_xx",
            "o^pau-a+sil=xx/A:-1+1+1/F:1_0/!0_xx",
            "pau^a-sil+xx=xx/A:xx+xx+xx/F:xx_xx/!0_xx",
        ]);

        Assert.Contains("_", t.LastSymbols);
        Assert.DoesNotContain("sil", t.LastSymbols);
        Assert.DoesNotContain("pau", t.LastSymbols);
    }

    [Fact]
    public void Encode_ReturnsNothingForEmptyInput()
    {
        var t = new OpenJTalkProsodyTokeniser();
        Assert.Empty(t.Encode(""));
        Assert.Empty(t.LastUnknown);
    }

    [Fact]
    public void Vocabulary_KeepsBlankAndUnkAtTheirTrainedIds()
    {
        // Index IS the token id, fixed by the model's config.yaml. If this array
        // is ever sorted or de-duplicated, every id shifts and the voice speaks
        // gibberish with no error anywhere.
        Assert.Equal("<blank>", OpenJTalkProsodyTokeniser.SymbolFor(OpenJTalkProsodyTokeniser.BlankId));
        Assert.Equal("<unk>", OpenJTalkProsodyTokeniser.SymbolFor(OpenJTalkProsodyTokeniser.UnkId));
        Assert.Equal("a", OpenJTalkProsodyTokeniser.SymbolFor(2));
        Assert.Equal("<sos/eos>", OpenJTalkProsodyTokeniser.SymbolFor(46));
    }
}
