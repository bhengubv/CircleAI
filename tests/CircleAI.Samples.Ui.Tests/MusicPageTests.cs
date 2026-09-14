// MusicPageTests.cs
//
// Making a piece, and getting it off the phone.
//
// THE SCREEN COULD MAKE MUSIC AND NOT GIVE IT TO ANYBODY. It printed the
// app-private path and stopped there - a real WAV, in a folder nothing on the
// phone can browse to, which for the person holding it is the same as not having
// made anything. The other head offered "Save as WAV" through a document picker.
//
// This page had no tests at all, because WireEverything registered neither
// IMakesMusic nor IPlaysMedia, so the container could not build it.

using AngleSharp.Dom;
using Bunit;
using CircleAI.Assistant;
using CircleAI.Samples.Shared.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.Ui.Tests;

public class MusicPageTests : TestContext
{
    /// <summary>Wire the page with a music maker this test can interrogate.</summary>
    FakeMusic Wire(FakeMusic? music = null)
    {
        var fake = music ?? new FakeMusic();

        // AFTER WireEverything, AND THE ORDER IS NOT ARBITRARY. That method
        // registers IMakesMusic with AddSingleton, and the LAST AddSingleton for
        // a service type is the one the container resolves - so registering
        // first is silently overwritten and the test passes against the default
        // fake while appearing to configure its own.
        //
        // IRemembers is the opposite way round, because it arrives through
        // AddConversationStore's TryAddSingleton - which keeps the first. Two
        // conventions in one container; this is the one that bites.
        this.WireEverything();
        Services.AddSingleton<IMakesMusic>(fake);
        return fake;
    }

    static IElement Mood(IRenderedFragment cut, string name)
        => cut.FindAll("button.music-mood").First(b => b.TextContent.Trim() == name);

    [Fact]
    public void Every_mood_the_maker_offers_is_on_the_screen()
    {
        // NOT A SUBSET CHOSEN HERE. The day a mood is added, a screen holding its
        // own list would silently not offer it - which is the shape of half the
        // bugs this whole exercise has been about.
        var music = Wire();
        var cut = RenderComponent<Music>();

        Assert.Equal(music.Moods.Count, cut.FindAll("button.music-mood").Count);
    }

    [Fact]
    public void Pressing_a_mood_asks_for_that_mood()
    {
        var music = Wire();
        var cut = RenderComponent<Music>();

        Mood(cut, nameof(MusicMood.Calm)).Click();

        Assert.Equal([MusicMood.Calm], music.Asked);
    }

    [Fact]
    public void A_finished_piece_can_be_sent_somewhere()
    {
        // THE WHOLE GAP. Before this the only thing offered after making
        // something was "Play it again" and a path nobody can reach.
        var music = Wire();
        var cut = RenderComponent<Music>();

        Mood(cut, nameof(MusicMood.Calm)).Click();

        var send = cut.FindAll("button.music-play")
                      .First(b => b.TextContent.Contains("Send", StringComparison.Ordinal));
        send.Click();

        Assert.Equal([music.Made], music.Shared);
    }

    [Fact]
    public void Nothing_is_offered_to_send_before_anything_is_made()
    {
        Wire();
        var cut = RenderComponent<Music>();

        Assert.DoesNotContain("Send it somewhere", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_phone_with_nothing_that_takes_a_wav_still_says_where_the_file_is()
    {
        // A DECLINED SHARE SHEET IS NOT A LOST PIECE OF MUSIC. The file is real
        // either way, so the path stays on screen and the status explains rather
        // than reporting a failure.
        var music = Wire(new FakeMusic { ShareWorks = false });
        var cut = RenderComponent<Music>();

        Mood(cut, nameof(MusicMood.Calm)).Click();
        cut.FindAll("button.music-play")
           .First(b => b.TextContent.Contains("Send", StringComparison.Ordinal))
           .Click();

        Assert.Contains("takes a WAV", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(music.Made!, cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_head_that_cannot_make_music_offers_no_moods()
    {
        // The browser. It says so rather than showing dead buttons - the same
        // shape as every other capability seam here.
        Wire(new FakeMusic { Available = false });
        var cut = RenderComponent<Music>();

        Assert.Empty(cut.FindAll("button.music-mood"));
    }
}
