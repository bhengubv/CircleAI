// MalformedToolCallTests.cs
//
// A constrained retry on a malformed tool call (S6), where the request is still in hand: the
// real agentic loop sees a reply that looks like a call and reads as nothing, asks again with the
// template's form spelled out, runs the call the retry produced - and Wolverine hears that it
// happened and that the retry worked.

using System.Runtime.CompilerServices;
using CircleAI.Core.Logging;
using CircleAI.Hosting;
using CircleAI.Hosting.SelfHealing;
using CircleAI.Inference;
using CircleAI.Tools;
using Xunit;

namespace CircleAI.Tests;

[Collection(nameof(WolverineCollection))]
public sealed class MalformedToolCallTests
{
    private sealed class Scripted(params string[] replies) : IChatGenerator
    {
        private int _next;

        public List<IReadOnlyList<ChatMessage>> Seen { get; } = [];

        public Task<string> GenerateAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null, CancellationToken ct = default)
        {
            Seen.Add(messages.ToList());
            return Task.FromResult(replies[Math.Min(_next++, replies.Length - 1)]);
        }

        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return replies[Math.Min(_next++, replies.Length - 1)];
        }

        public void Dispose()
        {
        }
    }

    private static async Task<(string Answer, FakeToolBridge Bridge, List<HealthEvent> Seen, Scripted Gen)> RunAsync(params string[] replies)
    {
        var seen = new List<HealthEvent>();
        void Take(HealthEvent e)
        {
            if (e.ErrorType == "MalformedToolCall") lock (seen) seen.Add(e);
        }

        HealthEvents.Raised += Take;
        var model = Path.GetTempFileName();
        try
        {
            var bridge = new FakeToolBridge(new ToolResult { ToolName = "tgn.test.ping", Success = true, Result = "pong" });
            var gen = new Scripted(replies);
            await using var service = new AIService(
                new AIOptions { ModelPath = model, WarmOnStart = false, AgenticMaxIterations = 4, ToolBridge = bridge },
                generatorFactory: _ => gen);
            var answer = await service.AgenticChatAsync("ping it");
            return (answer, bridge, seen, gen);
        }
        finally
        {
            HealthEvents.Raised -= Take;
            File.Delete(model);
        }
    }

    [Fact]
    public async Task A_call_nobody_could_read_is_asked_for_again_in_its_form_and_wolverine_hears_the_retry_read()
    {
        var (answer, bridge, seen, gen) = await RunAsync(
            "<tool_call>{not json at all}</tool_call>",
            "<tool_call>{\"name\":\"tgn.test.ping\",\"arguments\":{}}</tool_call>",
            "pong it is");

        Assert.Equal("pong it is", answer);
        Assert.Equal(1, bridge.InvokeCallCount);
        Assert.Contains(gen.Seen[1], m => m.Role == "tool" && m.Content!.Contains("<function=NAME>"));   // the form, spelled out
        var e = Assert.Single(seen);
        Assert.Equal(Remedies.ToolCallRetry, e.Remedy);
        Assert.True(e.Worked);
        Assert.Equal("CircleAI:MalformedToolCall:AIService.AgenticChatAsync", e.Fingerprint);
    }

    [Fact]
    public async Task A_retry_that_still_cannot_be_read_ends_the_turn_and_says_so()
    {
        var (answer, bridge, seen, _) = await RunAsync(
            "<tool_call>{not json}</tool_call>",
            "<tool_call>{still not json}</tool_call>");

        Assert.Equal("<tool_call>{still not json}</tool_call>", answer);
        Assert.Equal(0, bridge.InvokeCallCount);
        var e = Assert.Single(seen);
        Assert.False(e.Worked);
    }

    [Fact]
    public async Task A_plain_answer_is_no_tool_call_and_nothing_is_retried()
    {
        var (answer, _, seen, gen) = await RunAsync("Just an answer.");

        Assert.Equal("Just an answer.", answer);
        Assert.Single(gen.Seen);
        Assert.Empty(seen);
    }
}
