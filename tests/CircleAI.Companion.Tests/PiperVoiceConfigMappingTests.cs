using System.Linq;
using System.Text.Json;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Companion.Tests;

/// <summary>
/// Covers how text is mapped onto a voice's token vocabulary.
/// </summary>
/// <remarks>
/// Every bug these guard against is SILENT: an unmapped symbol makes no sound, so
/// the audio is merely shorter and every acoustic measure still reads healthy.
/// A lower-case-only vocabulary once ate the first letter of every sentence in
/// eleven languages and the clarity screen passed all of them — only a listener
/// noticed. These assert the mapping directly instead.
/// </remarks>
public class PiperVoiceConfigMappingTests
{
    /// <summary>A voice whose vocabulary is lower-case Latin, as the real ones are.</summary>
    private static PiperVoiceConfig Voice(string symbols)
    {
        var entries = symbols.Select((c, i) => $"\"{c}\":[{i + 1}]");
        var json = $"{{\"phoneme_id_map\":{{{string.Join(",", entries)}}}}}";
        using var doc = JsonDocument.Parse(json);
        return PiperVoiceConfig.Parse(doc.RootElement);
    }

    private static string[] Chars(string s) => s.Select(c => c.ToString()).ToArray();

    [Fact]
    public void Maps_a_capital_via_its_lower_case_form()
    {
        // "Sawubona" reached the model as "awubona" until this worked: the vocab
        // holds no capitals, and the unmatched 'S' was dropped on the floor.
        var voice = Voice("sawubon");

        var ids = voice.PhonemesToIds(Chars("Sawubona"), out var skipped, out var dropped);

        Assert.Equal(0, skipped);
        Assert.Empty(dropped);
        Assert.Equal(8, ids.Length);
    }

    [Fact]
    public void Prefers_an_exact_match_over_the_lower_case_fallback()
    {
        // A vocabulary that genuinely distinguishes case must keep doing so.
        var voice = Voice("sS");

        var ids = voice.PhonemesToIds(Chars("S"), out _, out _);

        Assert.Equal(2, ids[0]);   // 'S' is the second entry, not 's'
    }

    [Fact]
    public void Folds_a_diacritic_the_voice_lacks_and_declares_it()
    {
        // Sepedi's š and Tshivenda's ṱ ḓ ṋ are absent from the SA-11 vocabulary.
        // Dropping them deletes a consonant mid-word; folding keeps the word whole
        // but is NOT the language's true sound, so it must be reported.
        var voice = Voice("stdn");

        var ids = voice.PhonemesToIds(
            Chars("šṱḓṋ"), out var skipped, out var dropped, out var approximated);

        Assert.Equal(0, skipped);
        Assert.Empty(dropped);
        Assert.Equal(4, ids.Length);
        Assert.Equal(new[] { "š", "ṱ", "ḓ", "ṋ" }, approximated);
    }

    [Fact]
    public void Uses_the_true_phoneme_when_the_vocabulary_carries_it()
    {
        // 'ṅ' IS /ŋ/. When the vocabulary has 'ŋ' — the SA-11 one does — that
        // substitution is exact and loses nothing, unlike folding it to 'n'.
        var voice = Voice("nŋ");

        var ids = voice.PhonemesToIds(Chars("ṅ"), out _, out _, out var approximated);

        Assert.Equal(2, ids[0]);           // 'ŋ', not 'n'
        Assert.Single(approximated);
    }

    [Fact]
    public void An_exactly_mapped_letter_is_never_called_an_approximation()
    {
        var voice = Voice("abc");

        voice.PhonemesToIds(Chars("abc"), out _, out _, out var approximated);

        Assert.Empty(approximated);
    }

    [Fact]
    public void Reports_a_symbol_it_cannot_map_at_all()
    {
        // Punctuation and foreign scripts have no stand-in. Being told is the whole
        // point — the failure is otherwise inaudible.
        var voice = Voice("abc");

        var ids = voice.PhonemesToIds(Chars("a©b"), out var skipped, out var dropped);

        Assert.Equal(1, skipped);
        Assert.Equal(new[] { "©" }, dropped);
        Assert.Equal(2, ids.Length);
    }
}
