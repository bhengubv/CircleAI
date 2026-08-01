// LanguageSpanSplitterTests.cs
//
// Mixed-language text is the normal case in South Africa, not an edge case:
// "Igama lami ngu-CircleAI" is isiZulu carrying an English name. Read wholly in
// isiZulu the name comes out mangled, and the listener hears the machine fail at
// a word they know.
//
// The rule these tests pin is as much about restraint as detection. Flagging an
// ordinary isiZulu word as English would mispronounce someone's own language to
// "fix" a foreign word — worse than leaving the foreign word alone.

using CircleAI.Voice;
using Xunit;

namespace CircleAI.Companion.Tests;

public class LanguageSpanSplitterTests
{
    [Fact]
    public void Plain_isiZulu_is_one_span_so_the_normal_path_is_untouched()
    {
        var spans = LanguageSpanSplitter.Split("Sawubona mhlaba. Unjani namuhla?");
        Assert.Single(spans);
        Assert.False(spans[0].IsForeign);
    }

    [Fact]
    public void An_English_name_inside_isiZulu_is_split_out()
    {
        var spans = LanguageSpanSplitter.Split("Igama lami ngu-CircleAI.");

        Assert.Equal(2, spans.Count);
        Assert.False(spans[0].IsForeign);
        Assert.True(spans[1].IsForeign);
        Assert.Contains("CircleAI", spans[1].Text);

        // The isiZulu prefix "ngu-" stays with the isiZulu run: it is a Zulu
        // morpheme glued to a foreign noun, and reading it in English would be the
        // same error in the opposite direction.
        Assert.Contains("ngu-", spans[0].Text);
    }

    [Fact]
    public void Nothing_is_dropped_no_matter_where_it_splits()
    {
        const string text = "Ngiyakwazi ukusebenzisa i-WhatsApp ne-GPS namuhla.";
        var spans = LanguageSpanSplitter.Split(text);

        var rebuilt = string.Concat(spans.Select(s => s.Text));
        Assert.Equal(text.Replace(" ", ""), rebuilt.Replace(" ", ""));
    }

    [Theory]
    [InlineData("CircleAI")]   [InlineData("WhatsApp")]  [InlineData("YouTube")]
    [InlineData("GPS")]        [InlineData("SMS")]       [InlineData("ATM")]
    public void Brand_names_and_acronyms_are_recognised_as_foreign(string word)
        => Assert.True(LanguageSpanSplitter.IsForeignWord(word));

    [Theory]
    [InlineData("Sawubona")]      // sentence-initial capital, ordinary isiZulu
    [InlineData("mhlaba")]
    [InlineData("Ngiyathemba")]
    [InlineData("ukuxhumana")]
    [InlineData("Xitsonga")]      // a language name, natively capitalised
    [InlineData("i")]             // a one-letter isiZulu prefix
    [InlineData("ne")]
    public void Ordinary_words_are_never_mistaken_for_foreign(string word)
        => Assert.False(LanguageSpanSplitter.IsForeignWord(word));

    [Fact]
    public void A_capital_only_counts_when_it_is_INSIDE_a_word()
    {
        // Every African language here capitalises the start of a sentence, so a
        // leading capital carries no information about language at all.
        Assert.False(LanguageSpanSplitter.IsForeignWord("Konke"));
        Assert.True(LanguageSpanSplitter.IsForeignWord("KoNke"));
    }

    [Fact]
    public void Long_all_caps_is_left_alone_because_it_is_probably_shouting()
    {
        // "SAWUBONA" is isiZulu in capitals, not an acronym.
        Assert.False(LanguageSpanSplitter.IsForeignWord("SAWUBONA"));
    }

    [Theory]
    [InlineData("CircleAI",   "Circle A.I.")]
    [InlineData("YouTube",    "You Tube")]
    [InlineData("WhatsApp",   "Whats App")]
    [InlineData("OpenAPIKey", "Open A.P.I. Key")]
    public void Compound_names_are_split_into_words_the_voice_can_say(string written, string spoken)
    {
        // Switching "CircleAI" to English was necessary and not sufficient: as one
        // token a synthesiser has no idea where the words are and mumbles it. The
        // written form is untouched — only what reaches the voice changes.
        Assert.Equal(spoken, LanguageSpanSplitter.ToSpokenForm(written));
    }

    [Theory]
    [InlineData("GPS", "G.P.S.")]
    [InlineData("SMS", "S.M.S.")]
    [InlineData("ATM", "A.T.M.")]
    public void Acronyms_are_punctuated_so_they_are_read_as_letters(string written, string spoken)
    {
        // "AI" as a bare token is read as a word — "ay". Dotted, it is read as the
        // letters, which is what it is. The stops are for the voice, not the eye.
        Assert.Equal(spoken, LanguageSpanSplitter.ToSpokenForm(written));
    }

    [Theory]
    [InlineData("Sawubona")]   [InlineData("mhlaba")]  [InlineData("")]
    [InlineData("Ngiyathemba")]
    public void Ordinary_words_are_left_exactly_as_they_are(string word)
    {
        // Splitting or dotting an isiZulu word would invent boundaries that are not
        // there. A single leading capital is an ordinary sentence opening.
        Assert.Equal(word, LanguageSpanSplitter.ToSpokenForm(word));
    }

    [Fact]
    public void Empty_input_yields_nothing_rather_than_a_blank_utterance()
    {
        Assert.Empty(LanguageSpanSplitter.Split(""));
        Assert.Empty(LanguageSpanSplitter.Split("   "));
        Assert.Empty(LanguageSpanSplitter.Split(null));
    }
}
