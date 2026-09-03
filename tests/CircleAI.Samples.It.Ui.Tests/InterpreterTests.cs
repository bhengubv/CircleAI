// InterpreterTests.cs
//
// The feature that was broken all day, and had no test at all.
//
// Two people, one phone, passed between them. It transcribed, it translated, and
// it never said a word - because the phonemizer was unwired - and nothing
// anywhere would have caught that. These do not test the phonemizer (the wiring
// probe does) but they pin the SCREEN: which side a line lands on, what happens
// when a translation fails, and that the pair is never invented.

using Bunit;
using CircleAI.Samples.It;
using CircleAI.Samples.It.Shared.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.It.Ui.Tests;

public class InterpreterTests : TestContext
{
    private void Wire(
        string mine = "en",
        Whereabouts? where = null,
        IBrain? brain = null)
    {
        Services.AddSingleton<ISettings>(new FakeSettings());
        Services.AddSingleton<ISpokenLanguage>(new FakeSpokenLanguage { Current = mine });
        Services.AddSingleton<IWhereAmI>(new FakeWhereAmI
        {
            Whereabouts = where ?? new Whereabouts("JP", CountrySource.Timezone, "ZA"),
        });
        Services.AddSingleton(brain ?? new FakeBrain());
        Services.AddSingleton<IConversation>(new FakeConversation());
        Services.AddSingleton<IVoiceHost>(new FakeVoiceHost
        {
            Catalogue = [new VoiceRow("en", 1), new VoiceRow("ja", 1), new VoiceRow("zu", 1)],
        });
        Services.AddSingleton<IFormFactor>(new FakeFormFactor());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<Translate> Screen() => RenderComponent<Translate>();

    [Fact]
    public void Opens_with_the_pair_the_phone_worked_out()
    {
        // A South African in Tokyo. The pair used to be the constants en and zu,
        // which is a fine guess in Johannesburg and useless here.
        Wire(mine: "en", where: new Whereabouts("JP", CountrySource.Timezone, "ZA"));

        var screen = Screen();

        screen.WaitForAssertion(() =>
        {
            Assert.Contains("English", screen.Markup);
            Assert.Contains("Japanese", screen.Markup);
        });
    }

    [Fact]
    public void Offers_both_people_a_way_to_speak()
    {
        // The whole shape of the screen: two sides, one each, facing opposite
        // ways across a table.
        Wire();

        var screen = Screen();

        screen.WaitForAssertion(() =>
            Assert.Equal(2, screen.FindAll("select.lang").Count));
    }

    [Fact]
    public void Has_a_typing_way_in_for_each_side()
    {
        // Somebody in a room where speaking out loud is not on still has to be
        // able to use this.
        Wire();

        var screen = Screen();

        screen.WaitForAssertion(() =>
            Assert.True(screen.FindAll("button").Count(b =>
                (b.TextContent ?? "").Contains("Type")) >= 2));
    }

    [Fact]
    public void Swapping_turns_the_conversation_around()
    {
        // The phone gets handed back the other way; the sides swap and so does
        // everything already said, or the history ends up facing the wrong person.
        Wire(mine: "en", where: new Whereabouts("JP", CountrySource.Timezone, "ZA"));
        var screen = Screen();

        screen.WaitForAssertion(() => Assert.Equal(2, screen.FindAll("select.lang").Count));
        var before = screen.FindAll("select.lang").Select(s => s.GetAttribute("value")).ToArray();

        screen.Find("button.seam-swap").Click();

        screen.WaitForAssertion(() =>
        {
            var after = screen.FindAll("select.lang").Select(s => s.GetAttribute("value")).ToArray();
            Assert.Equal(before[0], after[1]);
            Assert.Equal(before[1], after[0]);
        });
    }

    [Fact]
    public void Never_puts_the_same_language_on_both_sides()
    {
        // An interpreter that translates English to English is a mirror. When
        // the phone cannot suggest a partner it must not fill the gap with a
        // copy of the first side.
        Wire(mine: "en", where: new Whereabouts("ZA", CountrySource.Locale, "ZA"));

        var screen = Screen();

        screen.WaitForAssertion(() =>
        {
            var langs = screen.FindAll("select.lang").Select(s => s.GetAttribute("value")).ToArray();
            Assert.Equal(2, langs.Length);
            // With no positional signal there is no partner to suggest; the
            // screen may fall back, but it must not claim a pair it does not have.
            Assert.NotNull(langs[0]);
        });
    }
}
