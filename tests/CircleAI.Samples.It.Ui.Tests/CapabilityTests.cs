// CapabilityTests.cs
//
// The doing side: capabilities that act on a sentence rather than opening the
// screen that would.
//
// WHY THIS MATTERS MORE THAN IT LOOKS. ICapability and CapabilityRegistry were
// written, committed and had NOTHING implementing them - a contract with no
// features on it, which reads on a list as progress and is worth nothing to
// anybody holding the phone. These tests exist against real implementations, so
// "the circle does the work" is a thing that can be checked instead of claimed.

using CircleAI.Samples.It;

namespace CircleAI.Samples.It.Ui.Tests;

public class CapabilityTests
{
    private static CapabilityRegistry Everything(IBrain? brain = null, ISettings? settings = null)
        => CapabilityRegistry.For(brain ?? new FakeBrain(), settings ?? new FakeSettings());

    // ---- Navigation, which is most of them -------------------------------

    [Fact]
    public void Every_place_a_voice_can_reach_is_a_capability()
    {
        // The ten destinations used to be reachable ONLY through the router's
        // own table. If the registry does not carry them, moving to capabilities
        // would quietly lose nine of the ten things voice could already do.
        var registry = Everything();

        foreach (var destination in VoiceDestinations.All)
            Assert.True(
                registry.All.Any(c => c.Title == destination.Title),
                $"nothing in the registry can reach {destination.Title}");
    }

    [Fact]
    public void A_navigate_capability_takes_its_words_from_the_destination()
    {
        // Not a style point. Two lists of the words that mean "settings" is the
        // bug this codebase hit three times in a week; a wrapper cannot drift.
        var settings = VoiceDestinations.All.First(d => d.Route == "settings");
        var capability = new NavigateCapability(settings);

        Assert.Same(settings.Words, capability.Phrases);
        Assert.Equal(settings.Title, capability.Title);
    }

    [Fact]
    public async Task Opening_a_screen_is_free_and_says_where_it_went()
    {
        var capability = new NavigateCapability(
            VoiceDestinations.All.First(d => d.Route == "settings"));

        var did = await capability.DoAsync(new Ask("open settings"));

        Assert.True(did.Done);
        Assert.Equal("settings", did.Route);
        Assert.Equal(Cost.Free, capability.Cost);
    }

    // ---- Recognising a translation --------------------------------------

    // Names are asserted as SampleLanguages spells them, not as English habit
    // does: the catalogue calls zu "isiZulu", and it is the one owner of what a
    // language is called. Note the consequence - somebody who says "in Zulu"
    // is not understood, because no row is called that.
    [Theory]
    [InlineData("how do you say hello in isiZulu", "hello", "isiZulu")]
    [InlineData("How do you say good morning in Japanese?", "good morning", "Japanese")]
    [InlineData("translate where is the station into French", "where is the station", "French")]
    [InlineData("what is thank you in Swahili", "thank you", "Swahili")]
    public void Recognises_a_sentence_that_carries_its_own_job(
        string heard, string expectedText, string expectedLanguage)
    {
        var request = TranslationRequest.Parse(heard);

        Assert.NotNull(request);
        Assert.Equal(expectedText, request!.Text);
        Assert.Equal(expectedLanguage, request.Language.Name);
    }

    [Theory]
    [InlineData("what is the weather")]                  // no language at the end
    [InlineData("open settings")]                        // not a translation at all
    [InlineData("how do you say hello in the morning")]  // "the morning" is not a language
    [InlineData("translate")]                            // nothing to translate
    [InlineData("how do you say in French")]             // no text
    [InlineData("")]
    public void Refuses_anything_that_is_not_one(string heard)
    {
        // NULL IS THE SAFE ANSWER. A sentence that falls through gets the reply
        // it would have got anyway; one that is wrongly recognised answers a
        // question nobody asked in a language nobody wanted.
        Assert.Null(TranslationRequest.Parse(heard));
    }

