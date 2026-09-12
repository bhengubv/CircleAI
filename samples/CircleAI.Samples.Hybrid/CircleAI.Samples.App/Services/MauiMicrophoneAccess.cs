// MauiMicrophoneAccess.cs
//
// The head's answer to "may I listen".
//
// THIS IS WHY IMicrophoneAccess IS AN INTERFACE. Asking for the microphone can
// put a dialog in front of somebody, and a dialog needs an Activity, a lifecycle
// and a screen - none of which a library that runs the assistant has, or should
// pretend to have. The assistant asks the question; this app knows how to get it
// answered, because this app is the thing on screen.
//
// It is four lines because MAUI already does the work. The point was never that
// the code is hard - it is that the turn loop, the wake word and first-run setup
// could only be compiled inside a MAUI application while this call was a static
// buried in the middle of them.

namespace CircleAI.Assistant.Device;

/// <inheritdoc />
public sealed class MauiMicrophoneAccess : IMicrophoneAccess
{
    /// <inheritdoc />
    /// <remarks>
    /// ASKS, not just reports - see the interface. MicPermission.EnsureAsync
    /// checks first and only prompts when it has to, so a granted permission
    /// costs nothing and the first turn somebody takes is the one that asks.
    /// </remarks>
    public Task<bool> GrantedAsync(CancellationToken ct = default)
        => MicPermission.GrantedAsync();
}
