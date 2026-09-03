// IResidentAssistant.cs
//
// The app listening when nothing is on screen.
//
// This is the difference between an assistant and a demo you have to open. The
// native head proved the shape: a FOREGROUND service with a persistent
// notification is the only form Android lets hold a microphone once the app is
// away, and the notification is not an apology for holding it — it is the
// honest disclosure that we are.

namespace CircleAI.Samples.It;

/// <summary>Where the resident listener is, in one word.</summary>
public enum ResidentState
{
    /// <summary>Not running. The microphone is free.</summary>
    Off,

    /// <summary>The microphone permission has not been given.</summary>
    NeedsPermission,

    /// <summary>No wake bundle on this device, so there is nothing to listen for.</summary>
    NotInstalled,

    /// <summary>Up, and holding the microphone.</summary>
    Listening,

    /// <summary>It tried and could not. <see cref="ResidentStatus.Hint"/> says what to do.</summary>
    Failed,

    /// <summary>This platform cannot do it at all — see the browser.</summary>
    Unsupported,
}

/// <summary>What the resident listener is doing, in words a person can read.</summary>
public sealed record ResidentStatus(ResidentState State, string Status, string Hint);

/// <summary>Starts and stops the always-on assistant.</summary>
public interface IResidentAssistant
{
    /// <summary>True when the service is up and holding the microphone.</summary>
    bool IsListening { get; }

    /// <summary>
    /// Brings the resident service up and puts it on the microphone.
    /// </summary>
    /// <remarks>
    /// Idempotent, and called from a VISIBLE screen: from Android 12 a
    /// foreground service may not be started from the background at all.
    /// </remarks>
    Task<ResidentStatus> StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Releases the microphone and leaves the models resident.
    /// </summary>
    /// <remarks>
    /// Two separate things, deliberately. Somebody who wants the phone to stop
    /// listening has not asked it to forget everything and reload half a
    /// gigabyte the next time they speak.
    /// </remarks>
    Task<ResidentStatus> StopAsync(CancellationToken ct = default);

    /// <summary>
    /// Start again if — and only if — the owner had it on.
    /// </summary>
    /// <remarks>
    /// THE CONSENT WAS RECORDED AND ONLY ONE PATH EVER READ IT. The flag behind
    /// this is written whenever somebody turns the assistant on or off, and it
    /// was being read by the boot receiver alone. So the service came back after
    /// a REBOOT and never after an ordinary restart - swipe the app away, open
    /// it again, and the setting still read "on" while nothing was listening.
    /// A phone that quietly stops answering to its name, with the control still
    /// ticked, is indistinguishable from a wake word that does not work.
    /// <para>
    /// Call it from a VISIBLE screen: this starts a foreground service, and from
    /// Android 12 that may not be done from the background.
    /// </para>
    /// <para>
    /// Returns <see cref="ResidentState.Off"/> without starting anything when
    /// the owner had it off. It restores a choice; it does not make one.
    /// </para>
    /// </remarks>
    Task<ResidentStatus> ResumeAsync(CancellationToken ct = default);

    /// <summary>Raised when the wake phrase is heard. Not on the UI thread.</summary>
    event EventHandler<string>? Woke;
}
