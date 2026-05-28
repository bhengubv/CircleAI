// InferenceBridgeTests.cs
//
// End-to-end tests for the CircleAI.Hosting.InferenceBridge contract and
// reference implementations (LocalProcessInferenceBridge, MockInferenceBridge).

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CircleAI.Hosting.InferenceBridge;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Hosting.InferenceBridge.Tests;

// ── InferenceRequest factory ──────────────────────────────────────────────────

public sealed class InferenceRequestTests
{
    [Fact]
    public void Create_StampsFreshIdAndTimestamp()
    {
        var before = DateTimeOffset.UtcNow;
        var req = InferenceRequest.Create("m", "hello");
        var after = DateTimeOffset.UtcNow;

        Assert.NotEqual(Guid.Empty, req.Id);
        Assert.InRange(req.RequestedAt, before.AddSeconds(-1), after.AddSeconds(1));
        Assert.Equal("m", req.ModelId);
        Assert.Equal("hello", req.Prompt);
        Assert.Equal(256, req.MaxOutputTokens);
        Assert.Equal(0.7f, req.Temperature);
        Assert.Equal(0.95f, req.TopP);
        Assert.Empty(req.StopSequences);
        Assert.Empty(req.Metadata);
    }

    [Fact]
    public void Create_TwoCallsProduceDifferentIds()
    {
        var a = InferenceRequest.Create("m", "x");
        var b = InferenceRequest.Create("m", "x");
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void Create_NullPrompt_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => InferenceRequest.Create("m", null!));
    }
}

// ── ModelDescriptor JSON round-trip ───────────────────────────────────────────

public sealed class ModelDescriptorTests
{
    [Fact]
    public void RoundTrip_Json_PreservesAllFields()
    {
        var original = new ModelDescriptor(
            ModelId: "llama-3.1-8b-instruct",
            Version: "1.2.3",
            Format: ModelFormat.Gguf,
            ContextWindowTokens: 131072,
            VocabSize: 128256,
            ParameterCount: 8_030_261_312L,
            QuantisationLabel: "Q4_K_M",
            ApproximateMemoryBytes: 5_000_000_000L);

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<ModelDescriptor>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(original, roundTripped);
        Assert.Equal(original.ModelId, roundTripped!.ModelId);
        Assert.Equal(original.Version, roundTripped.Version);
        Assert.Equal(original.Format, roundTripped.Format);
        Assert.Equal(original.ContextWindowTokens, roundTripped.ContextWindowTokens);
        Assert.Equal(original.VocabSize, roundTripped.VocabSize);
        Assert.Equal(original.ParameterCount, roundTripped.ParameterCount);
        Assert.Equal(original.QuantisationLabel, roundTripped.QuantisationLabel);
        Assert.Equal(original.ApproximateMemoryBytes, roundTripped.ApproximateMemoryBytes);
    }

    [Fact]
    public void RoundTrip_Json_PreservesNullQuantisationLabel()
    {
        var original = new ModelDescriptor("m", "1.0.0", ModelFormat.Onnx, 1024, 32000, 1_000_000, null, 1024);
        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<ModelDescriptor>(json);
        Assert.NotNull(roundTripped);
        Assert.Null(roundTripped!.QuantisationLabel);
    }
}

// ── MockInferenceBridge ───────────────────────────────────────────────────────

public sealed class MockInferenceBridgeTests
{
    [Fact]
    public async Task CompleteAsync_ReturnsCannedOutput()
    {
        var bridge = new MockInferenceBridge("canned reply");
        var req = InferenceRequest.Create(bridge.Descriptor.ModelId, "prompt");

        var resp = await bridge.CompleteAsync(req);

        Assert.Equal("canned reply", resp.OutputText);
        Assert.Equal(InferenceStatus.Completed, resp.Status);
        Assert.Equal(req.Id, resp.RequestId);
        Assert.Null(resp.FailureMessage);
    }

