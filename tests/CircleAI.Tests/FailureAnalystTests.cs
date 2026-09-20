// FailureAnalystTests.cs
//
// Circle AI's self-healing sense, proven with a FAKE brain — no model, no server, no
// database. That is the point of the capability: it stands on its own, so a stranger
// (or, later, Wolverine) can rely on it. These pin the two things that make it safe:
// it parses a real verdict when the brain gives one, and it degrades to "needs a human"
// — never a throw, never an invented fix — when the brain does not.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Hosting;
using CircleAI.Hosting.SelfHealing;
using CircleAI.Inference;
using CircleAI.Memory;   // FeedbackSignal
using Xunit;

namespace CircleAI.Tests;

public sealed class FailureAnalystTests
{
    private static FailureContext AnError =>
        new("System.Net.Http.HttpRequestException: connection reset",
            Source: "PayfastAPI", Details: "POST /pay 503");

    [Fact]
    public async Task Parses_a_quick_fix_verdict()
    {
        var brain = new FakeBrain(
            "{\"kind\":\"quick-fix\",\"category\":\"transient-network\"," +
            "\"summary\":\"A transient network blip; retrying should clear it.\"," +
            "\"action\":\"retry with backoff\",\"fix\":\"\",\"confidence\":0.85}");
        var analyst = new FailureAnalyst(brain);

        var v = await analyst.AnalyseAsync(AnError);

        Assert.Equal(HealingKind.QuickFix, v.Kind);
        Assert.Equal("transient-network", v.Category);
        Assert.Equal("retry with backoff", v.RecommendedAction);
        Assert.Null(v.DraftFix);                 // empty "fix" becomes null, not ""
        Assert.Equal(0.85, v.Confidence, 3);
        Assert.Equal(1, brain.Calls);
    }

    [Fact]
    public async Task Parses_a_patch_with_a_drafted_fix()
    {
        var brain = new FakeBrain(
            "{\"kind\":\"patch\",\"category\":\"null-reference\"," +
            "\"summary\":\"Unchecked null on the user object.\"," +
            "\"action\":\"guard the null\",\"fix\":\"if (user is null) return;\",\"confidence\":0.6}");

        var v = await new FailureAnalyst(brain).AnalyseAsync(AnError);

        Assert.Equal(HealingKind.Patch, v.Kind);
        Assert.Equal("if (user is null) return;", v.DraftFix);
    }

    [Fact]
    public async Task Reads_json_even_when_the_model_wraps_it_in_prose()
    {
        // Small models pad JSON with chatter; the first {...} block must still be read.
        var brain = new FakeBrain(
            "Sure! Here is my analysis:\n{\"kind\":\"defer\",\"category\":\"business-logic\"," +
            "\"summary\":\"Insufficient funds — not a bug.\",\"confidence\":0.9}\nHope that helps.");

        var v = await new FailureAnalyst(brain).AnalyseAsync(AnError);

        Assert.Equal(HealingKind.Defer, v.Kind);
        Assert.Equal("business-logic", v.Category);
    }

    [Fact]
    public async Task An_unknown_kind_defers_rather_than_guessing()
    {
        var brain = new FakeBrain("{\"kind\":\"restart-the-universe\",\"summary\":\"x\"}");

        var v = await new FailureAnalyst(brain).AnalyseAsync(AnError);

        Assert.Equal(HealingKind.Defer, v.Kind);   // anything unrecognised = the safe side
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Garbled_or_empty_replies_become_needs_a_human_not_a_throw(string reply)
    {
        var v = await new FailureAnalyst(new FakeBrain(reply)).AnalyseAsync(AnError);

        Assert.Equal(HealingKind.Defer, v.Kind);
        Assert.Equal("unclassified", v.Category);
        Assert.Equal(0.0, v.Confidence);
        Assert.Null(v.DraftFix);                    // never invents a fix
    }

    [Fact]
    public async Task An_empty_failure_is_not_even_sent_to_the_brain()
    {
        var brain = new FakeBrain("{\"kind\":\"quick-fix\"}");

        var v = await new FailureAnalyst(brain).AnalyseAsync(new FailureContext("   "));

        Assert.Equal(HealingKind.Defer, v.Kind);
        Assert.Equal(0, brain.Calls);               // no point asking about nothing
    }

    [Fact]
    public async Task A_brain_that_throws_becomes_needs_a_human()
    {
        var brain = new FakeBrain(_ => throw new InvalidOperationException("model not loaded"));

        var v = await new FailureAnalyst(brain).AnalyseAsync(AnError);

        Assert.Equal(HealingKind.Defer, v.Kind);    // a dead brain never loses the error
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var brain = new FakeBrain("{\"kind\":\"quick-fix\"}");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new FailureAnalyst(brain).AnalyseAsync(AnError, cts.Token));
    }

    // ── a brain that only answers ChatAsync ────────────────────────────────────
    private sealed class FakeBrain : IAIService
    {
        private readonly Func<IReadOnlyList<ChatMessage>, string> _reply;
        public int Calls;

        public FakeBrain(string reply) : this(_ => reply) { }
        public FakeBrain(Func<IReadOnlyList<ChatMessage>, string> reply) => _reply = reply;

        public bool IsReady => true;

        public Task<string> ChatAsync(
            IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(_reply(messages));
        }

        // Nothing else is used by the analyst; fail loudly if that changes.
        public Task StartAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> AskAsync(string question, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CircleAI.Tools.ToolResult> InvokeToolAsync(
            CircleAI.Tools.ToolInvocation invocation, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> AgenticChatAsync(
            string prompt, GenerationOptions? options = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SubmitFeedbackAsync(FeedbackSignal signal, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
