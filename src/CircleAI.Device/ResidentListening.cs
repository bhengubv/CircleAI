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
public interface IResidentListener : IAsyncDisposable
{
    /// <summary>True while the microphone is open.</summary>
    bool IsListening { get; }

    /// <summary>What it is listening for, for the notification.</summary>
    string Describe { get; }

    /// <summary>Raised with the phrase heard.</summary>
    event EventHandler<string>? Woke;

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

    /// <summary>True when the service currently holds the microphone.</summary>
    public static bool IsListening => _listener?.IsListening == true;

    /// <summary>Starts resident listening, if a listener has been supplied.</summary>
    public static async Task<bool> StartListeningAsync(CancellationToken ct = default)
    {
        var listener = _listener;
        if (listener is null) return false;
        if (listener.IsListening) return true;

        listener.Woke -= OnListenerWoke;
        listener.Woke += OnListenerWoke;
        await listener.StartAsync(ct).ConfigureAwait(false);
        return listener.IsListening;
    }

    /// <summary>Stops resident listening and releases the microphone.</summary>
    public static async Task StopListeningAsync(CancellationToken ct = default)
    {
        var listener = _listener;
        if (listener is null) return;
        listener.Woke -= OnListenerWoke;
        await listener.StopAsync(ct).ConfigureAwait(false);
    }

    private static void OnListenerWoke(object? sender, string phrase) => Woke?.Invoke(sender, phrase);

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