    [Fact]
    public async Task CompleteAsync_RespectsLatencyMillis()
    {
        const int latency = 150;
        var bridge = new MockInferenceBridge("x", latencyMillis: latency);
        var req = InferenceRequest.Create(bridge.Descriptor.ModelId, "prompt");

        var sw = Stopwatch.StartNew();
        var resp = await bridge.CompleteAsync(req);
        sw.Stop();

        // Allow generous tolerance for timer resolution and CI noise.
        Assert.InRange(sw.Elapsed.TotalMilliseconds, latency - 30, latency + 750);
        Assert.True(resp.InferenceMillis >= latency - 30,
            $"reported inference {resp.InferenceMillis} ms should be at least {latency - 30} ms");
    }

    [Fact]
    public async Task CompleteAsync_Cancellation_Throws()
    {
        var bridge = new MockInferenceBridge("x", latencyMillis: 5000);
        var req = InferenceRequest.Create(bridge.Descriptor.ModelId, "prompt");
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(20);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => bridge.CompleteAsync(req, cts.Token));
    }

    [Fact]
    public async Task StreamCompletionAsync_YieldsCannedOutput()
    {
        var bridge = new MockInferenceBridge("stream-out");
        var req = InferenceRequest.Create(bridge.Descriptor.ModelId, "prompt");

        var chunks = new List<string>();
        await foreach (var chunk in bridge.StreamCompletionAsync(req))
        {
            chunks.Add(chunk);
        }

        Assert.Single(chunks);
        Assert.Equal("stream-out", chunks[0]);
    }

    [Fact]
    public async Task IsModelLoadedAsync_TrueForMockModel()
    {
        var bridge = new MockInferenceBridge("x", modelId: "alpha");
        Assert.True(await bridge.IsModelLoadedAsync("alpha"));
        Assert.False(await bridge.IsModelLoadedAsync("other"));
    }
}

// ── LocalProcessInferenceBridge ───────────────────────────────────────────────

public sealed class LocalProcessInferenceBridgeTests
{
    private static readonly ModelDescriptor Descriptor = new(
        ModelId: "test-model",
        Version: "1.0.0",
        Format: ModelFormat.Gguf,
        ContextWindowTokens: 4096,
        VocabSize: 32000,
        ParameterCount: 1_000_000,
        QuantisationLabel: "Q4_K_M",
        ApproximateMemoryBytes: 1024 * 1024);

    [Fact]
    public async Task ListLoadedModelsAsync_ReturnsTheDescriptor()
    {
        using var gen = new FakeChatGenerator("hello");
        var bridge = new LocalProcessInferenceBridge(gen, Descriptor);

        var loaded = await bridge.ListLoadedModelsAsync();

        var single = Assert.Single(loaded);
        Assert.Equal(Descriptor, single);
    }

    [Fact]
    public async Task IsModelLoadedAsync_TrueForConfiguredModelFalseOtherwise()
    {
        using var gen = new FakeChatGenerator("hello");
        var bridge = new LocalProcessInferenceBridge(gen, Descriptor);

        Assert.True(await bridge.IsModelLoadedAsync("test-model"));
        Assert.False(await bridge.IsModelLoadedAsync("other-model"));
    }

    [Fact]
    public async Task CompleteAsync_HappyPath_ReturnsOutput()
    {
        using var gen = new FakeChatGenerator("the reply");
        var bridge = new LocalProcessInferenceBridge(gen, Descriptor);
        var req = InferenceRequest.Create(Descriptor.ModelId, "the prompt");

        var resp = await bridge.CompleteAsync(req);

        Assert.Equal("the reply", resp.OutputText);
        Assert.Equal(req.Id, resp.RequestId);
        Assert.Equal(InferenceStatus.Completed, resp.Status);
        Assert.True(resp.InferenceMillis >= 0);
        Assert.Null(resp.FailureMessage);
    }

