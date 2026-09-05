// FirstRunFollowsTheLanguageTests.cs
//
// Every phone on earth downloaded South Africa.
//
// First run named two voices as constants - Piper English and Vits-11ZA, the
// multi-speaker South African bundle - and fetched exactly those on every
// handset, in every country, whatever the phone was set to. The reasoning for
// the PAIR is sound and technical, and is kept: Vits-11ZA is grapheme-driven and
// right for the South African languages, and structurally wrong for English,
// where it measured 0.17 word error rate against Piper lessac's 0.00.
//
// What was missing is a third: a voice for the language the phone is actually
// set to. Seen on a P30 running on Japan time with Japanese selected on the
// translate screen — the loading screen read
//
//     ✓ the English voice          one language
//     ✓ the South African voices   10 languages
//
// and there was no Japanese voice anywhere in the plan. The owner's words were
// "it's too vague ... makes it biased towards RSA", and the bias was not in the
// wording. It was in a constant called Preferred.
//
// ADDED, NOT SUBSTITUTED. English stays because it is what the rest of the app
// falls back to; the South African set stays because it is what this app is for.
// A phone in Osaka or Lagos now also gets its own.

using System.Linq;
using CircleAI.Core;
using CircleAI.Samples.It;
using Xunit;

namespace CircleAI.Tests;

public class FirstRunFollowsTheLanguageTests
{
    private static string[] Titles(string? language) =>
        FirstRun.WantedFor(speech: true, language).Select(w => w.Title).ToArray();

    private static Want? VoiceFor(string? language) =>
        FirstRun.WantedFor(speech: true, language)
            .Where(w => w.Modality == ModelModality.Tts && w.Speaks is not null)
            .Cast<Want?>()
            .FirstOrDefault();

    [Fact]
    public void A_japanese_phone_asks_for_a_japanese_voice()
    {
        // THE BUG, AS A TEST. Nothing in the old plan varied with the language.
        var voice = VoiceFor("ja");

        Assert.NotNull(voice);
        Assert.Equal("ja", voice!.Value.Speaks);
        Assert.Contains("Japanese", voice.Value.Title);
    }

    [Fact]
    public void The_row_is_named_not_called_your_language()
    {
        // It sits beside "the English voice" and "the South African voices" and
        // would read as an afterthought without a name of its own - which is
        // exactly the impression being corrected.
        Assert.Contains("the Japanese voice", Titles("ja"));
        Assert.DoesNotContain(Titles("ja"), t => t.Contains("your language"));
    }

    [Fact]
    public void A_regional_tag_still_finds_its_language()
    {
        // "ja-JP" is a reasonable thing to have stored, and it is Japanese.
        Assert.Equal(VoiceFor("ja")?.Title, VoiceFor("ja-JP")?.Title);
        Assert.Equal(VoiceFor("pt")?.Speaks, VoiceFor("pt_BR")?.Speaks);
    }

    [Fact]
    public void The_two_fixed_voices_are_still_fetched()
    {
        // ADDED, NOT SUBSTITUTED. Removing either would undo a measured decision:
        // English because Vits-11ZA cannot sound out English spelling, and the
        // South African set because it is the reason to want this app at all.
        var titles = Titles("ja");

        Assert.Contains("the English voice", titles);
        Assert.Contains("the South African voices", titles);
    }

    [Fact]
    public void An_english_phone_asks_for_nothing_extra()
    {
        // THE COMMON CASE MUST NOT CHANGE. English is already one of the two, so
        // a phone set to English plans exactly what it planned before.
        Assert.Null(VoiceFor("en"));
        Assert.Equal(Titles(null), Titles("en"));
    }

    [Theory]
    [InlineData("zu")]
    [InlineData("xh")]
    [InlineData("af")]
    [InlineData("st")]
    [InlineData("nso")]
    [InlineData("tn")]
    [InlineData("ts")]
    [InlineData("ss")]
    [InlineData("ve")]
    [InlineData("nr")]
    public void A_south_african_phone_asks_for_nothing_extra(string tag)
    {
        // The multi-speaker bundle already covers these. Asking again would
        // download a second voice for a language the phone can already speak.
        Assert.Null(VoiceFor(tag));
    }

    [Fact]
    public void No_language_at_all_plans_what_it_always_did()
    {
        // The console head and any caller with no opinion. Null must not mean
        // "fetch something arbitrary".
        Assert.Null(VoiceFor(null));
        Assert.Null(VoiceFor(""));
        Assert.Null(VoiceFor("   "));
    }

    [Fact]
    public void The_extra_voice_is_chosen_by_language_not_by_rank()
    {
        // WHY Speaks EXISTS AT ALL. A null Speaks lets the selector pick on
        // quality and fit, which is right for the ears and the brain and useless
        // here: it would hand back the highest-ranked voice in the catalogue,
        // which is how a Nepali voice once ended up in an English assistant's
        // mouth. The point of this row is the language.
        Assert.Equal("ja", VoiceFor("ja")?.Speaks);
        Assert.Null(VoiceFor("ja")?.Named);
    }

    [Fact]
    public void A_chat_only_build_still_plans_no_voices()
    {
        // speech:false is the chat-only APK, which cannot open a voice at all.
        // Threading a language through must not start it downloading one.
        var titles = FirstRun.WantedFor(speech: false, "ja").Select(w => w.Title).ToArray();

        Assert.Equal(["the brain"], titles);
    }
}
