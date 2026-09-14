// BrainToolFlowTests.cs
//
// The one shared "stream, and escalate to a tool only when the model asks"
// turn, tested against a fake head. Both surfaces - the typed chat screen and
// the spoken turn - call BrainToolFlow.AskMaybeToolsAsync, so what it does here
// is what both of them do. The bug it closes: the typed screen streamed the raw
// generator straight to the bubble and never noticed a <tool_call>, so the
// calculator (and every tool) could never run from text.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public class BrainToolFlowTests
{
    // A head whose stream is a fixed list of fragments, and whose tool pass
    // returns a fixed string. Counts each call so a test can prove the executor
    // ran (or did not).
    private sealed class FakeBrain : IBrain
    {
        private readonly string[] _fragments;
        private readonly string _tooled;

        public int AskCalls { get; private set; }
        public int ToolCalls { get; private set; }

        public FakeBrain(string[] fragments, string tooled = "")
        {
            _fragments = fragments;
            _tooled = tooled;
        }

        public Task<BrainState> StateAsync(CancellationToken ct = default)
            => Task.FromResult(new BrainState(true, ""));

        public Task<string> AskAsync(
            string prompt, Action<string>? token = null, CancellationToken ct = default)
        {
            AskCalls++;
            var sb = new StringBuilder();
            foreach (var f in _fragments)
            {
                token?.Invoke(f);
                sb.Append(f);
            }
            return Task.FromResult(sb.ToString());
        }

        public Task<string> SeeAsync(
            string question, byte[] image, Action<string>? token = null, CancellationToken ct = default)
            => Task.FromResult("");

        public Task<string> AskWithToolsAsync(string prompt, CancellationToken ct = default)
        {
            ToolCalls++;
            return Task.FromResult(_tooled);
        }

        /// <summary>The RAW question the engine routed to the tool path, or null.</summary>
        public string? ToolQuestion { get; private set; }

        public Task<string> AskWithToolsAsync(string prompt, string? question, CancellationToken ct = default)
        {
            ToolQuestion = question;
            ToolCalls++;
            return Task.FromResult(_tooled);
        }

        public int MaxImageEdge => 0;
    }

    private const string ACall =
        "<tool_call>{\"name\": \"calculate\", \"arguments\": {\"expression\": \"12 * 8\"}}</tool_call>";

    [Fact]
    public async Task NoToolCall_streams_and_returns_the_streamed_text()
    {
        var brain = new FakeBrain(new[] { "Ninety", "-six." }, tooled: "(should not be used)");
        var shown = new List<string>();
        var toolStarted = false;

        var reply = await brain.AskMaybeToolsAsync(
            "what is 12 times 8",
            shown.Add,
            onToolStarted: () => toolStarted = true);

        Assert.False(reply.UsedTools);
        Assert.Equal("Ninety-six.", reply.Text);          // the streamed text, whole
        Assert.Equal(new[] { "Ninety", "-six." }, shown); // every fragment shown
        Assert.False(toolStarted);                        // no tool banner
        Assert.Equal(0, brain.ToolCalls);                 // executor never ran
    }

    [Fact]
    public async Task ToolCall_at_the_head_shows_nothing_and_runs_the_executor()
    {
        var brain = new FakeBrain(new[] { ACall }, tooled: "96");
        var shown = new List<string>();
        var toolStarted = 0;

        var reply = await brain.AskMaybeToolsAsync(
            "what is 12 times 8", shown.Add, onToolStarted: () => toolStarted++);

        Assert.True(reply.UsedTools);
        Assert.Equal("96", reply.Text);        // the tool-grounded answer, not the JSON
        Assert.Empty(shown);                   // the call itself was never shown
        Assert.Equal(1, toolStarted);          // banner fired exactly once
        Assert.Equal(1, brain.ToolCalls);      // executor ran
    }

    [Fact]
    public async Task Text_before_a_tool_call_is_shown_then_the_call_is_suppressed()
    {
        var brain = new FakeBrain(new[] { "Let me check. ", ACall }, tooled: "96");
        var shown = new List<string>();

        var reply = await brain.AskMaybeToolsAsync("...", shown.Add);

        Assert.True(reply.UsedTools);
        Assert.Equal("96", reply.Text);
        Assert.Equal(new[] { "Let me check. " }, shown); // only the pre-call words
    }

    [Fact]
    public async Task A_head_with_no_tools_keeps_what_streamed_rather_than_blanking()
    {
        // The executor returns empty (the interface documents this as "no tools
        // wired"). The turn must not be replaced with nothing.
        var brain = new FakeBrain(new[] { "Let me check. ", ACall }, tooled: "");
        var shown = new List<string>();

        var reply = await brain.AskMaybeToolsAsync("...", shown.Add);

        Assert.False(reply.UsedTools);              // nothing to replace with
        Assert.Equal("Let me check. ", reply.Text); // fall back to the streamed text
        Assert.Equal(1, brain.ToolCalls);           // it did try
    }

    [Fact]
    public async Task Null_brain_or_fragment_sink_is_rejected()
    {
        var brain = new FakeBrain(Array.Empty<string>());
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            BrainToolFlow.AskMaybeToolsAsync(null!, "q", _ => { }));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            brain.AskMaybeToolsAsync("q", null!));
    }

    [Fact]
    public async Task A_plain_sum_is_answered_by_the_engine_without_the_model()
    {
        // The head would stream and would run a tool, but neither should happen:
        // a plain sum is worked out deterministically and the model is not asked.
        var brain = new FakeBrain(new[] { "the model should never run" }, tooled: "nor the tool");
        var shown = new List<string>();

        var reply = await brain.AskMaybeToolsAsync(
            "composed prompt with memory facts", shown.Add, question: "what is 347 times 89");

        Assert.True(reply.UsedTools);
        Assert.Equal("30883", reply.Text);   // exact, where the 0.6B answered "30621"
        Assert.Empty(shown);                  // nothing streamed
        Assert.Equal(0, brain.AskCalls);      // the model was never consulted
        Assert.Equal(0, brain.ToolCalls);
    }

    [Fact]
    public async Task A_question_that_is_not_a_sum_still_streams_from_the_model()
    {
        var brain = new FakeBrain(new[] { "A ", "joke." });
        var shown = new List<string>();

        var reply = await brain.AskMaybeToolsAsync(
            "composed", shown.Add, question: "tell me a joke");

        Assert.False(reply.UsedTools);
        Assert.Equal("A joke.", reply.Text);
        Assert.Equal(1, brain.AskCalls);      // the model ran, as before
    }

    [Fact]
    public async Task A_battery_question_runs_the_tool_and_never_streams()
    {
        // The engine recognises the intent (ToolIntent) and hands the RAW question
        // to the executor, which seeds get_battery_level — the 0.6B is not asked to
        // emit a call it will not emit.
        var brain = new FakeBrain(new[] { "should never stream" }, tooled: "Your battery is at 82%.");
        var shown = new List<string>();
        var toolStarted = 0;

        var reply = await brain.AskMaybeToolsAsync(
            "composed prompt", shown.Add,
            onToolStarted: () => toolStarted++, question: "what's my battery");

        Assert.True(reply.UsedTools);
        Assert.Equal("Your battery is at 82%.", reply.Text);
        Assert.Empty(shown);                          // nothing streamed
        Assert.Equal(0, brain.AskCalls);              // the raw generator never ran
        Assert.Equal(1, brain.ToolCalls);             // the executor did
        Assert.Equal("what's my battery", brain.ToolQuestion);  // the RAW question, not the composed prompt
        Assert.Equal(1, toolStarted);
    }

    [Fact]
    public async Task A_weather_question_routes_to_the_tool()
    {
        var brain = new FakeBrain(new[] { "x" }, tooled: "It's 24 degrees in Durban.");
        var reply = await brain.AskMaybeToolsAsync(
            "composed", _ => { }, question: "what's the weather like today");

        Assert.True(reply.UsedTools);
        Assert.Equal("It's 24 degrees in Durban.", reply.Text);
        Assert.Equal(1, brain.ToolCalls);
    }

    [Fact]
    public async Task A_tool_question_on_a_head_with_no_tools_falls_back_to_streaming()
    {
        // The executor returns empty (a browser has no tools). Rather than answer a
        // battery question with nothing, stream the model as normal.
        var brain = new FakeBrain(new[] { "A ", "joke." }, tooled: "");
        var shown = new List<string>();

        var reply = await brain.AskMaybeToolsAsync(
            "composed", shown.Add, question: "what's my battery");

        Assert.False(reply.UsedTools);
        Assert.Equal("A joke.", reply.Text);
        Assert.Equal(1, brain.AskCalls);   // fell through to streaming
    }
}
