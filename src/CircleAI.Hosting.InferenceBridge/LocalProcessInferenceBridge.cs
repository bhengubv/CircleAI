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

/// <summary>
/// In-process <see cref="IInferenceBridge"/> implementation. Wraps any
/// <see cref="IChatGenerator"/> and exposes it through the bridge contract.
/// Transport-layer encryption is reported as <c>true</c> because there is
/// no cross-process channel — calls never leave the host process.
/// </summary>
[CircleAIVerificationStatus(VerificationLevel.WireProven,
    Notes = "Wraps any IChatGenerator. Outcome classification + duration metrics + audit emission verified. GetDeviceCapabilitiesAsync returns synthetic values (see [Experimental] attribute on that method).")]
public sealed class LocalProcessInferenceBridge : CircleAIComponentBase, IInferenceBridge
{
    private readonly IChatGenerator _chatGenerator;
    private readonly ModelDescriptor _descriptor;

    /// <inheritdoc />
    public override string ComponentName => "LocalProcessInferenceBridge";

    /// <summary>
    /// Constructs a bridge that forwards every call to
    /// <paramref name="chatGenerator"/> for the model described by
    /// <paramref name="descriptor"/>.
    /// </summary>
    /// <param name="chatGenerator">The in-process chat generator to wrap.</param>
    /// <param name="descriptor">Canonical descriptor for the loaded model.</param>
    /// <exception cref="ArgumentNullException">When any argument is <c>null</c>.</exception>
    public LocalProcessInferenceBridge(IChatGenerator chatGenerator, ModelDescriptor descriptor)
        : base(logger: null)
    {
        ArgumentNullException.ThrowIfNull(chatGenerator);
        ArgumentNullException.ThrowIfNull(descriptor);
        _chatGenerator = chatGenerator;
        _descriptor = descriptor;
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
    /// Currently returns synthetic values — <c>HasGpu</c>/<c>HasNpu</c> are
    /// hardcoded to <c>false</c> regardless of the actual device. Real
    /// platform probes (DXGI/Metal/Vulkan/NNAPI/CoreML/DirectML) are pending.
    /// Treat the GPU/NPU fields as place-holders, not as ground truth.
    /// </remarks>
    [Experimental("CIRCLEAI_DEVCAPS_001",
        UrlFormat = "https://github.com/bhengubv/CircleAI/blob/master/docs/experimental.md#{0}")]
    public Task<DeviceCapabilities> GetDeviceCapabilitiesAsync(CancellationToken ct = default)
    {
        return RunOperationAsync(
            "GetDeviceCapabilitiesAsync",
            () =>
            {
                var osName = DetectOsName();
                var osVersion = Environment.OSVersion.Version.ToString();
                var cores = Environment.ProcessorCount;
                var physicalMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

                var caps = new DeviceCapabilities(
                    OsName: osName,
                    OsVersion: osVersion,
                    PhysicalMemoryBytes: physicalMemory,
                    CpuCoreCount: cores,
                    HasGpu: false,
                    GpuName: null,
                    GpuMemoryBytes: null,
                    HasNpu: false,
                    NpuName: null,
                    HasTransportLayerEncryption: true);

                return Task.FromResult(caps);
            },
            ct);
    }

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

    private static string DetectOsName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "Windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macOS";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Best-effort Android detection: the Android runtime sets this
            // env var and ships a distinctive process path. The JRE/native
            // host fills it in for managed code.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANDROID_ROOT"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANDROID_DATA")))
            {
                return "Android";
            }
            return "Linux";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Create("IOS"))) return "iOS";
        return "Unknown";
    }
}
