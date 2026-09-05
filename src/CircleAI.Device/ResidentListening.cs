#nullable enable

// ResidentListening.cs
//
// Lets the resident service hold the microphone, so "always listening" means what
// it says instead of "listening while you are looking at the app".
//
// THE HONEST DESCRIPTION OF WHAT IT WAS. WakeWordActivity opens the microphone in
// OnResume and closes it in OnPause. Put the phone in your pocket and the wake
// word is gone; lock the screen and it is gone. Every measurement taken so far
// was of a thing you had to be staring at, which is not a wake word — it is a
// button you say instead of press.
//
// WHY A SEAM AND NOT A REFERENCE. CircleAI.Device deliberately does not depend on
// CircleAI.Voice: that would drag ONNX Runtime and the whole speech stack into
// every Android head, including the chat-only build that has no microphone
// permission and wants none. So the service holds this small interface and the
// head — which already has the speech stack when it is built with voice — plugs
// the real detector in. The dependency points the way it should.
//
// THE MICROPHONE IS A PROMISE, so the notification says so in words while it is
// held. A resident process quietly holding a microphone with a notification that
// says "running" is exactly the behaviour people are right to be afraid of.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Device;

/// <summary>Something that can listen for a wake word for as long as it is asked to.</summary>
/// <remarks>
/// Deliberately smaller than IWakeWordDetector. The service does not need to know
/// about phrases, confidence or audio formats — it needs to start it, stop it,
/// and be told when it fired.
/// </remarks>
/// <summary>How close the listener came to waking, without waking.</summary>
/// <param name="Phrase">The phrase it was tracking.</param>
/// <param name="Matched">How many of the phrase's tokens landed.</param>
/// <param name="Total">How many tokens the phrase has.</param>
/// <param name="Score">What that match scored.</param>
/// <param name="Refused">
/// Why a COMPLETE match was turned down, or null when the phrase never
/// completed. Two different things to tell somebody: come closer, or pause
/// first.
/// </param>
/// <remarks>
/// DECLARED HERE RATHER THAN REUSED FROM CircleAI.Voice, for the same reason
/// <see cref="IResidentListener"/> is smaller than IWakeWordDetector: this
/// assembly must stay free of the speech stack so a chat-only build does not
/// drag ONNX Runtime in behind it. The adapter that owns both does the mapping.
/// </remarks>
public sealed record ResidentNearMiss(
    string Phrase, int Matched, int Total, double Score, string? Refused = null);

public interface IResidentListener : IAsyncDisposable
{
    /// <summary>True while the microphone is open.</summary>
    bool IsListening { get; }

    /// <summary>What it is listening for, for the notification.</summary>
    string Describe { get; }

    /// <summary>Raised with the phrase heard.</summary>
    event EventHandler<string>? Woke;

    /// <summary>Raised when the phrase was nearly heard, and was not.</summary>
    /// <remarks>
    /// THE OTHER HALF OF <see cref="Woke"/>, and the reason it is on this
    /// interface despite the interface being deliberately small. Without it, a
    /// screen watching the resident listener cannot distinguish a dead
    /// microphone from somebody standing slightly too far away - both are the
    /// absence of Woke. Measured on a P30 on 2026-09-06: the log knew the phrase
    /// had reached one token of eight and the screen said "Listening".
    /// <para>
    /// A listener that cannot tell simply never raises it, which is the honest
    /// answer and costs its implementer one line.
    /// </para>
    /// </remarks>
    event EventHandler<ResidentNearMiss>? Nearly;

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}

public sealed partial class CircleNeuronService
{
    private static IResidentListener? _listener;

    /// <summary>
    /// The wake-word listener the service should hold, supplied by the app.
    /// </summary>
    /// <remarks>
    /// Set this BEFORE starting the service. It is a property rather than a
    /// constructor argument because Android constructs services itself — there is
    /// no place to hand one in.
    /// </remarks>
    public static IResidentListener? Listener
    {
        get => _listener;
        set
        {
            var old = _listener;
            _listener = value;
            if (!ReferenceEquals(old, value) && old is not null)
                _ = SafeStopAsync(old);
        }
    }

    /// <summary>Raised when the resident listener hears its wake phrase.</summary>
    /// <remarks>
    /// Static because the thing that wants to know — the app — cannot hold a
    /// reference to a Service instance Android owns and may recreate.
    /// </remarks>
    public static event EventHandler<string>? Woke;

    /// <summary>Raised when the resident listener nearly heard its phrase.</summary>
    /// <inheritdoc cref="IResidentListener.Nearly" path="/remarks"/>
    public static event EventHandler<ResidentNearMiss>? Nearly;

    /// <summary>True when the service currently holds the microphone.</summary>
    public static bool IsListening => _listener?.IsListening == true;

