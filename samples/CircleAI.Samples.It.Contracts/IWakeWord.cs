// IWakeWord.cs
//
// Hearing "Hey B" without being touched.

namespace CircleAI.Samples.It;

/// <summary>Where the wake listener stands.</summary>
public enum WakeState
{
    /// <summary>Starting up.</summary>
    Preparing,

    /// <summary>
    /// The microphone has not been allowed yet.
    /// </summary>
    /// <remarks>
    /// ITS OWN STATE, not a failure. RECORD_AUDIO is a runtime permission, and
    /// without it AudioRecord does not error - it hands back SILENCE, which looks
    /// exactly like a wake word that does not work.
    /// </remarks>
    NeedsPermission,

    /// <summary>The wake model is not on the device.</summary>
    NotInstalled,

    /// <summary>Microphone open, waiting for the phrase.</summary>
    Listening,

    /// <summary>The phrase was just heard.</summary>
    Heard,

    /// <summary>It could not start.</summary>
    Failed,
}

/// <summary>What the wake screen should say right now.</summary>
/// <param name="State">Which state it is in.</param>
/// <param name="Status">The large line.</param>
/// <param name="Hint">The quiet line under it.</param>
/// <param name="Heard">How many times the phrase has been heard this session.</param>
public sealed record WakeStatus(WakeState State, string Status, string Hint, int Heard = 0);

/// <summary>Listens for the wake phrase.</summary>
/// <remarks>
/// NOTHING IS RECORDED AND NOTHING IS SENT. The microphone feeds a keyword spotter
/// frame by frame and the frames are discarded; the screen says so, and that claim
/// is only worth making because it is what the code does.
/// </remarks>
public interface IWakeWord
{
    /// <summary>
    /// Start listening, reporting each change until cancelled.
    /// </summary>
    /// <remarks>
    /// Long-running: it returns when listening stops. The progress callback is how
    /// the screen follows it, because a wake listener has no natural end.
    /// </remarks>
    Task ListenAsync(IProgress<WakeStatus> updates, CancellationToken ct);

    /// <summary>Ask for the microphone, if this head can.</summary>
    /// <returns>True when it is now granted.</returns>
    Task<bool> RequestMicrophoneAsync();
}
