// LocalProcessInferenceBridge.cs
//
// Reference implementation of IInferenceBridge that runs the chat generator
// in the caller's own process (no daemon, no IPC). Use this:
//   * in tests where standing up a real daemon is overkill,
//   * in single-app deployments where ship-once / run-everywhere isn't needed,
//   * as the in-process model used by an OS-specific transport adapter
//     (Binder/XPC/named-pipe/D-Bus) which forwards to a LocalProcessInferenceBridge
//     instance running inside the daemon.

namespace CircleAI.Hosting.InferenceBridge;

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CircleAI.Core.Components;
using CircleAI.Core.Diagnostics;
using CircleAI.Core.Validation;
using CircleAI.Inference;
using CircleAI.Runtime.Capabilities;

/// <summary>
/// In-process <see cref="IInferenceBridge"/> implementation. Wraps any
/// <see cref="IChatGenerator"/> and exposes it through the bridge contract.
/// Transport-layer encryption is reported as <c>true</c> because there is
/// no cross-process channel — calls never leave the host process.
/// </summary>
[CircleAIVerificationStatus(VerificationLevel.WireProven,
    Notes = "Wraps any IChatGenerator. Outcome classification + duration metrics + audit emission verified. GetDeviceCapabilitiesAsync now delegates to CircleAI.Runtime.ICapabilityProbe — real values on Windows/Linux/macOS/Android.")]
public sealed class LocalProcessInferenceBridge : CircleAIComponentBase, IInferenceBridge
{
    private readonly IChatGenerator _chatGenerator;
    private readonly ModelDescriptor _descriptor;
    private readonly ICapabilityProbe _capabilityProbe;

    /// <inheritdoc />
    public override string ComponentName => "LocalProcessInferenceBridge";

    /// <summary>
    /// Constructs a bridge that forwards every call to
    /// <paramref name="chatGenerator"/> for the model described by
    /// <paramref name="descriptor"/>. Uses the default
    /// <see cref="CapabilityProbe"/> for device-capability reporting.
    /// </summary>
    /// <param name="chatGenerator">The in-process chat generator to wrap.</param>
    /// <param name="descriptor">Canonical descriptor for the loaded model.</param>
    /// <exception cref="ArgumentNullException">When any argument is <c>null</c>.</exception>
    public LocalProcessInferenceBridge(IChatGenerator chatGenerator, ModelDescriptor descriptor)
        : this(chatGenerator, descriptor, capabilityProbe: null) { }