    [Fact]
    public void Takes_the_language_from_the_end_not_the_first_in_it_finds()
    {
        // "the man in the moon" has an "in" long before the real one. Taking the
        // first would translate "the man" into a language called "the moon in
        // french".
        var request = TranslationRequest.Parse("how do you say the man in the moon in French");

        Assert.NotNull(request);
        Assert.Equal("the man in the moon", request!.Text);
        Assert.Equal("French", request.Language.Name);
    }

    [Fact]
    public void An_endonym_names_a_language_too()
    {
        // It is what the picker shows, so it is what somebody reads out.
        var request = TranslationRequest.Parse("how do you say hello in isiXhosa");

        Assert.NotNull(request);
        Assert.Equal("hello", request!.Text);
    }

    // ---- Doing it --------------------------------------------------------

    [Fact]
    public async Task A_full_request_is_answered_rather_than_navigated()
    {
        // THE WHOLE POINT. This sentence used to get a paragraph about isiZulu
        // from the general model, or a trip to a screen with two languages to
        // set. It carries everything needed, so the reply is the translation and
        // nothing opens.
        var capability = new TranslateCapability(new FakeBrain { Answer = "Sawubona" });

        var did = await capability.DoAsync(new Ask("how do you say hello in isiZulu"));

        Assert.True(did.Done);
        Assert.Equal("Sawubona", did.Say);
        Assert.Null(did.Route);
    }

    [Fact]
    public async Task A_bare_request_opens_the_screen_because_there_is_nothing_to_do_yet()
    {
        // "I need translation" names no words and no target. Guessing what
        // somebody wanted translated would be worse than asking.
        var capability = new TranslateCapability(new FakeBrain());

        var did = await capability.DoAsync(new Ask("I need translation"));

        Assert.True(did.Done);
        Assert.Equal("translate", did.Route);
    }

    [Fact]
    public async Task It_refuses_honestly_when_there_is_no_model()
    {
        // This app spent weeks offering translation it could not speak, and no
        // screen said so. Offering something that cannot run is the broken
        // promise; saying so is the feature.
        var capability = new TranslateCapability(
            new FakeBrain { Ready = false });

        var (ready, why) = await capability.ReadyAsync();
        Assert.False(ready);
        Assert.NotEqual(string.Empty, why);

        var did = await capability.DoAsync(new Ask("how do you say hello in isiZulu"));
        Assert.False(did.Done);
        Assert.Null(did.Route);
    }

