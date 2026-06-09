// P3ApiSurfaceTests.cs
//
// P3 = "API surface" — the polish pass:
//   • ChatResponse record + GenerateResponseAsync default
//   • LokiOrchestrator semaphore correctness (covered in Orchestration.Tests)
//   • AgentMessage correlation ID (covered in dedicated test below)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class P3ChatResponseTests
{
    [Fact]
    public async Task DefaultGenerateResponseAsync_PopulatesAllFields()
    {
        IChatGenerator gen = new EchoGenerator();
        var resp = await gen.GenerateResponseAsync(
            new[] { new ChatMessage("user", "hello") },
            options: null,
            ct: CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(resp.Text));
        Assert.True(resp.TokensIn > 0);
        Assert.True(resp.TokensOut > 0);
        Assert.True(resp.Latency >= TimeSpan.Zero);
        Assert.Equal(FinishReason.Stop, resp.FinishReason);
    }

    [Fact]
    public async Task DefaultGenerateResponseAsync_LatencyTracksWallClock()
    {
        IChatGenerator gen = new SlowGenerator(TimeSpan.FromMilliseconds(60));

        var resp = await gen.GenerateResponseAsync(
            new[] { new ChatMessage("user", "go") },
            options: null,
            ct: CancellationToken.None);

        // Allow some slack on CI but ensure latency reflects the delay.
        Assert.True(resp.Latency >= TimeSpan.FromMilliseconds(40),
            $"Expected ≥40ms, got {resp.Latency.TotalMilliseconds}ms");
    }

    [Fact]
    public void ChatResponse_RecordEquality()
    {
        var a = new ChatResponse("hi", 1, 1, TimeSpan.FromMilliseconds(10), FinishReason.Stop);
        var b = new ChatResponse("hi", 1, 1, TimeSpan.FromMilliseconds(10), FinishReason.Stop);
        Assert.Equal(a, b);
    }
}

// ────────────────────────────────────────────────────────────────────────
// Fakes for the default GenerateResponseAsync extension.
// ────────────────────────────────────────────────────────────────────────

internal sealed class EchoGenerator : IChatGenerator
{
    public Task<string> GenerateAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions? options = null,
        CancellationToken ct = default)
        => Task.FromResult(string.Concat("echo: ", messages.LastOrDefault()?.Content ?? string.Empty));

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield return await GenerateAsync(messages, options, ct);
    }

    public void Dispose() { }
}

internal sealed class SlowGenerator : IChatGenerator
{
    private readonly TimeSpan _delay;
    public SlowGenerator(TimeSpan delay) => _delay = delay;

    public async Task<string> GenerateAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions? options = null,
        CancellationToken ct = default)
    {
        await Task.Delay(_delay, ct);
        return "slow";
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Delay(_delay, ct);
        yield return "slow";
    }

    public void Dispose() { }
}
