// MicPermission.cs
//
// The one place this app asks for the microphone.
//
// PERMISSIONS.REQUESTASYNC THROWS OFF THE MAIN THREAD, and three separate
// classes reached it from behind a ConfigureAwait(false):
//
//   DeviceConversation.TurnAsync      - the circle on Home and the tab bar
//   DeviceConversation.DictateAsync   - the CV and interpreter screens
//   DeviceSetup.AllowMicrophoneAsync  - the setup tour
//   DeviceWakeWord.RequestMicrophoneAsync - the wake screen
//
// So on a fresh install the first press of any of them threw "Permission
// request must be invoked on main thread" instead of showing the system
// prompt, and the turn reported the exception - an app that looks broken
// rather than one that has not been allowed to listen yet.
//
// It hid behind every device that had already said yes: once granted, the
// check short-circuits and the throwing line never runs. It took reinstalling
// on a P30 to see it at all.
//
// DeviceSetup.AllowBackgroundAsync already marshalled its own intent this way,
// so the rule was known in this project - just not written down anywhere the
// other four call sites could follow it. Now it is written once.

namespace CircleAI.Samples.It.App.Services;

/// <summary>Asks for the microphone, on the thread Android insists on.</summary>
internal static class MicPermission
{
    /// <summary>
    /// The microphone permission, asking for it if it has not been given.
    /// </summary>
    /// <remarks>
    /// Checking is safe from any thread; only the REQUEST is thread-bound, so
    /// only that half is marshalled. A granted permission therefore costs
    /// nothing extra, which matters on the path a turn takes every time.
    /// </remarks>
    public static async Task<PermissionStatus> EnsureAsync()
    {
        var mic = await Permissions.CheckStatusAsync<Permissions.Microphone>()
            .ConfigureAwait(false);
        if (mic == PermissionStatus.Granted) return mic;

        return await MainThread.InvokeOnMainThreadAsync(
            Permissions.RequestAsync<Permissions.Microphone>).ConfigureAwait(false);
    }

    /// <summary>The same question, when only yes or no matters.</summary>
    public static async Task<bool> GrantedAsync()
        => await EnsureAsync().ConfigureAwait(false) == PermissionStatus.Granted;
}
