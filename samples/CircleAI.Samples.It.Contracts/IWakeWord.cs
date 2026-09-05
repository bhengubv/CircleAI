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

/// <summary>What to tell somebody when the phrase was nearly heard.</summary>
/// <remarks>
/// SHARED AND TESTABLE ON PURPOSE. This wording lived inline in the Android
/// head, which is the one place a test cannot reach — and it is the whole point
/// of the near-miss channel. What reaches the person is a sentence, not an
/// event, so the sentence is the thing worth pinning.
/// <para>
/// The split is on WHAT WOULD HELP, not on what happened. A partial match means
/// it can hear you and cannot make out all of the phrase: the useful advice is
/// distance. A refusal means it heard the whole thing and stage two turned it
/// down: the reason IS the advice, and it is already phrased for a person
/// ("had been speaking 1320 ms before the phrase ended" tells you to pause
/// first).
/// </para>
/// </remarks>
public static class NearMissWords
{
    /// <summary>The large line. Always the same: they nearly got there.</summary>
    public const string Status = "Nearly";

    /// <summary>The quiet line under it.</summary>
    /// <param name="matched">Tokens of the phrase that landed.</param>
    /// <param name="total">Tokens the phrase has.</param>
    /// <param name="refused">Why a complete match was turned down, or null.</param>
    public static string Hint(int matched, int total, string? refused)
    {
        if (refused is { Length: > 0 } why) return $"Heard it, but {why}";

        // NO FRACTION WHEN THERE IS NOTHING TO PUT IN IT. A spotter with no
        // registered phrase cannot produce this, but a screen dividing by zero
        // to say so would be a worse bug than the one being reported.
        return total <= 0
            ? "Heard something — a little closer, or a little louder"
            : $"Heard {matched} of {total} — a little closer, or a little louder";
    }
}

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
