// NotificationReaper.cs
//
// Taking down a "listening" notice that outlived the process behind it.
//
// WHY A RECEIVER AND NOT A TIMER. The first attempt at this was a lease on the
// notification itself - Notification.Builder.SetTimeoutAfter, renewed every
// thirty seconds by the live service, so a dead process would stop renewing and
// the platform would reap it. It does not work, and it was measured not working
// on a P30 on 2026-09-11: process force-stopped, no pid, no service record, and
// the notification still on the shade three minutes into a two-minute lease.
//
// The reason is that Android does not apply timeoutAfter to a notification
// carrying FLAG_FOREGROUND_SERVICE - it holds those for as long as the service
// runs, by design. When the process dies without an orderly stop the record is
// left orphaned with that flag still set, so the one mechanism that could expire
// it is the one the flag switches off.
//
// AN ALARM IS THE ONLY CLOCK THAT OUTLIVES THE PROCESS. AlarmManager keeps the
// schedule in the system, not in the app, and starts a fresh process to deliver
// the broadcast. So the living service pushes the alarm forward on every
// heartbeat and it never fires; when nothing is left to push it, it fires, this
// runs, and the notice comes down.
//
// The absence of a heartbeat is still the signal. What changed is who is holding
// the stopwatch.

using Android.App;
using Android.Content;

namespace CircleAI.Device;

/// <summary>Cancels the resident notification when nothing is behind it any more.</summary>
/// <remarks>
/// Exported=false: nothing outside this app may ask for this, and the alarm is
/// delivered by the system to an explicit component regardless.
/// </remarks>
[BroadcastReceiver(Name = "ai.circle.NotificationReaper", Enabled = true, Exported = false)]
public sealed class NotificationReaper : BroadcastReceiver
{
    /// <summary>The action this receiver answers to. Explicit intent; the string is for logs.</summary>
    internal const string Action = "ai.circle.REAP_STALE_NOTIFICATION";

    /// <inheritdoc />
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return;

        try
        {
            // THE LIVENESS TEST IS A STATIC IN THIS PROCESS, AND THAT IS EXACTLY
            // RIGHT. If the service is running, this broadcast was delivered into
            // its process and the handle is set - a late renewal, nothing more,
            // so leave the notice alone. If the process had died, the system
            // started a FRESH one to deliver this, where the static is null
            // because nothing has set it.
            //
            // No binder, no query to ActivityManager, no permission: the thing
            // being asked is "is this service alive in this process", and a
            // static is the honest answer to that question.
            if (CircleNeuronService.IsRunning)
            {
                // Still alive. Put the alarm back out rather than leaving the
                // app with no dead-man's switch at all until the next heartbeat.
                CircleNeuronService.ArmReaper(context);
                return;
            }

            if (context.GetSystemService(Context.NotificationService) is NotificationManager nm)
            {
                nm.Cancel(CircleNeuronService.NotificationId);
                global::Android.Util.Log.Info("CircleAI.Neuron",
                    "reaped a listening notice with no service behind it");
            }
        }
        catch (Exception ex)
        {
            // Never throw out of a receiver: the system treats that as the app
            // misbehaving, and the worst outcome here is that a stale notice
            // stays one cycle longer.
            global::Android.Util.Log.Warn("CircleAI.Neuron", "reaper failed: " + ex.Message);
        }
    }
}
