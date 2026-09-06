// TranscribeSessionTests.cs
//
// The screen that takes down a meeting, and the two decisions it actually makes.
//
// It used to loop DictateAsync, one call per utterance. That worked and does not
// scale to what the screen is for: a microphone opened and closed once per
// sentence flickers the recording indicator, pays the open cost at every pause,
// and loses whatever is said in the gap between closing and reopening - and it
// kept no audio, so there was nothing to read back at the end.
//
// It now runs ONE session. Which leaves the screen two things to get right, and
// both of them are easy to get wrong in a way no compiler notices.

using Bunit;
using CircleAI.Samples.It;
using CircleAI.Samples.It.Shared.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.It.Ui.Tests;

public class TranscribeSessionTests : TestContext
{
    private FakeConversation Wire(string sessionText = "")
    {
        var talk = new FakeConversation { SessionText = sessionText };
        Services.AddSingleton<IConversation>(talk);
        Services.AddSingleton<ISpokenLanguage>(new FakeSpokenLanguage());
        Services.AddSingleton(new VoiceMark());
        JSInterop.Mode = JSRuntimeMode.Loose;
        return talk;
    }

    [Fact]
    public void Recording_runs_one_session_not_a_loop_of_utterances()
    {
        var talk = Wire();
        var page = RenderComponent<Transcribe>();

        page.Find("button.record").Click();

        page.WaitForAssertion(() => Assert.Equal(1, talk.Sessions), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_meeting_gets_a_meeting_length_silence()
    {
        // THE ONE SETTING THIS SCREEN HAS TO CHOOSE. People pause to think in a
        // meeting, and cutting at every breath shreds one sentence across three
        // decodes and three chances to punctuate it wrongly. A question wants
        // about a second; passing that here would cut somebody off mid-thought.
        //
        // Asserted as "seconds, not milliseconds" rather than pinned to 5000, so
        // tuning the number stays possible and turning it into a question-length
        // gap does not.
        var talk = Wire();
        var page = RenderComponent<Transcribe>();

        page.Find("button.record").Click();

        page.WaitForAssertion(() => Assert.NotNull(talk.SessionSilenceMs), TimeSpan.FromSeconds(5));
        Assert.True(talk.SessionSilenceMs >= 3000,
            $"a meeting is being cut after {talk.SessionSilenceMs} ms of silence, "
            + "which is a pause for thought");
    }

    [Fact]
    public void Stopping_ends_the_session()
    {
        // Stop has to work mid-sentence: somebody ending a meeting presses it
        // while a person is still talking, and a button that waits for the
        // current sentence reads as broken.
        var talk = Wire();
        var page = RenderComponent<Transcribe>();

        page.Find("button.record").Click();
        page.WaitForAssertion(() => Assert.Equal(1, talk.Sessions), TimeSpan.FromSeconds(5));

        page.Find("button.record").Click();

        page.WaitForAssertion(() => Assert.True(talk.WasCancelled), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void The_re_read_replaces_the_transcript_rather_than_doubling_it()
    {
        // THE TRAP IN THE NEW SHAPE. A session reports the text so far as each
        // piece lands, and then reports it ONCE MORE after re-reading the whole
        // recording - so a screen that appends what it is handed shows the
        // meeting twice, the second time slightly differently worded, which reads
        // as the app having lost track of what was said.
        var talk = Wire("The meeting is at three, in the small room.");
        var page = RenderComponent<Transcribe>();

        page.Find("button.record").Click();
        page.WaitForAssertion(() => Assert.Equal(1, talk.Sessions), TimeSpan.FromSeconds(5));
        page.Find("button.record").Click();

        page.WaitForAssertion(
            () => Assert.Contains("small room", page.Markup),
            TimeSpan.FromSeconds(5));

        // Once. Not once live and once again after the closing pass.
        var markup = page.Markup;
        var first = markup.IndexOf("small room", StringComparison.Ordinal);
        var second = markup.IndexOf("small room", first + 1, StringComparison.Ordinal);
        Assert.True(second < 0, "the transcript was shown twice after the closing pass");
    }
}
