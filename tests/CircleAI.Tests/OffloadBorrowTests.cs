// OffloadBorrowTests.cs
//
// The borrow decision as it sits in the shared turn: consent gates it, the RAW
// question (never the composed, memory-bearing prompt) is what leaves, a borrowed
// answer replaces the local turn, and a no-borrow falls straight through to the
// local head. All browser-safe — no model, no mesh, no device.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public class OffloadBorrowTests
{
    [Theory]
    [InlineData(false, "B", false)]
    [InlineData(true, null, false)]
    [InlineData(true, "", false)]
    [InlineData(true, "   ", false)]
    [InlineData(true, "B", true)]
    public void CanBorrow_needs_borrowing_on_AND_a_named_node(bool enabled, string? node, bool expected)
        => Assert.Equal(expected, new OffloadConsent(enabled, node).CanBorrow);

    [Fact]
    public void Off_borrows_nothing() => Assert.False(OffloadConsent.Off.CanBorrow);

    private sealed class FakeBrain : IBrain
    {
        public bool AskCalled;
        public string? AskedWith;
        public Func<string, string> Reply = _ => "local answer";

        public Task<BrainState> StateAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> AskAsync(string prompt, Action<string>? token = null, CancellationToken ct = default)
        {
            AskCalled = true;
            AskedWith = prompt;
            var answer = Reply(prompt);
            token?.Invoke(answer);
            return Task.FromResult(answer);
        }
        public Task<string> SeeAsync(string question, byte[] image, Action<string>? token = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> AskWithToolsAsync(string prompt, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public int MaxImageEdge => 0;
    }

    private sealed class FakeGateway : IOffloadGateway
    {
        public string? Answer;   // null = do not borrow
        public string? SawQuestion;
        public Task<string?> TryBorrowAsync(string question, CancellationToken ct = default)
        {
            SawQuestion = question;
            return Task.FromResult(Answer);
        }
    }

    // "capital of Japan?" is neither arithmetic nor a battery/web tool intent, so
    // the turn reaches the borrow step.
    private const string Question = "capital of Japan?";
    private const string Composed = "COMPOSED-PROMPT-WITH-MEMORY-AND-PERSONA";

    [Fact]
    public async Task A_borrowed_answer_replaces_the_turn_and_skips_the_local_brain()
    {
        var brain = new FakeBrain();
        var gw = new FakeGateway { Answer = "Tokyo (borrowed)." };
        var frags = new List<string>();

        var reply = await brain.AskMaybeToolsAsync(Composed, frags.Add, question: Question, offload: gw);

        Assert.Equal("Tokyo (borrowed).", reply.Text);
        Assert.True(reply.UsedTools);
        Assert.False(brain.AskCalled);   // the local head never ran
        Assert.Empty(frags);
        // The privacy guarantee, pinned: only the RAW question left the phone.
        Assert.Equal(Question, gw.SawQuestion);
    }

    [Fact]
    public async Task No_borrow_falls_through_to_the_local_stream()
    {
        var brain = new FakeBrain { Reply = _ => "local answer" };
        var gw = new FakeGateway { Answer = null };
        var frags = new List<string>();

        var reply = await brain.AskMaybeToolsAsync(Composed, frags.Add, question: Question, offload: gw);

        Assert.Equal("local answer", reply.Text);
        Assert.False(reply.UsedTools);
        Assert.True(brain.AskCalled);
        Assert.Equal(Composed, brain.AskedWith);   // the local head still gets the composed prompt
    }

    [Fact]
    public async Task Without_a_gateway_the_turn_is_unchanged()
    {
        var brain = new FakeBrain { Reply = _ => "local" };
        var frags = new List<string>();

        var reply = await brain.AskMaybeToolsAsync(Composed, frags.Add, question: Question);

        Assert.Equal("local", reply.Text);
        Assert.True(brain.AskCalled);
    }
}
