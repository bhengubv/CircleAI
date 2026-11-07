// SpokenOpeningTests.cs
//
// The voice starts on the opening words of a reply, not at the end of its first sentence. Everything
// after the opening is still handed on sentence by sentence.

using System.Text;
using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public sealed class SpokenOpeningTests
{
    private static (string? Opening, string Left) Take(string text, int words = 4)
    {
        var buffer = new StringBuilder(text);
        return (SpokenReply.TakeOpening(buffer, words), buffer.ToString());
    }

    [Fact]
    public void The_opening_is_the_first_few_whole_words()
    {
        var (opening, left) = Take("The capital of Kenya is");

        Assert.Equal("The capital of Kenya", opening);
        Assert.Equal(" is", left);
    }

    [Theory]
    [InlineData("Sparrows are", "Sparrows", " are")]
    [InlineData("A sparrow is", "A sparrow", " is")]
    [InlineData("The sky is", "The sky", " is")]
    public void One_word_is_enough_when_it_can_stand_alone(string text, string expected, string rest)
    {
        var (opening, left) = Take(text, words: 1);

        Assert.Equal(expected, opening);
        Assert.Equal(rest, left);
    }

    [Fact]
    public void A_word_at_the_end_of_the_buffer_may_still_be_growing()
    {
        Assert.Null(Take("The capital of Ken").Opening);
    }

    [Theory]
    [InlineData("Well then, that is", "Well then,", " that is")]
    [InlineData("Two things: first this", "Two things:", " first this")]
    public void A_clause_comes_sooner_than_the_word_count(string text, string expected, string rest)
    {
        var (opening, left) = Take(text);

        Assert.Equal(expected, opening);
        Assert.Equal(rest, left);
    }

    [Fact]
    public void One_word_and_a_comma_is_not_an_opening()
    {
        Assert.Null(Take("Yes, it").Opening);
        Assert.Equal("Yes, it is the", Take("Yes, it is the capital").Opening);
    }

    [Theory]
    [InlineData("It costs 3,5 million")]
    [InlineData("We meet at 12:30 today")]
    public void Numbers_are_not_clauses(string text)
    {
        var (opening, _) = Take(text, words: 9);

        Assert.Null(opening);
    }

    [Fact]
    public async Task The_first_piece_is_the_opening_and_the_rest_are_sentences()
    {
        var said = new List<string>();
        await using (var reply = new SpokenReply((s, _) => { lock (said) said.Add(s); return Task.CompletedTask; }) { FirstWords = 4 })
        {
            foreach (var fragment in new[] { "The capital ", "of Kenya ", "is Nairobi. ", "It is ", "a big city. ", "People like it" })
                reply.Push(fragment);
            await reply.CompleteAsync();
        }

        Assert.Equal(["The capital of Kenya", "is Nairobi.", "It is a big city.", "People like it"], said);
    }

    [Fact]
    public async Task The_pieces_after_the_opening_grow_up_to_the_most_a_piece_may_wait_for()
    {
        var said = new List<string>();
        await using (var reply = new SpokenReply((s, _) => { lock (said) said.Add(s); return Task.CompletedTask; }) { FirstWords = 3, NextWords = 6 })
        {
            foreach (var word in "one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen ".Split(' '))
                reply.Push(word + " ");
            await reply.CompleteAsync();
        }

        Assert.Equal([3, 4, 6, 6], said.Select(s => s.Split(' ').Length).Take(4));
    }

    [Fact]
    public async Task A_short_first_sentence_goes_as_it_is()
    {
        var said = new List<string>();
        await using (var reply = new SpokenReply((s, _) => { lock (said) said.Add(s); return Task.CompletedTask; }) { FirstWords = 4 })
        {
            reply.Push("Yes. ");
            reply.Push("It is the one you want.");
            await reply.CompleteAsync();
        }

        Assert.Equal(["Yes.", "It is the one you want."], said);
    }

    [Fact]
    public async Task With_no_opening_asked_for_nothing_changes()
    {
        var said = new List<string>();
        await using (var reply = new SpokenReply((s, _) => { lock (said) said.Add(s); return Task.CompletedTask; }))
        {
            reply.Push("The capital of Kenya is ");
            reply.Push("Nairobi. ");
            await reply.CompleteAsync();
        }

        Assert.Equal(["The capital of Kenya is Nairobi."], said);
    }

    [Fact]
    public async Task Audio_held_in_memory_renders_ahead_of_the_one_playing()
    {
        var played = new List<byte[]>();
        var rendered = 0;
        var firstPlaying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var reply = new SpokenReply(
            render: (s, _) => { Interlocked.Increment(ref rendered); return Task.FromResult<byte[]?>([(byte)s.Length]); },
            play: async (pcm, _) =>
            {
                played.Add(pcm);
                firstPlaying.TrySetResult();
                await release.Task;
            });

        reply.Push("One. ");
        reply.Push("Three. ");
        await firstPlaying.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 200 && Volatile.Read(ref rendered) < 2; i++) await Task.Delay(5);

        Assert.Equal(2, Volatile.Read(ref rendered));

        release.SetResult();
        await reply.CompleteAsync();
        Assert.Equal([4, 6], played.Select(p => (int)p[0]));
    }
}
