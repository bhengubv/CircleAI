// IInferenceBridge.cs
//
// The contract every inference daemon must satisfy. Apple Intelligence is
// iOS-only; Gemini Nano is Android-only. This is the cross-OS equivalent:
// one model loaded once per device, shared by every app on the device via
// an OS-specific IPC mechanism (Binder / XPC / named pipe / D-Bus).
//
// This package ships only the contract plus an in-process reference
// implementation (LocalProcessInferenceBridge). OS-specific transport
// adapters live in follow-up packages.

namespace CircleAI.Hosting.InferenceBridge;

/// <summary>
/// Cross-OS contract for an inference daemon. A single process implementing
/// this interface owns one or more loaded models and exposes them to other
/// processes on the same device over an OS-specific IPC channel.
/// </summary>
public interface IInferenceBridge
{
    /// <summary>
    /// Returns a descriptor for every model currently loaded by the bridge.
    /// The list may be empty if the bridge is warming up.
    /// </summary>
    Task<IReadOnlyList<ModelDescriptor>> ListLoadedModelsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns <c>true</c> when a model with <paramref name="modelId"/> is
    /// currently loaded and ready to serve requests.
    /// </summary>
    Task<bool> IsModelLoadedAsync(string modelId, CancellationToken ct = default);

    /// <summary>
    /// Runs a single completion against the configured model and returns the
    /// full response once generation terminates.
    /// </summary>
    Task<InferenceResponse> CompleteAsync(InferenceRequest request, CancellationToken ct = default);

    /// <summary>
    /// Streams tokens (or token chunks) as the model decodes them. Each
    /// yielded string is the next chunk to append; callers concatenate them
    /// in order.
    /// </summary>
    IAsyncEnumerable<string> StreamCompletionAsync(InferenceRequest request, CancellationToken ct = default);

    /// <summary>
    /// Returns the bridge's view of the hardware it is running on.
    /// </summary>
    Task<DeviceCapabilities> GetDeviceCapabilitiesAsync(CancellationToken ct = default);
}
