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

    // ── Two stages: render one ahead of play ─────────────────────────────

    [Fact]
    public async Task The_next_sentence_renders_while_this_one_plays()
    {
        // THE POINT OF THE SECOND STAGE. With one call per sentence, sentence
        // two cannot begin rendering until sentence one has finished playing.
        // Here rendering of sentence two must have STARTED before playback of
        // sentence one has ended.
        var playing = new TaskCompletionSource();     // set when play #1 starts
        var release = new TaskCompletionSource();     // play #1 waits on this
        var renderStarts = new List<string>();

        var reply = new SpokenReply(
            render: async (s, _) => { renderStarts.Add(s); await Task.Yield(); return s + ".wav"; },
            play: async (item, _) =>
            {
                if (item == "One..wav") { playing.TrySetResult(); await release.Task; }
            });

        reply.Push("One. Two. ");
        await playing.Task;                            // play #1 is in progress

        // Give the renderer a moment: it should have started on "Two." already.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (renderStarts.Count < 2 && DateTime.UtcNow < deadline) await Task.Delay(10);

        Assert.Equal(["One.", "Two."], renderStarts);
        release.SetResult();
        await reply.CompleteAsync();
        Assert.Equal(2, reply.Spoken);
    }

    [Fact]
    public async Task Rendering_does_not_run_away_from_playback()
    {
        // Bounded look-ahead: a long answer must not render itself to the end
        // while the first sentence is still being heard - if the person
        // interrupts, everything past the look-ahead is wasted work.
        var release = new TaskCompletionSource();
        var renderedCount = 0;

        var reply = new SpokenReply(
            render: (s, _) => { Interlocked.Increment(ref renderedCount); return Task.FromResult<string?>(s); },
            play: async (_, _) => await release.Task,
            lookAhead: 1);

        reply.Push("A. B. C. D. E. F. ");
        await Task.Delay(300);

        // Play #1 holds; the renderer may finish #2 (into the buffer) and be
        // blocked on #3 at most - never the whole six.
        Assert.True(renderedCount <= 3, $"rendered {renderedCount} ahead of a stalled player");

        release.SetResult();
        await reply.CompleteAsync();
        Assert.Equal(6, reply.Spoken);
    }

    [Fact]
    public async Task A_sentence_that_cannot_be_rendered_is_skipped_not_fatal()
    {
        var played = new List<string>();
        var reply = new SpokenReply(
            render: (s, _) => Task.FromResult<string?>(s.StartsWith("Bad") ? null : s),
            play: (item, _) => { played.Add(item); return Task.CompletedTask; });

        reply.Push("Good. Bad. Fine.");
        await reply.CompleteAsync();

        Assert.Equal(["Good.", "Fine."], played);
    }

    [Fact]
    public async Task Started_does_not_complete_until_a_sentence_is_actually_spoken()
    {
        // THE BUG THIS SIGNAL EXISTS FOR. Barge-in armed when the reply object
        // was created and spent the model's whole 5-13 second think gap
        // listening with nothing to interrupt, cancelling replies before a word
        // of them was spoken.
        var release = new TaskCompletionSource();
        var reply = new SpokenReply(async (_, _) => await release.Task);

        Assert.False(reply.Started.IsCompleted, "armed before anything was queued");

        reply.Push("Not a whole sentence yet");
        await Task.Delay(100);
        Assert.False(reply.Started.IsCompleted, "armed on an unfinished sentence");

        reply.Push(". ");
        await reply.Started.WaitAsync(TimeSpan.FromSeconds(5));

        release.SetResult();
        await reply.CompleteAsync();
    }

    [Fact]
    public async Task Started_never_completes_when_nothing_is_spoken()
    {
        // A reply that produced no audio cannot be interrupted, so the watcher
        // must never open a microphone for it.
        var reply = new SpokenReply((_, _) => Task.CompletedTask);
        await reply.CompleteAsync();

        Assert.False(reply.Started.IsCompleted);
    }
}
