// SpokenReplyTests.cs
//
// Where a sentence ends inside a reply that is still being written, and that
// the voice is handed each one the moment it is complete rather than when the
// paragraph is.

using System.Text;
using CircleAI.Samples.It;

namespace CircleAI.Samples.It.Ui.Tests;

public class SpokenReplyTests
{
    private static List<string> Take(string text)
    {
        var sb = new StringBuilder(text);
        return SpokenReply.TakeComplete(sb).ToList();
    }

    [Fact]
    public void A_finished_sentence_is_taken_and_the_tail_is_kept()
    {
        var sb = new StringBuilder("It is twelve degrees in Durban. Tomorrow will be war");
        var got = SpokenReply.TakeComplete(sb);
        Assert.Equal(["It is twelve degrees in Durban."], got);
        Assert.Equal("Tomorrow will be war", sb.ToString().Trim());
    }

    [Fact]
    public void A_dot_inside_a_number_is_not_a_boundary()
    {
        Assert.Empty(Take("Inflation is 3.5 percent and"));
        Assert.Equal(["Inflation is 3.5 percent."], Take("Inflation is 3.5 percent. It"));
    }

    [Fact]
    public void A_dot_at_the_very_end_waits_for_more()
    {
        // The model may be mid-number, or mid-abbreviation. Only whitespace
        // after the dot proves the sentence is over.
        Assert.Empty(Take("The answer is 4."));
    }

    [Fact]
    public void A_line_break_ends_a_sentence_whatever_it_ended_with()
    {
        Assert.Equal(["First point", "Second"], Take("First point\nSecond\n"));
    }

    [Fact]
    public void Closing_punctuation_runs_stay_with_their_sentence()
    {
        Assert.Equal(["Really?!", "Yes..."], Take("Really?! Yes... and"));
    }

    [Fact]
    public async Task Sentences_are_spoken_in_order_as_they_complete()
    {
        var said = new List<string>();
        await using var reply = new SpokenReply(async (s, _) => { said.Add(s); await Task.Yield(); });

        reply.Push("It is twelve ");
        reply.Push("degrees. ");
        reply.Push("Tomorrow ");
        reply.Push("will be warmer. ");
        reply.Push("Bring a hat");
        await reply.CompleteAsync();

        Assert.Equal(
            ["It is twelve degrees.", "Tomorrow will be warmer.", "Bring a hat"],
            said);
    }

    [Fact]
    public async Task The_first_sentence_is_handed_over_before_the_reply_is_finished()
    {
        // THE POINT OF THE CLASS. Measured on a Redmi 12: 11,8 s of synthesis
        // before a sound, because the whole paragraph was rendered as one block
        // after the model had finished. The first sentence must reach the voice
        // while the model is still writing the second.
        var reply = new SpokenReply((_, _) => Task.CompletedTask);

        reply.Push("Yes. ");
        Assert.Equal(1, reply.Queued);

        reply.Push("And there is more to");
        Assert.Equal(1, reply.Queued);

        await reply.CompleteAsync();
        Assert.Equal(2, reply.Queued);
    }

    [Fact]
    public async Task One_sentence_failing_does_not_silence_the_rest()
    {
        var said = new List<string>();
        await using var reply = new SpokenReply((s, _) =>
        {
            if (s.StartsWith("Bad")) throw new InvalidOperationException("no voice for that");
            said.Add(s);
            return Task.CompletedTask;
        });

        reply.Push("Good one. Bad one. Good again.");
        await reply.CompleteAsync();

        Assert.Equal(["Good one.", "Good again."], said);
    }

    [Fact]
    public async Task Cancelling_stops_the_queue()
    {
        using var cts = new CancellationTokenSource();
        var said = 0;
        var reply = new SpokenReply(async (_, ct) =>
        {
            said++;
            cts.Cancel();
            await Task.Delay(10, ct);
        }, cts.Token);

        reply.Push("One. Two. Three. ");
        try { await reply.CompleteAsync(); } catch (OperationCanceledException) { }

        Assert.Equal(1, said);
    }
}