    /// <summary>
    /// Constructs a bridge with an explicit capability probe — useful in
    /// tests and when hosting on a port (iOS / HarmonyOS) that ships a
    /// platform-specific probe alongside the bridge.
    /// </summary>
    /// <param name="chatGenerator">The in-process chat generator to wrap.</param>
    /// <param name="descriptor">Canonical descriptor for the loaded model.</param>
    /// <param name="capabilityProbe">
    /// Probe to use for <see cref="GetDeviceCapabilitiesAsync"/>. When <c>null</c>
    /// a fresh <see cref="CapabilityProbe"/> is constructed (auto-selects the
    /// running platform).
    /// </param>
    /// <exception cref="ArgumentNullException">When chatGenerator or descriptor are <c>null</c>.</exception>
    public LocalProcessInferenceBridge(
        IChatGenerator chatGenerator,
        ModelDescriptor descriptor,
        ICapabilityProbe? capabilityProbe)
        : base(logger: null)
    {
        ArgumentNullException.ThrowIfNull(chatGenerator);
        ArgumentNullException.ThrowIfNull(descriptor);
        _chatGenerator = chatGenerator;
        _descriptor = descriptor;
        _capabilityProbe = capabilityProbe ?? new CapabilityProbe();
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ModelDescriptor>> ListLoadedModelsAsync(CancellationToken ct = default)
    {
        return RunOperationAsync(
            "ListLoadedModelsAsync",
            () =>
            {
                IReadOnlyList<ModelDescriptor> list = new[] { _descriptor };
                return Task.FromResult(list);
            },
            ct);
    }

    /// <inheritdoc/>
    public Task<bool> IsModelLoadedAsync(string modelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        return RunOperationAsync(
            "IsModelLoadedAsync",
            () => Task.FromResult(string.Equals(_descriptor.ModelId, modelId, StringComparison.Ordinal)),
            ct,
            correlationId: modelId);
    }

    /// <inheritdoc/>
    public Task<InferenceResponse> CompleteAsync(InferenceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return RunOperationAsync(
            "CompleteAsync",
            async () =>
            {
                var response = await CompleteImplAsync(request, ct).ConfigureAwait(false);

                CircleAIDiagnostics.InferenceRequestsTotal.Add(1,
                    new KeyValuePair<string, object?>("bridge", ComponentName),
                    new KeyValuePair<string, object?>("model_id", request.ModelId),
                    new KeyValuePair<string, object?>("outcome", response.Status.ToString().ToLowerInvariant()));

                return response;
            },
            ct,
            correlationId: request.Id.ToString("N"));
    }

    private async Task<InferenceResponse> CompleteImplAsync(InferenceRequest request, CancellationToken ct)
    {
        if (!string.Equals(_descriptor.ModelId, request.ModelId, StringComparison.Ordinal))
        {
            return new InferenceResponse(
                RequestId: request.Id,
                ModelId: request.ModelId,
                OutputText: string.Empty,
                OutputTokenCount: 0,
                PromptTokenCount: 0,
                Status: InferenceStatus.Failed,
                InferenceMillis: 0.0,
                FailureMessage: $"Model '{request.ModelId}' is not loaded by this bridge (have '{_descriptor.ModelId}').",
                CompletedAt: DateTimeOffset.UtcNow);
        }

        var messages = new[] { new ChatMessage("user", request.Prompt) };
        var options = new GenerationOptions
        {
            MaxTokens = request.MaxOutputTokens,
            Temperature = request.Temperature,
            TopP = request.TopP,
            StopSequences = request.StopSequences.Count == 0
                ? null
                : request.StopSequences.ToArray(),
        };

        var sw = Stopwatch.StartNew();
        string output;
        InferenceStatus status;
        string? failureMessage = null;

        try
        {
            output = await _chatGenerator.GenerateAsync(messages, options, ct).ConfigureAwait(false);
            status = DetermineStatus(output, request);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new InferenceResponse(
                RequestId: request.Id,
                ModelId: request.ModelId,
                OutputText: string.Empty,
                OutputTokenCount: 0,
                PromptTokenCount: EstimateTokenCount(request.Prompt),
                Status: InferenceStatus.Cancelled,
                InferenceMillis: sw.Elapsed.TotalMilliseconds,
                FailureMessage: null,
                CompletedAt: DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            sw.Stop();
            output = string.Empty;
            status = InferenceStatus.Failed;
            failureMessage = ex.Message;
        }

        sw.Stop();

        return new InferenceResponse(
            RequestId: request.Id,
            ModelId: request.ModelId,
            OutputText: output,
            OutputTokenCount: EstimateTokenCount(output),
            PromptTokenCount: EstimateTokenCount(request.Prompt),
            Status: status,
            InferenceMillis: sw.Elapsed.TotalMilliseconds,
            FailureMessage: failureMessage,
            CompletedAt: DateTimeOffset.UtcNow);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<string> StreamCompletionAsync(
        InferenceRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return RunStreamAsync<string>(
            "StreamCompletionAsync",
            innerCt => StreamCompletionImplAsync(request, innerCt),
            ct,
            correlationId: request.Id.ToString("N"));
    }

    private async IAsyncEnumerable<string> StreamCompletionImplAsync(
        InferenceRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!string.Equals(_descriptor.ModelId, request.ModelId, StringComparison.Ordinal))
        {
            yield break;
        }

        var messages = new[] { new ChatMessage("user", request.Prompt) };
        var options = new GenerationOptions
        {
            MaxTokens = request.MaxOutputTokens,
            Temperature = request.Temperature,
            TopP = request.TopP,
            StopSequences = request.StopSequences.Count == 0
                ? null
                : request.StopSequences.ToArray(),
        };

        // Defer to the generator's streaming API. If it yields zero chunks we
        // still emit one (the empty string) so callers can rely on at least
        // one element being produced.
        var hasYielded = false;
        await foreach (var chunk in _chatGenerator.StreamAsync(messages, options, ct).ConfigureAwait(false))
        {
            hasYielded = true;
            yield return chunk;
        }

        if (!hasYielded)
        {
            // Fallback: generator streamed nothing. Fall back to the full
            // completion in a single chunk so callers always see ≥ 1 token.
            var full = await _chatGenerator.GenerateAsync(messages, options, ct).ConfigureAwait(false);
            yield return full;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Delegates to the configured <see cref="ICapabilityProbe"/> (default:
    /// <see cref="CapabilityProbe"/>) which reads the host's real
    /// CPU/RAM/GPU/NPU surface. The returned <see cref="DeviceCapabilities"/>
    /// is a projection of the richer <see cref="HostProfile"/> — see
    /// <see cref="HostProfile"/> if you need driver versions or per-core split.
    /// </remarks>
    public Task<DeviceCapabilities> GetDeviceCapabilitiesAsync(CancellationToken ct = default)
    {
        return RunOperationAsync(
            "GetDeviceCapabilitiesAsync",
            async () =>
            {
                var profile = await _capabilityProbe.ProbeAsync(ct).ConfigureAwait(false);
                return ToDeviceCapabilities(profile);
            },
            ct);
    }

    private static DeviceCapabilities ToDeviceCapabilities(HostProfile p) =>
        new(
            OsName: p.Os.ToString(),
            OsVersion: p.OsVersion,
            PhysicalMemoryBytes: p.TotalPhysicalMemoryBytes,
            CpuCoreCount: p.LogicalCoreCount,
            HasGpu: p.Gpu is not null,
            GpuName: p.Gpu?.Model,
            GpuMemoryBytes: p.Gpu?.VramBytes is { } vram and > 0 ? vram : null,
            HasNpu: p.Npu is not null,
            NpuName: p.Npu?.Model,
            HasTransportLayerEncryption: true);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static InferenceStatus DetermineStatus(string output, InferenceRequest request)
    {
        if (request.StopSequences.Count > 0)
        {
            foreach (var s in request.StopSequences)
            {
                if (!string.IsNullOrEmpty(s) && output.Contains(s, StringComparison.Ordinal))
                {
                    return InferenceStatus.StoppedByToken;
                }
            }
        }

        var produced = EstimateTokenCount(output);
        return produced >= request.MaxOutputTokens
            ? InferenceStatus.StoppedByLength
            : InferenceStatus.Completed;
    }

    private static int EstimateTokenCount(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        // Rough heuristic: ~4 chars per BPE token for English. Real bridges
        // use their generator's tokeniser; this is fine for the reference impl.
        return Math.Max(1, text.Length / 4);
    }

    // ── OS-detection helper was deleted (Phase 1) — GetDeviceCapabilitiesAsync
    //     now delegates to ICapabilityProbe whose ArchHelpers.ResolveOsKind()
    //     handles Android/iOS/macOS/Linux/Windows discrimination.
}
