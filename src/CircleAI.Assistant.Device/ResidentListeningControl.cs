// ResidentListeningControl.cs
//
// "Answer to its name", as a switch the service can actually be asked to flip.
//
// THERE WAS NO IResidentAssistant ON THIS SIDE AT ALL. The only implementations in
// the tree were a browser one that says "impossible", a client one that said "open
// CircleAI" — about an app with no launcher icon — and the proxy that now calls
// this. So the switch a person saw in Settings had, on every head, nothing behind
// it.
//
// IT IS A VIEW OVER CircleNeuronService, NOT A SECOND LISTENER. That service already
// holds the microphone, owns the foreground notification the microphone is disclosed
// through, and survives the screen going off — which is the entire meaning of
// "answer to its name". Building a parallel one here would give a device two things
// competing for one microphone.
//
// THE STATE IS READ, NEVER REMEMBERED. Android can stop a foreground service, a
// person can revoke the microphone, a battery optimiser can step in — none of which
// this object would hear about. So every call asks the service what it is doing now.

using Android.Content;
using CircleAI.Assistant;
using CircleAI.Device;

namespace CircleAI.Assistant.Device;

/// <summary>Turns the resident listener on and off, and says what it is doing.</summary>
/// <param name="context">Any context; the application context is right here.</param>
public sealed class ResidentListeningControl(Context context) : IResidentAssistant
{
    /// <inheritdoc />
    public bool IsListening => CircleNeuronService.State == CircleNeuronService.ServiceState.Ready;

    /// <inheritdoc />
    /// <remarks>
    /// Declared to satisfy the interface, never raised here. The wake word fires
    /// inside CircleNeuronService; routing that out is a separate seam and inventing
    /// an event that never comes would be worse than an honest silence.
    /// </remarks>
    public event EventHandler<string>? Woke { add { } remove { } }

    /// <inheritdoc />
    public Task<ResidentStatus> StartAsync(CancellationToken ct = default)
    {
        try { CircleNeuronService.Start(context); }
        catch (Exception ex)
        {
            return Task.FromResult(new ResidentStatus(ResidentState.Failed,
                "Could not start listening", ex.Message));
        }
        return Task.FromResult(Now());
    }

    /// <inheritdoc />
    public Task<ResidentStatus> StopAsync(CancellationToken ct = default)
    {
        try { CircleNeuronService.Stop(context); }
        catch (Exception ex)
        {
            return Task.FromResult(new ResidentStatus(ResidentState.Failed,
                "Could not stop listening", ex.Message));
        }
        return Task.FromResult(new ResidentStatus(ResidentState.Off,
            "Not listening", "Turn it on to wake it by name."));
    }

    /// <inheritdoc />
    /// <remarks>Resuming is starting; the service decides whether that is a no-op.</remarks>
    public Task<ResidentStatus> ResumeAsync(CancellationToken ct = default) => StartAsync(ct);

    /// <inheritdoc />
    public Task<ResidentStatus> RefreshAsync(CancellationToken ct = default) => Task.FromResult(Now());

    /// <summary>What the resident service is doing, in the words a screen can show.</summary>
    /// <remarks>
    /// LOADING IS NOT LISTENING AND NOT OFF. It is the thirteen to twenty-three
    /// seconds a model takes to open on a P30, and a screen that shows "off" for that
    /// window invites somebody to tap the switch again.
    /// </remarks>
    private static ResidentStatus Now() => CircleNeuronService.State switch
    {
        CircleNeuronService.ServiceState.Ready =>
            new ResidentStatus(ResidentState.Listening, "Listening", CircleNeuronService.Status),
        CircleNeuronService.ServiceState.Loading =>
            new ResidentStatus(ResidentState.Off, "Getting ready", CircleNeuronService.Status),
        CircleNeuronService.ServiceState.Failed =>
            new ResidentStatus(ResidentState.Failed, "It could not start", CircleNeuronService.Status),
        _ =>
            new ResidentStatus(ResidentState.Off, "Not listening", "Turn it on to wake it by name."),
    };
}
