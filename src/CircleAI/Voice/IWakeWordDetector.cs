namespace CircleAI.Voice;

/// <summary>
/// Detects a configured wake word in a continuous audio stream and raises
/// <see cref="WakeWordDetected"/> when the phrase is recognised.
/// Implementations are expected to manage their own audio capture pipeline
/// (microphone open/close) between <see cref="StartAsync"/> and
/// <see cref="StopAsync"/>.
/// </summary>
public interface IWakeWordDetector : IAsyncDisposable
{
    /// <summary>
    /// The PRIMARY phrase the detector listens for — the default is "Hey B!".
    /// </summary>
    /// <remarks>
    /// This is the first entry of <see cref="WakeWords"/>, kept as its own
    /// member so existing callers and UI labels ("say Hey B!") keep working.
    /// </remarks>
    string WakeWord { get; }

    /// <summary>
    /// Every phrase that may wake the assistant. Speaking any one of them
    /// activates it; speaking anything else does not.
    /// </summary>
    /// <remarks>
    /// This is the ACCESS LIST, not merely a convenience for synonyms. Voice is
    /// an open microphone in a shared room: anyone within earshot can address a
    /// single-phrase assistant. Giving each permitted person their own phrase
    /// means an unlisted phrase is simply not a wake word, so a household or
    /// office can decide who may drive it by voice at all.
    /// <para>
    /// Two consequences worth stating plainly, because a caller will otherwise
    /// assume more than this delivers: phrases are a shared secret, not
    /// biometrics — anyone who overhears one can repeat it, and the detector
    /// cannot tell two speakers apart. Treat it as a door with a knock, not a
    /// lock. Speaker identification, if it is ever wanted, is a separate model
    /// and belongs behind <c>ISpeechModelSelector</c> like every other model.
    /// </para>
    /// <para>
    /// Never empty: an empty list would wake on nothing, which reads as a broken
    /// microphone rather than a configuration mistake. Implementations fall back
    /// to <see cref="WakeWord"/>.
    /// </para>
    /// </remarks>
    IReadOnlyList<string> WakeWords => new[] { WakeWord };

    /// <summary>
    /// True when the detector is actively listening for the wake word.
    /// </summary>
    bool IsListening { get; }

    /// <summary>
    /// Raised when the wake word is detected with sufficient confidence.
    /// Subscribers should treat the event as the trigger to begin command capture.
    /// </summary>
    event EventHandler<WakeWordDetectedEventArgs>? WakeWordDetected;

    /// <summary>
    /// Begin listening for the wake word. Idempotent: calling when already
    /// listening should complete without error.
    /// </summary>
    /// <param name="ct">Cancellation token used to abort startup.</param>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Stop listening and release any audio capture resources held by the
    /// detector. Idempotent: calling when not listening should complete
    /// without error.
    /// </summary>
    /// <param name="ct">Cancellation token used to abort shutdown.</param>
    Task StopAsync(CancellationToken ct = default);
}

/// <summary>
/// Payload describing a single wake-word detection event.
/// </summary>
public sealed class WakeWordDetectedEventArgs : EventArgs
{
    /// <summary>The wake word phrase that was detected.</summary>
    public required string WakeWord { get; init; }

    /// <summary>UTC timestamp at which the detection fired.</summary>
    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Detector-reported confidence in the detection, in the range [0, 1].
    /// Implementations that do not produce a confidence score should report 1.0.
    /// </summary>
    public float Confidence { get; init; }
}