    [Fact]
    public async Task CompleteAsync_ModelMismatch_ReportsFailure()
    {
        using var gen = new FakeChatGenerator("never invoked");
        var bridge = new LocalProcessInferenceBridge(gen, Descriptor);
        var req = InferenceRequest.Create("wrong-model", "x");

        var resp = await bridge.CompleteAsync(req);

        Assert.Equal(InferenceStatus.Failed, resp.Status);
        Assert.NotNull(resp.FailureMessage);
        Assert.Contains("wrong-model", resp.FailureMessage);
    }

    [Fact]
    public async Task StreamCompletionAsync_YieldsAtLeastOneToken()
    {
        using var gen = new FakeChatGenerator("tokens", streamChunks: new[] { "to", "ken", "s" });
        var bridge = new LocalProcessInferenceBridge(gen, Descriptor);
        var req = InferenceRequest.Create(Descriptor.ModelId, "p");

        var chunks = new List<string>();
        await foreach (var chunk in bridge.StreamCompletionAsync(req))
        {
            chunks.Add(chunk);
        }

        Assert.NotEmpty(chunks);
        Assert.Equal(new[] { "to", "ken", "s" }, chunks);
    }

    [Fact]
    public async Task StreamCompletionAsync_FallbackWhenGeneratorYieldsNothing()
    {
        using var gen = new FakeChatGenerator("fallback-text", streamChunks: Array.Empty<string>());
        var bridge = new LocalProcessInferenceBridge(gen, Descriptor);
        var req = InferenceRequest.Create(Descriptor.ModelId, "p");

        var chunks = new List<string>();
        await foreach (var chunk in bridge.StreamCompletionAsync(req))
        {
            chunks.Add(chunk);
        }

        var single = Assert.Single(chunks);
        Assert.Equal("fallback-text", single);
    }

    [Fact]
    public async Task GetDeviceCapabilitiesAsync_ReturnsRealOsAndPositiveCoreCount()
    {
        using var gen = new FakeChatGenerator("x");
        var bridge = new LocalProcessInferenceBridge(gen, Descriptor);

        var caps = await bridge.GetDeviceCapabilitiesAsync();

        Assert.False(string.IsNullOrWhiteSpace(caps.OsName));
        Assert.True(caps.CpuCoreCount > 0,
            $"CpuCoreCount should be positive but was {caps.CpuCoreCount}");
        Assert.True(caps.PhysicalMemoryBytes >= 0);
        Assert.True(caps.HasTransportLayerEncryption,
            "In-process bridge should report transport-layer encryption = true");
    }
}

// ── InferenceResponse invariants ──────────────────────────────────────────────

public sealed class InferenceResponseTests
{
    [Fact]
    public void Failed_Status_RequiresFailureMessage()
    {
        var resp = new InferenceResponse(
            RequestId: Guid.NewGuid(),
            ModelId: "m",
            OutputText: string.Empty,
            OutputTokenCount: 0,
            PromptTokenCount: 0,
            Status: InferenceStatus.Failed,
            InferenceMillis: 0,
            FailureMessage: "boom",
            CompletedAt: DateTimeOffset.UtcNow);

        Assert.Equal(InferenceStatus.Failed, resp.Status);
        Assert.NotNull(resp.FailureMessage);
        Assert.False(string.IsNullOrEmpty(resp.FailureMessage));
    }
}

// ── Test doubles ──────────────────────────────────────────────────────────────

/// <summary>
/// Test double that returns a canned completion and optional streamed chunks.
/// </summary>
internal sealed class FakeChatGenerator : IChatGenerator
{
    private readonly string _full;
    private readonly string[]? _streamChunks;

    public FakeChatGenerator(string full, string[]? streamChunks = null)
    {
        _full = full;
        _streamChunks = streamChunks;
    }

    public Task<string> GenerateAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions? options = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_full);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_streamChunks is null)
        {
            // Mimic generators that haven't implemented streaming: yield zero
            // chunks. The bridge must fall back to the full completion.
            await Task.CompletedTask;
            yield break;
        }

        foreach (var chunk in _streamChunks)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return chunk;
        }
    }

    public void Dispose() { }
}
