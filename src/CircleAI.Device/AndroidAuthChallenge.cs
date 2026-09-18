// AndroidAuthChallenge.cs
//
// The biometric / PIN / pattern gate, over the PLATFORM BiometricPrompt.
//
// NO AndroidX DEPENDENCY. android.hardware.biometrics.BiometricPrompt (API 28+)
// shows the same system sheet AndroidX.Biometric wraps, and every device this
// targets is API 28+ (P30 = 29). Using it keeps this library free of the AndroidX
// version dance that a plain-Android lib otherwise inherits.
//
// NEEDS A FOREGROUND SCREEN. A system auth sheet can only be shown from an
// Activity, so the host supplies the current one. A link request that arrives
// while Circle AI is in the background therefore cannot prompt in place — that
// path brings a small consent Activity to the front first (a follow-up); here,
// with no Activity, the challenge fails cleanly rather than throwing.

using System;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Hardware.Biometrics;
using Android.OS;
using CircleAI.Aether;
using Java.Lang;

namespace CircleAI.Device;

/// <summary>Presents the platform biometric / device-credential sheet for <see cref="IAuthChallenge"/>.</summary>
public sealed class AndroidAuthChallenge : IAuthChallenge
{
    // The authenticator bit-flags, as raw ints to avoid depending on the exact
    // generated enum names. From android.hardware.biometrics.BiometricManager.Authenticators:
    private const int BiometricWeak = 0x000000FF;   // BIOMETRIC_WEAK
    private const int DeviceCredential = 1 << 15;   // DEVICE_CREDENTIAL (PIN/pattern/password)

    private readonly Func<Activity?> _currentActivity;

    /// <param name="currentActivity">
    /// Supplies the foreground Activity to show the sheet on — e.g. the MAUI host
    /// passes <c>() =&gt; Platform.CurrentActivity</c>. May return null when nothing
    /// is in the foreground, in which case a challenge fails rather than throws.
    /// </param>
    public AndroidAuthChallenge(Func<Activity?> currentActivity)
        => _currentActivity = currentActivity ?? throw new ArgumentNullException(nameof(currentActivity));

    /// <inheritdoc/>
    public Task<AuthChallengeResult> RequestOsToggleAsync(bool enable, CancellationToken ct = default)
        => ChallengeAsync(
            AuthChallengeReason.OsLevelToggle,
            AuthMethod.BiometricAndDeviceAdmin,
            enable ? "Turn on the Circle service?" : "Turn off the Circle service?",
            ct);

    /// <inheritdoc/>
    public Task<AuthChallengeResult> ChallengeAsync(
        AuthChallengeReason reason, AuthMethod? minimumMethod, string prompt, CancellationToken ct = default)
    {
        var method = minimumMethod ?? AuthMethod.BiometricAndDeviceAdmin;

        var activity = _currentActivity();
        if (activity is null)
            return Task.FromResult(AuthChallengeResult.Failure(method, "no foreground screen to show the prompt"));

        var tcs = new TaskCompletionSource<AuthChallengeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var builder = new BiometricPrompt.Builder(activity)
                .SetTitle("Circle AI")
                .SetDescription(prompt);

            // Allow the device PIN/pattern/password alongside biometrics so a phone
            // with no fingerprint enrolled can still approve. From API 30 it is one
            // call and removes the need for a negative button; before that, the
            // deprecated flag is the only route.
            if ((int)Build.VERSION.SdkInt >= 30)
                builder = builder.SetAllowedAuthenticators(BiometricWeak | DeviceCredential);
            else
#pragma warning disable CS0618
                builder = builder.SetDeviceCredentialAllowed(true);
#pragma warning restore CS0618

            var sheet = builder.Build();
            var signal = new CancellationSignal();
            ct.Register(() => signal.Cancel());

            sheet.Authenticate(signal, activity.MainExecutor!, new Callback(tcs, method));
        }
        catch (System.Exception ex)
        {
            tcs.TrySetResult(AuthChallengeResult.Failure(method, ex.Message));
        }

        return tcs.Task;
    }

    private sealed class Callback : BiometricPrompt.AuthenticationCallback
    {
        private readonly TaskCompletionSource<AuthChallengeResult> _tcs;
        private readonly AuthMethod _method;

        public Callback(TaskCompletionSource<AuthChallengeResult> tcs, AuthMethod method)
        {
            _tcs = tcs;
            _method = method;
        }

        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult? result)
            => _tcs.TrySetResult(AuthChallengeResult.Success(_method));

        public override void OnAuthenticationError(BiometricErrorCode errorCode, ICharSequence? errString)
            => _tcs.TrySetResult(AuthChallengeResult.Failure(
                _method, errString?.ToString() ?? ("auth error " + (int)errorCode)));

        // A single non-match is not the end — the sheet lets the user try again;
        // give-up and cancel arrive as OnAuthenticationError.
        public override void OnAuthenticationFailed() { }
    }
}
