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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CircleAI.Inference;

/// <summary>
/// In-process <see cref="IInferenceBridge"/> implementation. Wraps any
/// <see cref="IChatGenerator"/> and exposes it through the bridge contract.
/// Transport-layer encryption is reported as <c>true</c> because there is
/// no cross-process channel — calls never leave the host process.
/// </summary>
public sealed class LocalProcessInferenceBridge : IInferenceBridge
{
    private readonly IChatGenerator _chatGenerator;
    private readonly ModelDescriptor _descriptor;

    /// <summary>
    /// Constructs a bridge that forwards every call to
    /// <paramref name="chatGenerator"/> for the model described by
    /// <paramref name="descriptor"/>.
    /// </summary>
    /// <param name="chatGenerator">The in-process chat generator to wrap.</param>
    /// <param name="descriptor">Canonical descriptor for the loaded model.</param>
    /// <exception cref="ArgumentNullException">When any argument is <c>null</c>.</exception>
    public LocalProcessInferenceBridge(IChatGenerator chatGenerator, ModelDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(chatGenerator);
        ArgumentNullException.ThrowIfNull(descriptor);
        _chatGenerator = chatGenerator;
        _descriptor = descriptor;
    }

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
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        InferenceRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

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
    public Task<DeviceCapabilities> GetDeviceCapabilitiesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

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
