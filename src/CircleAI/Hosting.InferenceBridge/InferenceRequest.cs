// InferenceRequest.cs
//
// One inference call addressed to the bridge. Carries every knob the
// underlying generator needs so the bridge can route the request without
// holding any caller state of its own.

namespace CircleAI.Hosting.InferenceBridge;

/// <summary>
/// One completion request submitted to an <see cref="IInferenceBridge"/>.
/// Immutable; create new instances for retries.
/// </summary>
/// <param name="Id">Unique request identifier. Echoed back in the response.</param>
/// <param name="ModelId">Target model. Must be currently loaded in the bridge.</param>
/// <param name="Prompt">The prompt text to complete.</param>
/// <param name="MaxOutputTokens">Hard upper bound on tokens to emit.</param>
/// <param name="Temperature">Sampling temperature. <c>0</c> = greedy.</param>
/// <param name="TopP">Nucleus sampling cutoff. <c>1.0</c> disables.</param>
/// <param name="StopSequences">
/// Substrings that, if produced, end generation immediately. May be empty.
/// </param>
/// <param name="Metadata">
/// Free-form key/value bag for caller bookkeeping (app id, session id, locale).
/// Opaque to the bridge; never returned in the response.
/// </param>
/// <param name="RequestedAt">UTC timestamp the request was created at.</param>
public sealed record InferenceRequest(
    Guid Id,
    string ModelId,
    string Prompt,
    int MaxOutputTokens,
    float Temperature,
    float TopP,
    IReadOnlyList<string> StopSequences,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset RequestedAt)
{
    /// <summary>
    /// Convenience factory that stamps a fresh <see cref="Id"/> and
    /// <see cref="RequestedAt"/> and uses sensible defaults for the
    /// remaining knobs.
    /// </summary>
    /// <param name="modelId">Target model id.</param>
    /// <param name="prompt">Prompt text.</param>
    /// <param name="maxOutputTokens">Hard upper bound on tokens to emit (default 256).</param>
    /// <param name="temperature">Sampling temperature (default 0.7).</param>
    /// <param name="topP">Nucleus sampling cutoff (default 0.95).</param>
    public static InferenceRequest Create(
        string modelId,
        string prompt,
        int maxOutputTokens = 256,
        float temperature = 0.7f,
        float topP = 0.95f)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        ArgumentNullException.ThrowIfNull(prompt);
        return new InferenceRequest(
            Id: Guid.NewGuid(),
            ModelId: modelId,
            Prompt: prompt,
            MaxOutputTokens: maxOutputTokens,
            Temperature: temperature,
            TopP: topP,
            StopSequences: Array.Empty<string>(),
            Metadata: new Dictionary<string, string>(),
            RequestedAt: DateTimeOffset.UtcNow);
    }
}