    /// <summary>
    /// Holds the CPU awake for exactly as long as the microphone is open.
    /// </summary>
    /// <remarks>
    /// WITHOUT THIS, "ALWAYS LISTENING" LASTS UNTIL THE SCREEN GOES OFF.
    ///
    /// A foreground service keeps the PROCESS from being killed. It does not keep
    /// the CPU from suspending. With the screen off and no wake lock, the
    /// AudioRecord read loop simply stops being scheduled: the service record is
    /// still there, the notification is still there, IsListening still says true,
    /// and no audio is read at all. Nothing reports an error, because nothing
    /// failed - the thread was just never run again.
    ///
    /// Measured on a P30 on 2026-09-05. The heartbeat prints every five seconds
    /// while the loop is alive; after the app left the foreground it stopped
    /// entirely, the process fell from 711 MB to 47 MB as the models were
    /// reclaimed, and EMUI logged HibStrategySwapCandidateProcessAdd against the
    /// package. Somebody then spoke to the phone for eleven minutes and it heard
    /// a quiet room, because there was nothing running to hear them.
    ///
    /// PARTIAL, so it holds the CPU and NOT the screen - the whole point is to
    /// answer with the screen dark. It is acquired when the microphone opens and
    /// released when it closes, so it is exactly as long-lived as the promise the
    /// notification is making. A wake lock held longer than the microphone would
    /// be a battery leak; held shorter, it is this bug.
    ///
    /// Not sufficient on its own for every vendor - Huawei, Xiaomi, Oppo and Vivo
    /// each add their own killing - which is what ISetup.AllowBackgroundAsync is
    /// for. It is necessary on all of them.
    /// </remarks>
    private static global::Android.OS.PowerManager.WakeLock? _awake;

    private const string WakeLockTag = "CircleAI:resident-listening";

    private static void HoldTheCpu()
    {
        if (_awake is { IsHeld: true }) return;

        try
        {
            var context = global::Android.App.Application.Context;
            var power = (global::Android.OS.PowerManager?)context.GetSystemService(
                global::Android.Content.Context.PowerService);
            if (power is null) return;

            _awake = power.NewWakeLock(
                global::Android.OS.WakeLockFlags.Partial, WakeLockTag);

            // NO TIMEOUT. A wake lock that expires on a timer would make the
            // wake word work for an hour and then not, which is worse than not
            // working at all - it is the failure that reads as "sometimes it
            // does not hear me". Release is tied to the microphone closing.
            _awake?.SetReferenceCounted(false);
            _awake?.Acquire();

            // AND SAY WHETHER THE OTHER HALF IS IN PLACE. The lock stops the CPU
            // suspending; it does not stop a vendor deciding to hibernate the
            // package anyway, which is what EMUI did here. That is what the
            // battery-optimisation exemption is for - and it is only ever asked
            // for during SETUP, so a phone set up before the question existed,
            // or one where somebody said no, has no exemption and no way back to
            // it. Printing it here means the next time listening dies we can
            // tell "the lock did not hold" from "the vendor killed us anyway"
            // instead of guessing.
            var exempt = power.IsIgnoringBatteryOptimizations(context.PackageName!);
            global::Android.Util.Log.Info(LogTag,
                "wake lock held: the cpu stays up while listening; battery exemption: "
                + (exempt ? "granted" : "NOT granted - this phone may still hibernate the service"));
        }
        catch (Exception ex)
        {
            // Never fatal. A phone that refuses the lock still listens while it
            // is awake; it just stops sooner, which is the behaviour we had.
            global::Android.Util.Log.Warn(LogTag, "could not hold the cpu: " + ex.Message);
        }
    }

    private static void LetTheCpuSleep()
    {
        try
        {
            if (_awake is { IsHeld: true }) _awake.Release();
            global::Android.Util.Log.Info(LogTag, "wake lock released");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(LogTag, "could not release the cpu: " + ex.Message);
        }
        finally
        {
            _awake?.Dispose();
            _awake = null;
        }
    }

    /// <summary>Starts resident listening, if a listener has been supplied.</summary>
    public static async Task<bool> StartListeningAsync(CancellationToken ct = default)
    {
        var listener = _listener;
        if (listener is null) return false;
        if (listener.IsListening) return true;

        listener.Woke -= OnListenerWoke;
        listener.Woke += OnListenerWoke;
        listener.Nearly -= OnListenerNearly;
        listener.Nearly += OnListenerNearly;
        await listener.StartAsync(ct).ConfigureAwait(false);

        // AFTER, and only if it actually opened. Holding the CPU for a listener
        // that refused the microphone would drain the battery for nothing.
        if (listener.IsListening) HoldTheCpu();
        else LetTheCpuSleep();

        return listener.IsListening;
    }

    /// <summary>Stops resident listening and releases the microphone.</summary>
    public static async Task StopListeningAsync(CancellationToken ct = default)
    {
        var listener = _listener;
        if (listener is null) return;
        listener.Woke -= OnListenerWoke;
        listener.Nearly -= OnListenerNearly;
        await listener.StopAsync(ct).ConfigureAwait(false);

        // The lock outliving the microphone is a battery leak with no feature
        // attached, so it goes even if the stop threw.
        LetTheCpuSleep();
    }

    private static void OnListenerWoke(object? sender, string phrase) => Woke?.Invoke(sender, phrase);

    private static void OnListenerNearly(object? sender, ResidentNearMiss miss) =>
        Nearly?.Invoke(sender, miss);

    private static async Task SafeStopAsync(IResidentListener listener)
    {
        try { await listener.StopAsync().ConfigureAwait(false); } catch { /* replacing it anyway */ }
    }

    /// <summary>What the ongoing notification should say about the microphone.</summary>
    /// <remarks>
    /// Named rather than implied. Somebody glancing at their notification shade
    /// should be able to tell that this app is holding the microphone right now,
    /// and what it is waiting to hear — not have to infer it from "running".
    /// </remarks>
    internal static string ListeningNotificationText() =>
        _listener is { IsListening: true } l
            ? $"Listening for “{l.Describe}” — nothing is recorded or sent"
            : "Ready";
}
