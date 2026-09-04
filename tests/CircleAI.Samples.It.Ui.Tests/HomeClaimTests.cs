// HomeClaimTests.cs
//
// What the home screen PROMISES has to come from what the host can do.
//
// "78 languages, spoken out loud" sat on the home screen of a build that could
// speak one. The number was never a lie about the catalogue - it was a true
// count of catalogue rows, printed as though it were a count of working voices,
// on a head whose phonemizer was never wired. Nothing reconciled the two, and
// nothing could: the claim had one owner and the capability had another.
//
// These pin the claim to its source. They cannot catch an unwired phonemizer -
// that is what the wiring probe is for - but they do catch the claim being
// invented, hard-coded, or kept after the host stops backing it.

using Bunit;
using CircleAI.Samples.It;
using CircleAI.Samples.It.Shared.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.It.Ui.Tests;

public class HomeClaimTests : TestContext
{
    private void Wire(
        IReadOnlyList<VoiceRow>? catalogue = null,
        VoiceAvailability availability = VoiceAvailability.OnDevice,
        AppMode mode = AppMode.Assistant,
        ReadyStage stage = ReadyStage.Ready)
    {
        Services.AddSingleton(new VoiceMark());
        Services.AddSingleton(CapabilityRegistry.For(new FakeBrain(), new FakeSettings()));
        Services.AddSingleton<IConversation>(new FakeConversation());
        Services.AddSingleton<IVoiceHost>(new FakeVoiceHost
        {
            Catalogue = catalogue ?? [],
            Availability = availability,
        });
        Services.AddSingleton<ISetup>(new FakeSetup
        {
            Readiness = new Readiness(stage, "Tap and talk", "", stage == ReadyStage.Ready),
        });
        Services.AddSingleton<ISettings>(new FakeSettings { Settings = new AppSettings(Mode: mode) });
        Services.AddSingleton<ISpokenLanguage>(new FakeSpokenLanguage());
        Services.AddSingleton<IWhereAmI>(new FakeWhereAmI());
    }

    private static VoiceRow[] Voices(params string[] tags)
        => tags.Select(t => new VoiceRow(t, 1024)).ToArray();

    [Fact]
    public void Counts_the_voices_the_host_actually_offers()
    {
        Wire(Voices("en", "zu", "af"));

        var home = RenderComponent<Home>();

        Assert.Contains("3 languages, spoken out loud", home.Markup);
    }

    [Fact]
    public void Claims_no_number_when_the_host_offers_none()
    {
        // THE HEDGE IS THE HONEST ANSWER. A host with nothing catalogued must not
        // print "0 languages, spoken out loud", and must not fall back to a
        // remembered number from a different device.
        Wire(catalogue: []);

        var home = RenderComponent<Home>();

        Assert.DoesNotContain("languages, spoken out loud", home.Markup);
        Assert.Contains("Spoken out loud, in your language", home.Markup);
    }

    [Fact]
    public void The_number_is_never_hard_coded()
    {
        // Two hosts, two numbers, same component. A constant would pass the first
        // assertion in this file and fail here, which is the point of having it.
        Wire(Voices("en", "zu"));

        var home = RenderComponent<Home>();

        Assert.Contains("2 languages, spoken out loud", home.Markup);

        // "78" alone matches SVG path coordinates inside the mark, so the
        // assertion has to name the CLAIM, not the digits.
        Assert.DoesNotContain("78 languages", home.Markup);
    }

    [Fact]
    public void A_head_that_cannot_speak_does_not_promise_speech()
    {
        // The web head has no on-device voice, and the claims are per host: a
        // shared component must not make the phone's promise on a browser.
        Wire(Voices("en", "zu", "af"), availability: VoiceAvailability.Unavailable);

        var home = RenderComponent<Home>();

        Assert.DoesNotContain("Runs on the phone — works with no signal", home.Markup);
    }

    [Fact]
    public void Translator_mode_changes_what_the_circle_offers()
    {
        // A MODE THAT CHANGES NOTHING ON THE SCREEN IT IS A MODE OF IS NOT A
        // MODE. Home used to know only one thing to do, so somebody could set the
        // app to Interpreter, come back, press the circle and get the assistant.
        Wire(Voices("en", "zu"), mode: AppMode.Translator);

        var home = RenderComponent<Home>();

        Assert.Contains("Tap to translate", home.Markup);
    }

    [Fact]
    public void Assistant_mode_offers_a_conversation_instead()
    {
        Wire(Voices("en", "zu"), mode: AppMode.Assistant);

        var home = RenderComponent<Home>();

        Assert.DoesNotContain("Tap to translate", home.Markup);
    }

    [Fact]
    public void An_unset_up_phone_is_not_invited_to_translate()
    {
        // Guarded by readiness for the same reason the headline is: an
        // interpreter with no model is a screen that cannot do the thing it just
        // offered.
        Wire(Voices("en", "zu"), mode: AppMode.Translator, stage: ReadyStage.NeedsSetup);

        var home = RenderComponent<Home>();

        Assert.DoesNotContain("Tap to translate", home.Markup);
    }

    [Fact]
    public void The_circle_shows_a_turn_started_anywhere()
    {
        // Home's half of the same agreement the tab bar is held to.
        Wire(Voices("en"));
        var mark = Services.GetRequiredService<VoiceMark>();

        var home = RenderComponent<Home>();
        home.InvokeAsync(() => mark.Report(new TurnState(TurnPhase.Listening)));

        Assert.Contains("bm-listening", home.Markup);
    }
}
