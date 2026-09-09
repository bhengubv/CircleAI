// PlayMediaTests.cs
//
// "Play Coldplay" — the first thing the circle can be asked that acts on the
// phone rather than on this app, and the first that can hijack a question.
//
// The parsing is pinned here rather than on a device because it is pure text and
// because getting it wrong is silent: a capability that claims "what does play
// mean" throws somebody off the screen they were using, and nothing in a log
// says so.

using CircleAI.Samples.It;

namespace CircleAI.Samples.It.Ui.Tests;

public class PlayMediaTests
{
    private static string N(string s) => VoiceDestinations.Normalise(s);

    [Theory]
    [InlineData("play Coldplay", "coldplay")]
    [InlineData("play some jazz", "jazz")]
    [InlineData("put on Coldplay", "coldplay")]
    [InlineData("play me something by Adele", "something by adele")]
    [InlineData("listen to Miriam Makeba", "miriam makeba")]
    [InlineData("play the album Parachutes", "album parachutes")]
    public void An_instruction_names_what_to_play(string said, string expected)
        => Assert.Equal(expected, PlayMediaCapability.Subject(N(said)));

    [Theory]
    [InlineData("what does play mean")]
    [InlineData("how do you play chess")]
    [InlineData("the play was good")]
    [InlineData("what is the weather in Durban")]
    [InlineData("who put on the kettle")]
    public void A_question_is_not_an_instruction(string said)
    {
        // POSITION IS THE WHOLE DIFFERENCE. Every one of these contains a word
        // this capability cares about, and none of them is a command. A phrase
        // list would claim all five; the shape test claims none.
        Assert.Null(PlayMediaCapability.Subject(N(said)));
    }

    [Theory]
    [InlineData("play music")]
    [InlineData("play some music")]
    [InlineData("play something")]
    [InlineData("put on a song")]
    public void An_instruction_that_names_nothing_is_still_an_instruction(string said)
    {
        // Empty, not null: it IS a play command and it named nothing, which gets
        // a question rather than a shrug.
        Assert.Equal(string.Empty, PlayMediaCapability.Subject(N(said)));
    }

    [Fact]
    public async Task It_asks_rather_than_choosing_for_you()
    {
        var cap = new PlayMediaCapability(new StubPlayer(PlayResult.Playing));
        var did = await cap.DoAsync(new Ask("play music"));

        Assert.False(did.Done);
        Assert.Equal("Play what?", did.Say);
    }

    [Fact]
    public async Task It_hands_the_words_over_unparsed()
    {
        // The player's own search understands its own catalogue. Splitting
        // artist from album here is the guess that turns "Adele 30" into a
        // search for the number thirty.
        var player = new StubPlayer(PlayResult.Playing);
        var cap = new PlayMediaCapability(player);

        var did = await cap.DoAsync(new Ask("play Adele 30"));

        Assert.True(did.Done);
        Assert.Equal("adele 30", player.Asked);
    }

    [Fact]
    public async Task No_music_app_is_said_plainly_and_not_claimed_as_done()
    {
        var cap = new PlayMediaCapability(new StubPlayer(PlayResult.NoPlayer));
        var did = await cap.DoAsync(new Ask("play Coldplay"));

        Assert.False(did.Done);
        Assert.Contains("no music app", did.Say);
    }

    [Fact]
    public async Task A_head_with_no_device_is_not_ready()
    {
        // The browser. Offering something that cannot run is the broken promise
        // ReadyAsync exists to prevent.
        var cap = new PlayMediaCapability(new NoMediaPlayer());
        var (ready, why) = await cap.ReadyAsync();

        Assert.False(ready);
        Assert.NotEqual(string.Empty, why);
    }

    [Fact]
    public async Task Probing_readiness_does_not_start_anything()
    {
        // Asking "can this phone play music" by opening Spotify would be a
        // readiness check with a side effect somebody can hear.
        var player = new StubPlayer(PlayResult.Playing);
        var cap = new PlayMediaCapability(player);

        await cap.ReadyAsync();

        Assert.Equal(string.Empty, player.Asked);
    }

    private sealed class StubPlayer : IPlaysMedia
    {
        private readonly PlayResult _result;
        public StubPlayer(PlayResult result) => _result = result;

        /// <summary>What it was last asked to play.</summary>
        public string Asked { get; private set; } = string.Empty;

        public Task<PlayResult> PlayAsync(string what, CancellationToken ct = default)
        {
            Asked = what;
            return Task.FromResult(_result);
        }
    }
}
