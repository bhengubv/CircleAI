#nullable enable

// BootReceiver.cs
//
// Coming back after the phone restarts.
//
// Without this the assistant is silent from the moment the phone reboots until
// somebody happens to open the app. A thing that forgets how to listen every
// time the battery dies is not an assistant, it is an app.
//
// WHAT IT CAN AND CANNOT DO, because the platform draws the line in an awkward
// place:
//
//   BOOT_COMPLETED is one of the few exemptions to Android 12's ban on starting
//   a foreground service from the background, so the model host CAN come back by
//   itself. That is the dataSync half, and it is the expensive half — several
//   hundred megabytes read off storage.
//
//   From Android 14, a MICROPHONE-typed foreground service may NOT be started
//   from BOOT_COMPLETED. So listening cannot resume on its own, by design: the
//   platform's position is that holding a microphone must follow a deliberate
//   act, not a power cycle. Trying to defeat that would be working against the
//   one protection a person has here.
//
// So: the brain comes back by itself, and the microphone waits for one tap. The
// notification the service posts is what makes that tap reachable without
// hunting for the app.

using Android.App;
using Android.Content;
using CircleAI.Device;

namespace CircleAI.Samples.It.Mobile;

/// <summary>Brings the resident service back after a restart.</summary>
[BroadcastReceiver(
    Name      = "com.bhengubv.itsample.BootReceiver",
    Enabled   = true,
    Exported  = true,
    Permission = "android.permission.RECEIVE_BOOT_COMPLETED")]
[IntentFilter(new[]
{
    Intent.ActionBootCompleted,
    // Some OEMs — Huawei among them — send their own after an update rather
    // than the standard one, and an app that listens for only the AOSP action
    // silently fails to come back on exactly the phones that need it most.
    "android.intent.action.QUICKBOOT_POWERON",
    "com.htc.intent.action.QUICKBOOT_POWERON",
})]
public sealed class BootReceiver : BroadcastReceiver
{
    const string Tag = "CircleAI.Boot";

    /// <inheritdoc/>
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return;

        try
        {
            // Only if the owner had it running before the reboot. Starting a
            // foreground service on a phone whose owner never asked for one is
            // how an app earns a place in somebody's battery settings.
            if (!ResidentPrefs.WasRunning(context))
            {
                Android.Util.Log.Info(Tag, "not restarting: was not running before reboot");
                return;
            }

            CircleNeuronService.Start(context);

            // Deliberately NOT calling StartListeningAsync. The microphone type
            // cannot be claimed from here on Android 14+, and pretending
            // otherwise would produce a service that dies on start. The models
            // load; the ear waits to be asked.
            Android.Util.Log.Info(Tag, "resident service restarted after boot");
        }
        catch (System.Exception ex)
        {
            Android.Util.Log.Error(Tag, "boot restart failed: " + ex);
        }
    }
}

/// <summary>Remembers whether the owner had the assistant running.</summary>
/// <remarks>
/// One flag, and it is a consent record rather than a cache: it is the
/// difference between restoring something somebody chose and helping ourselves
/// to a foreground service on every boot.
/// </remarks>
public static class ResidentPrefs
{
    const string File = "circleai.resident";
    const string Key  = "was-running";

    static ISharedPreferences? Prefs(Context c) =>
        (c.ApplicationContext ?? c).GetSharedPreferences(File, FileCreationMode.Private);

    /// <summary>Records that the owner has the assistant on, or has turned it off.</summary>
    public static void SetRunning(Context context, bool running) =>
        Prefs(context)?.Edit()?.PutBoolean(Key, running)?.Apply();

    /// <summary>Whether it was running when the phone last shut down.</summary>
    public static bool WasRunning(Context context) =>
        Prefs(context)?.GetBoolean(Key, false) ?? false;
}
