// IVoiceListener.cs
//
// Bridges the voice pipeline with the Companion session.
// When the wake word fires and the user speaks, VoiceCompanionListener
// transcribes → forwards to ICompanionSession → raises ResponseReady
// with the reply. Platform hosts subscribe to these two events and drive
// TTS playback or UI updates accordingly.

namespace Circle.AI.Companion;

/// <summary>
/// Arguments raised when a user utterance has been fully transcribed and
/// forwarded to the Companion session for processing.
/// </summary>
public sealed class UtteranceDetectedEventArgs : EventArgs
{
    /// <summary>Transcribed text of the user's utterance.</summary>
    public required string Text { get; init; }

    /// <summary>Transcription confidence in the range [0.0, 1.0].</summary>
    public float Confidence { get; init; }

    /// <summary>UTC timestamp when the transcription completed.</summary>
    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Arguments raised when the Companion has produced a reply to a voice utterance.
/// The platform host is responsible for synthesising the text into speech
/// (via <c>ITtsEngine</c>) or displaying it in the UI.
/// </summary>
public sealed class ResponseReadyEventArgs : EventArgs
{
    /// <summary>The Companion's reply text.</summary>
    public required string Text { get; init; }

    /// <summary>The utterance that triggered this response.</summary>
    public required string OriginalUtterance { get; init; }

    /// <summary>UTC timestamp when the Companion completed the reply.</summary>
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Bridges a voice pipeline with a <see cref="ICompanionSession"/>: listens for
/// transcribed utterances, forwards them to the session, and raises
/// <see cref="ResponseReady"/> when the Companion's reply is available.
/// </summary>
public interface IVoiceListener : IAsyncDisposable
{
    /// <summary>
    /// Raised when a user utterance has been transcribed and is being forwarded
    /// to the Companion session.
    /// </summary>
    event EventHandler<UtteranceDetectedEventArgs>? UtteranceDetected;

    /// <summary>
    /// Raised when the Companion has produced a reply to a transcribed utterance.
    /// Subscribe to drive TTS playback or render the reply in the UI.
    /// </summary>
    event EventHandler<ResponseReadyEventArgs>? ResponseReady;

    /// <summary>
    /// Begin listening for the wake word. Starts the underlying
    /// <c>VoicePipeline</c>.
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Stop listening and cancel any in-flight activation. Stops the underlying
    /// <c>VoicePipeline</c>.
    /// </summary>
    Task StopAsync(CancellationToken ct = default);
}
