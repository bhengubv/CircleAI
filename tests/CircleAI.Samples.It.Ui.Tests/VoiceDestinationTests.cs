// VoiceDestinationTests.cs
//
// A router that eats questions is worse than no router.
//
// The risk being tested is not "does it navigate" - that is the easy half. It is
// whether it stays out of the way: every sentence somebody says to an assistant
// that mentions a screen without asking to go there must fall through to a
// normal answer. So most of this file is things that must NOT match.

using CircleAI.Samples.It;

namespace CircleAI.Samples.It.Ui.Tests;

public class VoiceDestinationTests
{
    [Theory]
    [InlineData("I need translation", "translate")]
    [InlineData("Translate.", "translate")]
    [InlineData("open the interpreter", "translate")]
    [InlineData("take me to settings", "settings")]
    [InlineData("go to my cv", "career")]
    [InlineData("what can you do", "abilities")]
    [InlineData("show me the languages", "languages")]
    [InlineData("go home", "home")]
    public void Takes_you_where_you_asked(string heard, string route)
        => Assert.Equal(route, VoiceDestinations.Match(heard)?.Route);

    [Theory]
    [InlineData("how do you say hello in isiZulu")]
    [InlineData("what is the weather going to be like tomorrow morning")]
    [InlineData("can you tell me a story about a dog")]
    [InlineData("my name is Tumelo and I work in software")]
    public void Leaves_a_question_alone(string heard)
        => Assert.Null(VoiceDestinations.Match(heard));

    [Fact]
    public void A_long_sentence_about_a_screen_is_not_a_request_to_open_it()
    {
        // THE ONE THAT MATTERS. Somebody asking ABOUT translation, at length, is
        // having a conversation - and being thrown onto the interpreter mid
        // sentence is exactly the teleport MainLayout's own note forbids.
        Assert.Null(VoiceDestinations.Match(
            "I was wondering how good the translation is between English and isiZulu these days"));
    }

    [Fact]
    public void But_asking_plainly_still_works_however_long_it_is()
    {
        // An asking phrase carries it past the length rule, because now it IS an
        // instruction however wordy.
        Assert.Equal("translate", VoiceDestinations
            .Match("could you please take me to the translation screen now")?.Route);
    }

    [Fact]
    public void Says_nothing_for_silence()
    {
        Assert.Null(VoiceDestinations.Match(null));
        Assert.Null(VoiceDestinations.Match(""));
        Assert.Null(VoiceDestinations.Match("   "));
    }

    [Fact]
    public void Matches_whole_words_only()
    {
        // "chat" is a destination; "chatter" and "chatting about my day" are not.
        Assert.Equal("chat", VoiceDestinations.Match("open chat")?.Route);
        Assert.Null(VoiceDestinations.Match("chatterbox"));
    }

    [Fact]
    public void Punctuation_and_case_do_not_matter()
    {
        // The words arrive through Whisper, which punctuates and capitalises.
        Assert.Equal("settings", VoiceDestinations.Match("Settings!")?.Route);
        Assert.Equal("settings", VoiceDestinations.Match("  SETTINGS  ")?.Route);
    }

    [Fact]
    public void The_more_specific_place_wins()
    {
        // "language list" and "languages" both match; the longer phrase is the
        // one that was actually said.
        Assert.Equal("languages", VoiceDestinations.Match("show me the language list")?.Route);
    }

    [Fact]
    public void Every_destination_points_at_a_route_that_exists()
    {
        // A SPOKEN PROMISE THAT LANDS ON NOT FOUND.
        //
        // THIS TEST WAS ITSELF A FOURTH COPY OF THE ROUTE LIST. It pinned the
        // voice table to a hard-coded array of twelve strings written out here,
        // so adding a screen meant editing the pages, the menu, the voice table
        // AND this - and forgetting the last one turned a new feature into a red
        // test rather than a caught bug. A guard that has to be updated by hand
        // whenever the thing it guards changes is a fifth owner wearing a
        // hi-vis jacket.
        //
        // Both sides now come from AppRoutes, which is the point of AppRoutes.
        foreach (var d in VoiceDestinations.All)
            Assert.NotNull(AppRoutes.For(d.Route));
    }

    [Fact]
    public void Every_route_the_app_declares_is_reachable_somehow()
    {
        // THE QUESTION THE OLD VERSION COULD NOT ASK. Pinning voice to a list of
        // routes catches a destination pointing at nothing; it cannot catch a
        // SCREEN THAT NOTHING POINTS AT, which is the failure that actually
        // happened - Translate was in no menu, reachable only by knowing a mode
        // existed and going to find it.
        //
        // A route with no surface is allowed, but it has to be a decision: the
        // loading, setup and not-found stages own the whole screen and are
        // entered by the app rather than chosen by a person.
        string[] entered = ["", "setup", "not-found"];

        var orphans = AppRoutes.All
            .Where(r => r.Where == Surface.None && !entered.Contains(r.Route))
            .Select(r => r.Route)
            .ToList();

        Assert.True(orphans.Count == 0,
            "These screens exist and nothing offers them - no tab, no mode, no "
            + "tile, no words: " + string.Join(", ", orphans));
    }

    [Fact]
    public void No_destination_is_named_by_a_word_too_common_to_use()
    {
        // The guard behind leaving "You" and "Type" out. A one-word trigger this
        // short turns up inside ordinary speech, and the router would hijack it.
        foreach (var d in VoiceDestinations.All)
            foreach (var w in d.Words)
                Assert.True(w.Length >= 4, $"\"{w}\" ({d.Route}) is too short to be distinctive");
    }
}
