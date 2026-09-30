// DeviceMicrophoneAccess.cs
//
// Whether this process may open the microphone — asked of Android, not assumed.
//
// WHY NOT THE MAUI ONE. MauiMicrophoneAccess uses Permissions.CheckStatusAsync, and
// the service is not a MAUI app: it has no Essentials, and pulling them in for one
// permission check would be a framework for a boolean. This is the same question
// asked of the platform directly.
//
// IT REPORTS, IT DOES NOT ASK, and that boundary is the service's shape rather than
// an omission. Only an Activity can raise a runtime permission dialog, and this app
// deliberately has no screens. The honest consequence is that a setup plan built
// here reflects what has actually been granted - so a phone where the microphone was
// never granted is told it cannot listen, instead of being handed a recogniser that
// will fail the first time somebody speaks.

using Android.Content;
using Android.Content.PM;
using CircleAI.Assistant;

namespace CircleAI.Assistant.Device;

/// <summary>The real state of this process's microphone permission.</summary>
/// <param name="context">Any context; the application context is right here.</param>
public sealed class DeviceMicrophoneAccess(Context context) : IMicrophoneAccess
{
    /// <inheritdoc />
    public Task<bool> GrantedAsync(CancellationToken ct = default)
    {
        bool granted;
        try
        {
            granted = context.CheckSelfPermission(Android.Manifest.Permission.RecordAudio)
                      == Permission.Granted;
        }
        catch
        {
            // A check that cannot be made is not a grant. Saying yes here would put a
            // recogniser in a plan that fails the moment it opens the microphone.
            granted = false;
        }

        return Task.FromResult(granted);
    }
}
