// MockInferenceBridge.cs
//
// Deterministic test double for IInferenceBridge. Returns a fixed string for
// every call, optionally simulating a latency budget. Useful in tests that
// exercise calling code without spinning up a real chat generator.

namespace Circle.AI.Hosting.InferenceBridge;

using System.Diagnostics;
using System.Runtime.CompilerServices;

/// <summary>
/// Deterministic <see cref="IInferenceBridge"/> for tests. Returns the same
/// canned output for every call and reports a single fixed-mock model as
/// loaded.
/// </summary>
public sealed class MockInferenceBridge : IInferenceBridge
{
    private readonly string _cannedOutput;
    private readonly int _latencyMillis;
    private readonly ModelDescriptor _descriptor;

    /// <summary>
    /// Constructs a mock bridge that always returns <paramref name="cannedOutput"/>.
    /// </summary>
    /// <param name="cannedOutput">The fixed response text to return.</param>
    /// <param name="latencyMillis">
    /// Simulated wall-clock delay for <see cref="CompleteAsync"/>. Defaults to
    /// 0 (no delay). Useful for verifying that calling code respects timeouts.
    /// </param>
    /// <param name="modelId">
    /// Model id reported as loaded. Defaults to <c>"mock-model"</c>.
    /// </param>
    public MockInferenceBridge(string cannedOutput, int latencyMillis = 0, string modelId = "mock-model")
    {
        ArgumentNullException.ThrowIfNull(cannedOutput);
        if (latencyMillis < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(latencyMillis), "latencyMillis must be non-negative.");
        }
        _cannedOutput = cannedOutput;
        _latencyMillis = latencyMillis;
        _descriptor = new ModelDescriptor(
            ModelId: modelId,
            Version: "mock-1.0.0",
            Format: ModelFormat.Unknown,
            ContextWindowTokens: 4096,
            VocabSize: 32000,
            ParameterCount: 0,
            QuantisationLabel: null,
            ApproximateMemoryBytes: 0);
    }

    /// <summary>The model descriptor this mock reports as loaded.</summary>
    public ModelDescriptor Descriptor => _descriptor;

    /// <inheritdoc/>
    public Task<IReadOnlyList<ModelDescriptor>> ListLoadedModelsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<ModelDescriptor> list = new[] { _descriptor };
        return Task.FromResult(list);
    }

    /// <inheritdoc/>
    public Task<bool> IsModelLoadedAsync(string modelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(string.Equals(_descriptor.ModelId, modelId, StringComparison.Ordinal));
    }

    /// <inheritdoc/>
    public async Task<InferenceResponse> CompleteAsync(InferenceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sw = Stopwatch.StartNew();
        if (_latencyMillis > 0)
        {
            await Task.Delay(_latencyMillis, ct).ConfigureAwait(false);
        }
        else
        {
            ct.ThrowIfCancellationRequested();
        }
        sw.Stop();

        return new InferenceResponse(
            RequestId: request.Id,
            ModelId: _descriptor.ModelId,
            OutputText: _cannedOutput,
            OutputTokenCount: Math.Max(0, _cannedOutput.Length / 4),
            PromptTokenCount: Math.Max(0, request.Prompt.Length / 4),
            Status: InferenceStatus.Completed,
            InferenceMillis: sw.Elapsed.TotalMilliseconds,
            FailureMessage: null,
            CompletedAt: DateTimeOffset.UtcNow);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        InferenceRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_latencyMillis > 0)
        {
            await Task.Delay(_latencyMillis, ct).ConfigureAwait(false);
        }
        else
        {
            ct.ThrowIfCancellationRequested();
        }

        yield return _cannedOutput;
    }

    /// <inheritdoc/>
    public Task<DeviceCapabilities> GetDeviceCapabilitiesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var caps = new DeviceCapabilities(
            OsName: "Mock",
            OsVersion: "1.0",
            PhysicalMemoryBytes: 4L * 1024 * 1024 * 1024,
            CpuCoreCount: 1,
            HasGpu: false,
            GpuName: null,
            GpuMemoryBytes: null,
            HasNpu: false,
            NpuName: null,
            HasTransportLayerEncryption: true);
        return Task.FromResult(caps);
    }
}
