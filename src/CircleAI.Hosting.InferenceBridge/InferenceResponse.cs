// InferenceResponse.cs
//
// One completion result returned by the bridge. Status communicates how the
// generation terminated; the caller decides whether that status is acceptable.

namespace CircleAI.Hosting.InferenceBridge;

/// <summary>
/// Terminal state of a single inference call.
/// </summary>
public enum InferenceStatus
{
    /// <summary>The model finished generation cleanly (end-of-turn token).</summary>
    Completed,

    /// <summary>Generation halted because a <c>StopSequence</c> matched.</summary>
    StoppedByToken,

    /// <summary>Generation halted because <c>MaxOutputTokens</c> was reached.</summary>
    StoppedByLength,

    /// <summary>The bridge or model failed; see <see cref="InferenceResponse.FailureMessage"/>.</summary>
    Failed,

    /// <summary>The caller cancelled before generation could finish.</summary>
    Cancelled,
}

/// <summary>
/// Result of a single completion call to <see cref="IInferenceBridge"/>.
/// </summary>
/// <param name="RequestId">Identifier copied from the originating <see cref="InferenceRequest"/>.</param>
/// <param name="ModelId">Model that produced the output.</param>
/// <param name="OutputText">The decoded completion text. May be empty on failure.</param>
/// <param name="OutputTokenCount">Number of tokens the model emitted.</param>
/// <param name="PromptTokenCount">Number of prompt tokens the model consumed.</param>
/// <param name="Status">Terminal state of the call.</param>
/// <param name="InferenceMillis">Wall-clock duration of the call in milliseconds.</param>
/// <param name="FailureMessage">
/// Human-readable failure description when <see cref="Status"/> is
/// <see cref="InferenceStatus.Failed"/>; otherwise <c>null</c>.
/// </param>
/// <param name="CompletedAt">UTC timestamp when the call terminated.</param>
public sealed record InferenceResponse(
    Guid RequestId,
    string ModelId,
    string OutputText,
    int OutputTokenCount,
    int PromptTokenCount,
    InferenceStatus Status,
    double InferenceMillis,
    string? FailureMessage,
    DateTimeOffset CompletedAt);