    [Fact]
    public async Task A_model_that_throws_becomes_a_sentence_not_a_stack_trace()
    {
        // Did.Say is spoken out loud. An exception type read across a room is
        // noise, and silence is worse - it is indistinguishable from a phone
        // that never heard.
        var capability = new TranslateCapability(
            new FakeBrain { Throws = new InvalidOperationException("boom") });

        var did = await capability.DoAsync(new Ask("how do you say hello in isiZulu"));

        Assert.False(did.Done);
        Assert.Contains("isiZulu", did.Say, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("boom", did.Say);
    }

    [Fact]
    public async Task An_empty_answer_is_reported_rather_than_spoken()
    {
        var capability = new TranslateCapability(new FakeBrain { Answer = "   " });

        var did = await capability.DoAsync(new Ask("how do you say hello in isiZulu"));

        Assert.False(did.Done);
    }

    // ---- The registry ----------------------------------------------------

    [Fact]
    public void The_doing_capability_replaces_the_navigating_one()
    {
        // Otherwise both own the words that mean "translate", they score
        // identically, and Best correctly refuses to choose - so adding the
        // capability that actually translates would stop translation being
        // reachable at all.
        var registry = Everything();

        Assert.Single(registry.All, c => c.Title == "Translate");
        Assert.IsType<TranslateCapability>(registry.Best("open translation"));
    }

    [Fact]
    public void A_question_it_can_answer_is_claimed_despite_reading_as_a_question()
    {
        // The instruction test refuses this sentence - seven words, no asking
        // phrase - and it is right to, because the only thing that used to
        // happen on a match was NAVIGATION. A capability that answers it is not
        // hijacking anything.
        Assert.False(VoiceDestinations.SoundsLikeAnInstruction(
            VoiceDestinations.Normalise("how do you say hello in isiZulu")));

        Assert.IsType<TranslateCapability>(
            Everything().Best("how do you say hello in isiZulu"));
    }

    [Fact]
    public void A_question_nothing_can_answer_is_still_left_alone()
    {
        // The rule that matters most: a router that eats an ordinary question is
        // worse than no router at all.
        var registry = Everything();

        Assert.Null(registry.Best("what did you think of the settings we discussed"));
        Assert.Null(registry.Best("tell me about the languages of southern Africa"));
    }

    // ---- Changing what the app is for -----------------------------------

    [Fact]
    public async Task Saying_so_flips_the_mode()
    {
        // FOUR STEPS THROUGH SETTINGS FOR THE SWITCH THAT CHANGES WHAT THE APP
        // IS. Somebody holding the phone up between two people who cannot
        // understand each other should not have to go and find a menu.
        var settings = new FakeSettings();
        var capability = new SwitchModeCapability(settings, AppMode.Translator);

        var did = await capability.DoAsync(new Ask("switch to translator mode"));

        Assert.True(did.Done);
        Assert.Equal(AppMode.Translator, settings.Settings.Mode);
        Assert.Null(did.Route);
    }

    [Fact]
    public async Task It_goes_back_as_easily_as_it_went()
    {
        // A switch that only throws one way is a trap - and being stuck in
        // Translator mode is precisely the complaint that started this.
        var settings = new FakeSettings { Settings = new AppSettings(Mode: AppMode.Translator) };

        await new SwitchModeCapability(settings, AppMode.Assistant)
            .DoAsync(new Ask("stop translating"));

        Assert.Equal(AppMode.Assistant, settings.Settings.Mode);
    }

    [Fact]
    public async Task Already_being_there_is_said_rather_than_done_silently()
    {
        // Doing nothing quietly is indistinguishable from not having heard,
        // which is this app's whole failure history in one sentence.
        var settings = new FakeSettings();

        var did = await new SwitchModeCapability(settings, AppMode.Assistant)
            .DoAsync(new Ask("assistant mode"));

        Assert.True(did.Done);
        Assert.Contains("already", did.Say, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Changing_a_mode_costs_more_than_opening_a_screen()
    {
        // The first capability to earn a cost above Free. It writes something
        // that outlives the turn and changes how the next one behaves.
        Assert.Equal(Cost.Draft, new SwitchModeCapability(new FakeSettings(), AppMode.Translator).Cost);
        Assert.Equal(Cost.Free, new NavigateCapability(VoiceDestinations.All[0]).Cost);
    }

    [Fact]
    public void Switching_mode_is_not_one_syllable_from_opening_a_screen()
    {
        // "translation" means the SCREEN and "translator mode" means the app's
        // whole behaviour. A transcriber that drops a word must not turn one
        // into the other.
        var registry = Everything();

        Assert.Equal("translator mode", registry.Best("switch to translator mode")?.Id switch
        {
            "mode:translator" => "translator mode",
            var other => other,
        });

        Assert.IsType<TranslateCapability>(registry.Best("open translation"));
    }

    [Fact]
    public void A_registry_with_no_settings_leaves_mode_to_the_screen()
    {
        var registry = CapabilityRegistry.For(new FakeBrain(), settings: null);

        Assert.DoesNotContain(registry.All, c => c is SwitchModeCapability);
    }

    [Fact]
    public void A_registry_with_no_model_still_navigates()
    {
        // A head with nothing to think with can honestly offer screens, and
        // should not pretend to offer more.
        var registry = CapabilityRegistry.For(null);

        Assert.DoesNotContain(registry.All, c => c is TranslateCapability);
        Assert.Equal("Translate", registry.Best("open translation")?.Title);
        Assert.Null(registry.Best("how do you say hello in isiZulu"));
    }
}
