#if IT_VOICE_ANDROID
#nullable enable

// ResidentAssistant.cs
//
// The call nobody was making.
//
// Everything needed to be an always-on assistant was already built and none of
// it was connected. CircleNeuronService has Start, a resident-listener seam, a
// Woke event and a sticky foreground service. ResidentWakeWord.Install builds
// the detector and hands it over. Between them: nothing. The app set
// OptionsFactory, read Status, and called Stop — there was no call to Start
// anywhere in the codebase.
//
// So the wake word ran inside HomeActivity, opened on resume and closed on
// pause. Close the app and the microphone closed with it. Every "always
// listening" measurement taken so far was of a thing you had to be looking at,
// which is a button you say instead of press.
//
// WHAT ANDROID ALLOWS, which is the shape of this file:
//
//   A plain background service cannot survive — the system stops it within about
//   a minute of the app going away. The only durable form is a FOREGROUND
//   service with a persistent notification, which is the same mechanism Shazam
//   uses for Auto Shazam. The notification is not an apology for holding the
//   microphone; it is the honest disclosure that we are.
//
//   From Android 12 a foreground service may not be STARTED from the background
//   at all, except from a short list — a BOOT_COMPLETED receiver, a notification
//   action, or while the app is visible. So it is started here, from a visible
//   screen, and re-armed on boot by BootReceiver.
//
//   From Android 14 the microphone type may not be started from BOOT_COMPLETED.
//   After a reboot the models come back by themselves; listening needs one
//   deliberate tap. That is a platform rule and not something to design around.
//
// None of that saves it from the phone's own vendor. Huawei, Xiaomi, Oppo and
// Vivo kill foreground services regardless, and only the owner can exempt the
// app — which is why setup asks for it rather than assuming.

using System;
using System.Threading.Tasks;
using Android.Content;
using CircleAI.Device;

namespace CircleAI.Samples.It.Mobile;

/// <summary>Starts and stops the always-on assistant.</summary>
public static class ResidentAssistant
{
    const string Tag = "CircleAI.Resident";

    static bool _wired;

    /// <summary>True when the service is up and holding the microphone.</summary>
    public static bool IsListening => CircleNeuronService.IsListening;

    /// <summary>
    /// Brings up the resident service and puts it on the microphone.
    /// </summary>
    /// <remarks>
    /// Idempotent. Called from whichever screen notices the phone is ready,
    /// because the start has to happen while something is visible.
    /// </remarks>
    /// <param name="context">A live context — an Activity, not the application.</param>
    /// <param name="bundleDirectory">The wake bundle on disk.</param>
    /// <param name="onWoke">Runs when the phrase is heard. Called off the UI thread.</param>
    public static async Task<bool> StartAsync(
        Context context, string bundleDirectory, EventHandler<string> onWoke)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            // The microphone is the whole point, and without the permission the
            // service would claim a type it cannot honour — see ClaimedTypes.
            if (context is Android.App.Activity a &&
                a.CheckSelfPermission(Android.Manifest.Permission.RecordAudio)
                    != Android.Content.PM.Permission.Granted)
            {
                Android.Util.Log.Info(Tag, "not starting: no microphone permission");
                return false;
            }

            // LISTEN FOR ITS NAME IN THE LANGUAGE THIS PHONE IS SET TO. Without
            // the language the wake word was always the English "Hey B", so
            // choosing Japanese on the languages screen left the phone waiting
            // for a phrase its owner would never say. ResidentWakeWord judges the
            // phrase against the bundle's own tokenizer and stays on English when
            // the model cannot hear it, so this is safe to pass unconditionally.
            // REBUILT WHEN THE LANGUAGE CHANGES, not only when nothing is
            // installed. The wake phrase is chosen at install time, so a listener
            // built for English keeps listening for "Hey B" after somebody picks
            // Japanese — the screen says one thing and the microphone waits for
            // another, with nothing to show which.
            var language = SpokenLanguage.Current(context);
            var stale = CircleNeuronService.Listener is not null &&
                        !string.Equals(ResidentWakeWord.InstalledLanguage, language,
                                       StringComparison.OrdinalIgnoreCase);
            if (stale)
            {
                Android.Util.Log.Info(Tag,
                    $"wake word was built for '{ResidentWakeWord.InstalledLanguage ?? "none"}', " +
                    $"language is now '{language}' — rebuilding");

                // STOP IT BEFORE DROPPING IT. Android hands out AudioRecord
                // exclusively, so a detector that is merely dereferenced keeps the
                // microphone and the replacement comes up deaf — the same
                // exclusivity that makes TalkOnce await StopHandsFreeAsync before
                // opening its own capture. Awaited, not fire-and-forget: the new
                // detector is built on the next line and must not race the old one
                // for the device.
                var old = CircleNeuronService.Listener;
                CircleNeuronService.Listener = null;
                if (old is not null)
                {
                    try { await old.StopAsync().ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        Android.Util.Log.Warn(Tag, "old wake detector would not stop: " + ex.Message);
                    }
                    try { await old.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        Android.Util.Log.Warn(Tag, "old wake detector would not dispose: " + ex.Message);
                    }
                }
            }

            if (CircleNeuronService.Listener is null &&
                !ResidentWakeWord.Install(context, bundleDirectory, languageCode: language))
                return false;

            // Subscribed once for the life of the process. The event is static
            // because Android owns the Service instance and may recreate it;
            // re-subscribing per screen would fire the turn once per screen that
            // had ever been open.
            if (!_wired)
            {
                CircleNeuronService.Woke += onWoke;
                _wired = true;
            }

            CircleNeuronService.Start(context);

            // Started, then told to listen. The service posts its notification
            // first and loads the brain afterwards, so asking for the microphone
            // immediately is safe and does not wait on the model.
            var listening = await CircleNeuronService.StartListeningAsync().ConfigureAwait(false);
            Android.Util.Log.Info(Tag, listening
                ? "resident listening: on"
                : "service started but the listener did not open the microphone");
            return listening;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error(Tag, "could not start resident assistant: " + ex);
            return false;
        }
    }

    /// <summary>Releases the microphone but leaves the models resident.</summary>
    /// <remarks>
    /// Two separate things, deliberately. Somebody who wants the phone to stop
    /// listening has not asked it to forget everything and reload half a
    /// gigabyte the next time they speak.
    /// </remarks>
    public static Task StopListeningAsync() => CircleNeuronService.StopListeningAsync();

    /// <summary>Stops the service entirely and gives the memory back.</summary>
    public static void Stop(Context context) => CircleNeuronService.Stop(context);
}
#endif
