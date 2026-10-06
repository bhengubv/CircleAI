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
    /// in order. Content only — reasoning is filtered out. Use
    /// <see cref="StreamFragmentsAsync"/> when you need both streams tagged.
    /// </summary>
    IAsyncEnumerable<string> StreamCompletionAsync(InferenceRequest request, CancellationToken ct = default);

    /// <summary>
    /// Streams tokens tagged with their kind (content vs reasoning) so the
    /// caller can route each fragment into the appropriate OpenAI delta field
    /// (<c>content</c> or <c>reasoning_content</c>). Default implementation
    /// wraps <see cref="StreamCompletionAsync"/> and tags every chunk as
    /// <see cref="InferenceFragmentKind.Content"/>.
    /// </summary>
    async IAsyncEnumerable<InferenceFragment> StreamFragmentsAsync(
        InferenceRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var chunk in StreamCompletionAsync(request, ct).ConfigureAwait(false))
            yield return new InferenceFragment(InferenceFragmentKind.Content, chunk);
    }

    /// <summary>
    /// Returns the bridge's view of the hardware it is running on.
    /// </summary>
    Task<DeviceCapabilities> GetDeviceCapabilitiesAsync(CancellationToken ct = default);
}

/// <summary>Kind of fragment a streaming bridge emits.</summary>
public enum InferenceFragmentKind
{
    /// <summary>Part of the user-facing answer (goes into OpenAI <c>content</c>).</summary>
    Content   = 0,
    /// <summary>Part of the model's reasoning trace (goes into OpenAI <c>reasoning_content</c>).</summary>
    Reasoning = 1,
}

/// <summary>A single fragment emitted by <see cref="IInferenceBridge.StreamFragmentsAsync"/>.</summary>
/// <param name="Kind">Which sink this fragment belongs to.</param>
/// <param name="Text">The decoded fragment text.</param>
public readonly record struct InferenceFragment(InferenceFragmentKind Kind, string Text);
