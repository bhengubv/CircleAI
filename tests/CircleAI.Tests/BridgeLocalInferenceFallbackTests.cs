// BridgeLocalInferenceFallbackTests.cs
//
// The serve-side adapter that finally makes CircleAI.Mesh's offload engine
// reachable: a borrowed OffloadTurn, run through the raw inference bridge, comes
// back as an OffloadResult. Pinned here because the mapping is the whole seam —
// a wrong field is a silently wrong answer served to a stranger.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Hosting.InferenceBridge;
using CircleAI.Mesh;
using CircleAI.Mesh.Hosting;
using Xunit;

namespace CircleAI.Tests;

public class BridgeLocalInferenceFallbackTests
{
    /// <summary>A bridge that records the request and returns whatever the test dictates.</summary>
    private sealed class FakeBridge : IInferenceBridge
    {
        public Func<InferenceRequest, InferenceResponse> OnComplete = r =>
            new InferenceResponse(r.Id, r.ModelId, "ok", 1, 1, InferenceStatus.Completed, 1, null, DateTimeOffset.UtcNow);
        public InferenceRequest? Last;

        public Task<InferenceResponse> CompleteAsync(InferenceRequest request, CancellationToken ct = default)
        {
            Last = request;
            return Task.FromResult(OnComplete(request));
        }

        public IAsyncEnumerable<string> StreamCompletionAsync(InferenceRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> IsModelLoadedAsync(string modelId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<ModelDescriptor>> ListLoadedModelsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ModelDescriptor>>([]);
        public Task<DeviceCapabilities> GetDeviceCapabilitiesAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static OffloadTurn Turn(string prompt = "2 + 2?") => OffloadTurn.Create(
        "Qwen3-0.6B-MNN", prompt, maxOutputTokens: 64, temperature: 0.5f, topP: 0.9f,
        stopSequences: new[] { "STOP" });

    [Fact]
    public async Task Maps_every_sampling_knob_across_and_leaks_no_metadata()
    {
        var bridge = new FakeBridge();
        var turn = Turn("what is the capital of Japan?");

        await new BridgeLocalInferenceFallback(bridge).CompleteAsync(turn);

        Assert.NotNull(bridge.Last);
        Assert.Equal(turn.ModelId, bridge.Last!.ModelId);
        Assert.Equal(turn.Prompt, bridge.Last.Prompt);
        Assert.Equal(turn.MaxOutputTokens, bridge.Last.MaxOutputTokens);
        Assert.Equal(turn.Temperature, bridge.Last.Temperature);
        Assert.Equal(turn.TopP, bridge.Last.TopP);
        Assert.Equal(turn.StopSequences, bridge.Last.StopSequences);
        // Nothing of the serving node rides along with a borrowed turn.
        Assert.Empty(bridge.Last.Metadata);
    }

    [Fact]
    public async Task A_completed_response_is_a_local_success_carrying_the_text_and_timing()
    {
        var bridge = new FakeBridge
        {
            OnComplete = r => new InferenceResponse(
                r.Id, r.ModelId, "Tokyo.", 2, 7, InferenceStatus.Completed, 12.5, null,
                DateTimeOffset.UtcNow, "…thinking…"),
        };

        var res = await new BridgeLocalInferenceFallback(bridge).CompleteAsync(Turn());

        Assert.True(res.Success);
        Assert.Equal("Tokyo.", res.OutputText);
        Assert.Equal(OffloadServedBy.LocalFallback, res.ServedBy);
        Assert.Null(res.ServingPeerId);
        Assert.Equal(2, res.OutputTokenCount);
        Assert.Equal(12.5, res.ElapsedMilliseconds);
        Assert.Null(res.FailureReason);
        Assert.Equal("…thinking…", res.ReasoningText);
    }

    [Theory]
    [InlineData(InferenceStatus.StoppedByToken)]
    [InlineData(InferenceStatus.StoppedByLength)]
    public async Task Stop_terminated_responses_still_count_as_produced(InferenceStatus status)
    {
        var bridge = new FakeBridge
        {
            OnComplete = r => new InferenceResponse(
                r.Id, r.ModelId, "partial", 1, 1, status, 1, null, DateTimeOffset.UtcNow),
        };

        var res = await new BridgeLocalInferenceFallback(bridge).CompleteAsync(Turn());

        Assert.True(res.Success);
        Assert.Equal("partial", res.OutputText);
    }

    [Fact]
    public async Task A_failed_response_becomes_a_local_failure_with_the_reason()
    {
        var bridge = new FakeBridge
        {
            OnComplete = r => new InferenceResponse(
                r.Id, r.ModelId, "", 0, 0, InferenceStatus.Failed, 3, "model not loaded",
                DateTimeOffset.UtcNow),
        };

        var res = await new BridgeLocalInferenceFallback(bridge).CompleteAsync(Turn());

        Assert.False(res.Success);
        Assert.Equal(OffloadServedBy.LocalFallback, res.ServedBy);
        Assert.Equal("model not loaded", res.FailureReason);
    }

    [Fact]
    public async Task A_thrown_bridge_becomes_a_failure_not_a_throw()
    {
        var bridge = new FakeBridge { OnComplete = _ => throw new InvalidOperationException("boom") };

        var res = await new BridgeLocalInferenceFallback(bridge).CompleteAsync(Turn());

        Assert.False(res.Success);
        Assert.Equal(OffloadServedBy.LocalFallback, res.ServedBy);
        Assert.Contains("boom", res.FailureReason);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var bridge = new FakeBridge
        {
            OnComplete = _ => throw new OperationCanceledException(),
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new BridgeLocalInferenceFallback(bridge).CompleteAsync(Turn(), new CancellationToken(true)));
    }
}
