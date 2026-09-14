// TroubleTests.cs
//
// What a screen says when something breaks.
//
// Four screens printed `{ex.GetType().Name}: {ex.Message}` into a chat bubble -
// a type name and an internal file path, in front of somebody who wanted to know
// whether their phone was broken. The other head summarised and had done since
// it was written, in a sample that is being retired.

using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public class TroubleTests
{
    [Fact]
    public void The_mnn_failure_is_matched_before_the_memory_one()
    {
        // THE ORDERING BUG, AS A TEST. The MNN message contains the word "RAM"
        // AND a full internal file path. Matching "memory" first let that path
        // onto the first screen a person ever sees.
        var ex = new InvalidOperationException(
            "MNN model load failed: insufficient RAM at /data/user/0/pkg/files/x.mnn");

        var said = Trouble.Say(ex);

        Assert.Contains("could not be opened", said, StringComparison.Ordinal);
        Assert.DoesNotContain("/data/", said, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Out of memory", "Closing other apps")]
    [InlineData("OutOfMemoryException raised", "Closing other apps")]
    [InlineData("Permission denied", "blocked")]
    [InlineData("No such file or directory", "still be downloading")]
    [InlineData("config.json not found", "still be downloading")]
    public void A_known_failure_says_the_cause_and_the_fix(string message, string expected)
        => Assert.Contains(expected, Trouble.Say(new InvalidOperationException(message)),
                           StringComparison.Ordinal);

    [Fact]
    public void A_network_failure_is_recognised_by_its_type()
    {
        // The message on these is often a hostname and a port, which tells a
        // person nothing.
        Assert.Contains("internet",
            Trouble.Say(new System.Net.Http.HttpRequestException("nodename nor servname")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrecognised_failure_is_shortened_not_diagnosed()
    {
        // A WRONG DIAGNOSIS IS WORSE THAN AN UNEXPLAINED ONE. It sends somebody
        // to clear storage for a problem that was never about storage. So an
        // unknown message is passed through, trimmed.
        var long_ = new string('x', Trouble.Longest + 100);

        var said = Trouble.Say(new InvalidOperationException(long_));

        Assert.EndsWith("…", said, StringComparison.Ordinal);
        Assert.True(said.Length <= Trouble.Longest + 1);
    }

    [Fact]
    public void It_reads_the_innermost_cause()
    {
        // The outer exception is usually plumbing - "One or more errors
        // occurred" - and the fact is underneath it.
        var ex = new InvalidOperationException("wrapper",
                    new InvalidOperationException("Out of memory"));

        Assert.Contains("Closing other apps", Trouble.Say(ex), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    public void Nothing_at_all_still_says_something(Exception? ex)
        => Assert.False(string.IsNullOrWhiteSpace(Trouble.Say(ex)));

    [Fact]
    public void It_never_leaks_a_type_name()
    {
        // The whole point. `InvalidOperationException:` in a chat bubble is the
        // thing being replaced.
        foreach (var ex in new Exception[]
        {
            new InvalidOperationException("Out of memory"),
            new UnauthorizedAccessException("Permission denied"),
            new System.Net.Http.HttpRequestException("no route"),
        })
            Assert.DoesNotContain("Exception", Trouble.Say(ex), StringComparison.Ordinal);
    }
}
