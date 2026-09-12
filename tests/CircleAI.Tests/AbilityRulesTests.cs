// AbilityRulesTests.cs
//
// The rule behind every row on "What it can do", asserted for the first time.
//
// IT WAS UNTESTABLE, SO IT DRIFTED. The decision lived twice - once inside
// DeviceFacts.AbilitiesAsync and once inside AbilitiesActivity.Row - and both
// copies were wrapped in a model registry, a bundle loader, a device probe and,
// in the native head, an Android Activity. Nothing in any of the sixteen test
// projects could reach either.
//
// What that cost, on one phone, at the same moment:
//
//   Settings said "Waking ✓ On" and nothing was listening.
//   The abilities screen said "Answering ✓ On" under a header saying "Getting
//   ready".
//   One screen said "10 plus languages" and the other said 78.
//
// AbilityRules.Decide is that rule over primitives. These are its tests.

using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public class AbilityRulesTests
{
    /// <summary>A downloaded, running, ordinary ability.</summary>
    private static AbilityRow Row(
        bool needsNoModel = false,
        bool needsListening = false,
        long? chosenBytes = 500_000_000,
        bool present = true,
        bool anyCatalogued = true,
        bool listening = false,
        string? route = null)
        => AbilityRules.Decide("Answering", "Answers questions", needsNoModel,
            needsListening, chosenBytes, present, anyCatalogued, listening, route);

    // ── On versus Ready: the distinction the native head never learned ───────

    [Fact]
    public void A_model_on_disk_is_On_for_everything_that_does_not_listen()
    {
        Assert.Equal(AbilityState.On, Row().State);
    }

    [Fact]
    public void Waking_with_the_bundle_downloaded_and_nothing_listening_is_Ready()
    {
        // THE DEFECT, AS ONE ASSERTION. The bundle finishing its download is not
        // the microphone being open, and a tick that means "the file exists" told
        // somebody their phone was awake when it was not.
        var row = Row(needsListening: true, listening: false);

        Assert.Equal(AbilityState.Ready, row.State);
    }

    [Fact]
    public void Waking_is_On_only_while_something_is_actually_listening()
    {
        Assert.Equal(AbilityState.On, Row(needsListening: true, listening: true).State);
    }

    [Fact]
    public void Listening_elsewhere_does_not_make_an_unrelated_ability_Ready()
    {
        // The live listening flag governs waking and nothing else. A version that
        // applied it to every row would have flipped Talking and Answering to
        // Ready the moment the microphone closed.
        Assert.Equal(AbilityState.On, Row(needsListening: false, listening: false).State);
    }

    // ── nothing to download ──────────────────────────────────────────────────

    [Fact]
    public void An_ability_that_needs_no_model_is_On_with_nothing_installed()
    {
        // Music and lexical search are arithmetic. A row whose state is driven by
        // what is on disk reported "unavailable" for the one capability that works
        // on a phone with nothing on it - no size, no button, and no way in.
        var row = AbilityRules.Decide("Music", "Makes a piece of music",
            needsNoModel: true, needsListening: false,
            chosenBytes: null, present: false, anyCatalogued: false,
            listening: false, route: "music");

        Assert.Equal(AbilityState.On, row.State);
        Assert.Null(row.Bytes);
        Assert.Equal("music", row.TryRoute);
    }

    [Fact]
    public void An_ability_that_needs_no_model_never_shows_a_size()
    {
        // Even when the catalogue happens to hold something for that modality.
        var row = AbilityRules.Decide("Finding", "Looks through what you said",
            needsNoModel: true, needsListening: false,
            chosenBytes: 500_000_000, present: false, anyCatalogued: true,
            listening: false);

        Assert.Null(row.Bytes);
    }

    // ── a size belongs to a download and to nothing else ─────────────────────

    [Fact]
    public void Available_carries_the_size_and_nothing_else_does()
    {
        var row = Row(present: false, chosenBytes: 311_000_000);

        Assert.Equal(AbilityState.Available, row.State);
        Assert.Equal(311_000_000, row.Bytes);
    }

    [Theory]
    [InlineData(true,  false, false)]   // On
    [InlineData(true,  true,  false)]   // Ready - downloaded, not listening
    [InlineData(true,  true,  true)]    // On - listening
    public void An_installed_ability_never_advertises_a_download(
        bool present, bool needsListening, bool listening)
    {
        // A size next to something already on the phone reads as "pay for this
        // again", which is the worst thing to show somebody on a metered
        // connection.
        var row = Row(present: present, needsListening: needsListening, listening: listening);

        Assert.Null(row.Bytes);
    }

    // ── our gap versus their phone ───────────────────────────────────────────

    [Fact]
    public void Nothing_that_fits_this_phone_is_TooBig()
    {
        var row = Row(present: false, chosenBytes: null, anyCatalogued: true);

        Assert.Equal(AbilityState.TooBig, row.State);
        Assert.Null(row.Bytes);
    }

    [Fact]
    public void Nothing_catalogued_at_all_is_NotCatalogued()
    {
        // THESE TWO MUST NOT COLLAPSE INTO ONE. "Needs more memory" tells a
        // person their handset is the problem and invites them to go and buy a
        // better one - for a model that does not exist on any phone yet. That is
        // our gap, and it has to read like our gap.
        var row = Row(present: false, chosenBytes: null, anyCatalogued: false);

        Assert.Equal(AbilityState.NotCatalogued, row.State);
    }

    // ── the route ────────────────────────────────────────────────────────────

    [Fact]
    public void A_route_travels_with_the_states_a_person_can_act_on()
    {
        Assert.Equal("wake", Row(needsListening: true, listening: true,  route: "wake").TryRoute);
        Assert.Equal("wake", Row(needsListening: true, listening: false, route: "wake").TryRoute);
    }

    [Fact]
    public void A_row_offering_a_download_is_not_also_a_link_to_the_screen()
    {
        // A row that looks tappable and does nothing is worse than a plain one,
        // and a screen behind an ability that is not installed yet has nothing to
        // show. The button is the affordance here, not the row.
        var row = Row(present: false, chosenBytes: 311_000_000, route: "seeing");

        Assert.Null(row.TryRoute);
    }

    [Fact]
    public void A_row_with_no_screen_carries_no_route()
    {
        Assert.Null(Row().TryRoute);
    }

    // ── the blurb ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0,  "Reads things out loud, in your language")]
    [InlineData(1,  "Reads things out loud, in 1 language")]
    [InlineData(2,  "Reads things out loud, in 2 languages")]
    [InlineData(78, "Reads things out loud, in 78 languages")]
    public void The_language_count_is_written_the_way_it_is_spoken(int n, string expected)
    {
        // "READS THINGS OUT LOUD, IN 1 LANGUAGES", ON A P30, 2026-09-12. The
        // number was right - one voice installed, one language - and the sentence
        // around it was not, because the plural was baked into the template and
        // only the digit was substituted. A number dropped into a fixed plural
        // reads as a machine talking, which is the register this screen exists to
        // stay out of.
        Assert.Equal(expected, AbilityRules.Languages("Reads things out loud, in {n}", n));
    }
}
