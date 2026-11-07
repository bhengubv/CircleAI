// LiveHearingTests.cs
//
// Somebody is still talking: the ears take a block and go, the words are written down on a thread of
// their own, and whoever is listening is told when the words look finished and when they were not.

using System.Collections.Concurrent;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class LiveHearingTests
{
    private const int Block = 3_200;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>Hands back the next line of a script on each update.</summary>
    [Fact]
    public async Task Updates_that_write_nothing_new_are_counted_and_new_words_start_the_count_again()
    {
        var ears = new Scripted("Hello there", "Hello there", "Hello there", "Hello there now");
        await using var live = new LiveHearing(ears, 2);

        for (var i = 0; i < 3; i++)
        {
            live.Hear(new byte[2], TimeSpan.Zero);
            await Until(() => ears.Updates == i + 1);
        }
        await Until(() => live.SameWordsFor == 2);

        live.Hear(new byte[2], TimeSpan.Zero);
        await Until(() => live.Words == "Hello there now");
        Assert.Equal(0, live.SameWordsFor);
    }

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++) await Task.Delay(10);
        Assert.True(done());
    }

    private sealed class Scripted(params string[] lines) : IHearing
    {
        private int _next;

        public int Updates;
        public int Finishes;
        public int Bytes;
        public readonly TaskCompletionSource<string> Hold = new();
        public bool Holding;

        public string Spent => "spent";

        public void Hear(ReadOnlySpan<byte> pcm16) => Interlocked.Add(ref Bytes, pcm16.Length);

        public string Update()
        {
            if (Holding) Hold.Task.GetAwaiter().GetResult();
            Interlocked.Increment(ref Updates);
            return lines[Math.Min(_next++, lines.Length - 1)];
        }

        public string Peek() => Update();

        public string Finish()
        {
            Interlocked.Increment(ref Finishes);
            return lines[^1];
        }

        public void Dispose()
        {
        }
    }

    private static byte[] Audio() => new byte[Block];

    [Fact]
    public async Task Words_are_written_down_as_the_audio_goes_by()
    {
        var script = new Scripted("Why", "Why is the");
        await using var live = new LiveHearing(script, bytesPerUpdate: Block * 2);
        var changed = new BlockingCollection<string>();
        live.Changed += changed.Add;

        live.Hear(Audio(), TimeSpan.Zero);
        live.Hear(Audio(), TimeSpan.Zero);
        Assert.True(changed.TryTake(out var first, Wait));

        live.Hear(Audio(), TimeSpan.Zero);
        live.Hear(Audio(), TimeSpan.Zero);
        Assert.True(changed.TryTake(out var second, Wait));

        Assert.Equal("Why", first);
        Assert.Equal("Why is the", second);
        Assert.Equal("Why is the", live.Words);
    }

    [Fact]
    public async Task A_quiet_says_so_once_with_the_words_it_has()
    {
        var script = new Scripted("Why is the sky blue?");
        await using var live = new LiveHearing(script, bytesPerUpdate: Block * 100);
        var settled = new BlockingCollection<string>();
        live.Settled += settled.Add;

        live.Hear(Audio(), TimeSpan.Zero);
        live.Hear(Audio(), LiveHearing.SettledAfter);
        live.Hear(Audio(), LiveHearing.SettledAfter + Ms(100));
        live.Hear(Audio(), LiveHearing.SettledAfter + Ms(200));

        Assert.True(settled.TryTake(out var words, Wait));
        Assert.Equal("Why is the sky blue?", words);

        await live.FinishAsync().WaitAsync(Wait);
        Assert.Equal(0, settled.Count);
    }

    [Fact]
    public async Task A_quiet_is_still_a_quiet_when_there_is_nothing_new_to_read()
    {
        // MEASURED ON THE P30 ON 2026-11-06: the blocks of a long update are taken together, so the wake-ups
        // they each left behind found an empty queue, read that as no quiet at all, and took back an answer
        // the brain had just started.
        var script = new Scripted("Why is the sky blue?") { Holding = true };
        await using var live = new LiveHearing(script, bytesPerUpdate: Block * 100);
        var wentOn = 0;
        var settled = new TaskCompletionSource();
        live.WentOn += () => Interlocked.Increment(ref wentOn);
        live.Settled += _ => settled.TrySetResult();

        live.Hear(Audio(), LiveHearing.SettledAfter);
        for (var i = 1; i <= 5; i++) live.Hear(Audio(), LiveHearing.SettledAfter + Ms(100 * i));
        script.Hold.SetResult("go");

        await settled.Task.WaitAsync(Wait);
        await live.FinishAsync().WaitAsync(Wait);

        Assert.Equal(0, wentOn);
    }

    [Fact]
    public async Task Carrying_on_takes_it_back_and_the_next_quiet_says_so_again()
    {
        var script = new Scripted("What is", "What is the capital of Kenya?");
        await using var live = new LiveHearing(script, bytesPerUpdate: Block * 100);
        var said = new BlockingCollection<string>();
        live.Settled += words => said.Add("settled: " + words);
        live.WentOn += () => said.Add("went on");

        live.Hear(Audio(), LiveHearing.SettledAfter);
        Assert.True(said.TryTake(out var first, Wait));

        live.Hear(Audio(), TimeSpan.Zero);
        Assert.True(said.TryTake(out var second, Wait));

        live.Hear(Audio(), LiveHearing.SettledAfter);
        Assert.True(said.TryTake(out var third, Wait));

        Assert.Equal("settled: What is", first);
        Assert.Equal("went on", second);
        Assert.Equal("settled: What is the capital of Kenya?", third);
    }

    [Fact]
    public async Task Nothing_settles_on_no_words()
    {
        var script = new Scripted(string.Empty);
        await using var live = new LiveHearing(script, bytesPerUpdate: Block * 100);
        var settled = 0;
        live.Settled += _ => Interlocked.Increment(ref settled);

        live.Hear(Audio(), LiveHearing.SettledAfter);
        await live.FinishAsync().WaitAsync(Wait);

        Assert.Equal(0, settled);
    }

    [Fact]
    public async Task The_end_of_the_audio_gets_a_last_pass_over_all_of_it()
    {
        var script = new Scripted("Name one", "Name one kind of bird.");
        await using var live = new LiveHearing(script, bytesPerUpdate: Block * 100);

        for (var i = 0; i < 3; i++) live.Hear(Audio(), TimeSpan.Zero);

        Assert.Equal("Name one kind of bird.", await live.FinishAsync().WaitAsync(Wait));
        Assert.Equal(1, script.Finishes);
        Assert.Equal(Block * 3, script.Bytes);
    }

    [Fact]
    public async Task What_held_still_grows_and_is_only_said_while_they_are_talking()
    {
        var script = new Scripted("Name one", "Name one kind of", "Name one kind of bird.");
        await using var live = new LiveHearing(script, bytesPerUpdate: Block);
        var still = new BlockingCollection<string>();
        live.HeldStill += still.Add;

        live.Hear(Audio(), TimeSpan.Zero);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref script.Updates) == 1, Wait));
        live.Hear(Audio(), TimeSpan.Zero);
        Assert.True(still.TryTake(out var first, Wait));
        Assert.Equal("Name one kind", first);

        // The update on the quiet is the one somebody is about to act on: it starts nothing else.
        live.Hear(Audio(), LiveHearing.SettledAfter);
        await live.FinishAsync().WaitAsync(Wait);
        Assert.Equal(0, still.Count);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("You", "")]
    [InlineData("Thank you.", "")]
    [InlineData("Name one.", "")]
    [InlineData("Why is this?", "Why is")]
    [InlineData("Name one kind of bird.", "Name one kind of")]
    [InlineData("Well, yes, I think so", "Well, yes, I think")]
    [InlineData("Is it, really", "Is it")]
    public void What_is_still_leaves_off_the_word_being_said_and_what_ended_the_one_before(string words, string still) =>
        Assert.Equal(still, LiveHearing.Still(words));
}
