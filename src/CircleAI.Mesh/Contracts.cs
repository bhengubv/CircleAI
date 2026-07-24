// Contracts.cs
//
// The public data contracts for a single mesh offload turn. Deliberately
// transport-independent and inference-engine-independent: the router speaks
// prompts and completions, not KV tensors or sockets. A host adapts whatever
// local engine it owns (CircleAI.Hosting.InferenceBridge IInferenceBridge, an
// IChatGenerator, a smaller catalogue model, ...) into ILocalInferenceFallback
// and, on the serving side, lets the same seam answer inbound peer requests.

namespace CircleAI.Mesh;

/// <summary>
/// How an offload turn was ultimately served.
/// </summary>
public enum OffloadServedBy
{
    /// <summary>A remote mesh peer ran the model and returned the completion.</summary>
    RemotePeer = 0,

    /// <summary>
    /// No peer served it; the local fallback engine produced the answer -
    /// possibly by downshifting to a smaller model than the turn asked for.
    /// </summary>
    LocalFallback = 1,

    /// <summary>Neither a peer nor the local fallback could produce an answer.</summary>
    None = 2,
}

/// <summary>
/// One completion the caller wants run, plus the sampling knobs. Immutable;
/// create a new instance for retries. <see cref="CorrelationId"/> ties a
/// request to its reply as it crosses the transport.
/// </summary>
/// <param name="ModelId">The model this turn needs, e.g. <c>"Qwen3-1.7B-MNN"</c>.</param>
/// <param name="Prompt">The fully rendered prompt text to complete.</param>
/// <param name="MaxOutputTokens">Hard upper bound on tokens to emit.</param>
/// <param name="Temperature">Sampling temperature. <c>0</c> = greedy.</param>
/// <param name="TopP">Nucleus sampling cutoff. <c>1.0</c> disables.</param>
/// <param name="StopSequences">Substrings that end generation immediately. May be empty.</param>
/// <param name="CorrelationId">Opaque id echoed back on the reply. Unique per turn.</param>
/// <param name="CreatedAtUtc">When the turn was created.</param>
public sealed record OffloadTurn(
    string ModelId,
    string Prompt,
    int MaxOutputTokens,
    float Temperature,
    float TopP,
    IReadOnlyList<string> StopSequences,
    string CorrelationId,
    DateTimeOffset CreatedAtUtc)
{
    /// <summary>
    /// Convenience factory - stamps a fresh <see cref="CorrelationId"/> and
    /// <see cref="CreatedAtUtc"/> and applies sensible sampling defaults.
    /// </summary>
    public static OffloadTurn Create(
        string modelId,
        string prompt,
        int maxOutputTokens = 256,
        float temperature = 0.7f,
        float topP = 0.95f,
        IReadOnlyList<string>? stopSequences = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(prompt);
        return new OffloadTurn(
            modelId,
            prompt,
            maxOutputTokens,
            temperature,
            topP,
            stopSequences ?? Array.Empty<string>(),
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow);
    }
}

/// <summary>
/// The outcome of routing an <see cref="OffloadTurn"/>. Carries the completion
/// text plus the routing metadata a caller needs to reason about what happened
/// (who served it, how long it took, why it fell back).
/// </summary>
/// <param name="Success">True when a usable completion was produced.</param>
/// <param name="OutputText">The decoded completion. Empty on failure.</param>
/// <param name="ServedBy">Which path produced the answer.</param>
/// <param name="ServingPeerId">The peer that served it, when <see cref="ServedBy"/> is <see cref="OffloadServedBy.RemotePeer"/>; otherwise null.</param>
/// <param name="OutputTokenCount">Tokens emitted, when the server reported it; else 0.</param>
/// <param name="ElapsedMilliseconds">Wall-clock time this leg took.</param>
/// <param name="FailureReason">Human-readable reason when <see cref="Success"/> is false; else null.</param>
/// <param name="ReasoningText">Optional chain-of-thought from reasoning models; null when not surfaced.</param>
public sealed record OffloadResult(
    bool Success,
    string OutputText,
    OffloadServedBy ServedBy,
    string? ServingPeerId,
    int OutputTokenCount,
    double ElapsedMilliseconds,
    string? FailureReason,
    string? ReasoningText = null)
{
    /// <summary>Build a failed result with a reason and no output.</summary>
    public static OffloadResult Fail(
        string reason,
        OffloadServedBy servedBy = OffloadServedBy.None,
        double elapsedMilliseconds = 0)
        => new(false, string.Empty, servedBy, null, 0, elapsedMilliseconds, reason);
}
