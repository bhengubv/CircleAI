// DeviceResidentAssistant.cs
//
// The always-on assistant on the phone. Ports ResidentAssistant from the native
// head, with one deliberate difference.
//
// THE LANGUAGE COMES FROM ISpokenLanguage, NOT THE NATIVE STATIC. The native
// file reads SpokenLanguage.Current(context) — its own SharedPreferences store.
// This app keeps the chosen language in StoredSpokenLanguage behind
// ISpokenLanguage. Linking the native file would give the app two stores, and
// somebody who picks Japanese in Settings would be left with a phone still
// listening for the English phrase, because the wake word read the other one.
// That is the same bug the native head already fixed once. So the orchestration
// is re-expressed here and only ResidentWakeWord — which takes the language as
// a parameter and carries no store — is linked.
//
// WHAT ANDROID ALLOWS, which is the shape of this file:
//
//   A plain background service cannot survive; the system stops it within about
//   a minute of the app going away. The only durable form is a FOREGROUND
//   service with a persistent notification.
//
//   From Android 12 a foreground service may not be STARTED from the background,
//   so Start is called from a visible screen and uses the current Activity.
//
// None of that saves it from the phone's own vendor: Huawei, Xiaomi, Oppo and
// Vivo kill foreground services regardless, which is what
// DeviceSetup.AllowBackgroundAsync asks the owner to exempt.

using CircleAI.Device;
using CircleAI.Samples.It.Mobile;

namespace CircleAI.Samples.It.App.Services;

/// <inheritdoc />
public sealed class DeviceResidentAssistant : IResidentAssistant
{
    private const string Tag = "CircleAI.Resident";

    private readonly ISpokenLanguage _spoken;
    private bool _wired;

    public DeviceResidentAssistant(ISpokenLanguage spoken) => _spoken = spoken;

    /// <inheritdoc />
    public bool IsListening => CircleNeuronService.IsListening;

    /// <inheritdoc />
    public event EventHandler<string>? Woke;

    /// <inheritdoc />
    public async Task<ResidentStatus> StartAsync(CancellationToken ct = default)
    {
        try
        {
            var context = Android.App.Application.Context;

            // The microphone is the whole point, and without the permission the
            // service would claim a type it cannot honour.
            if (context.CheckSelfPermission(Android.Manifest.Permission.RecordAudio)
                != Android.Content.PM.Permission.Granted)
            {
                Android.Util.Log.Info(Tag, "not starting: no microphone permission");
                return new ResidentStatus(ResidentState.NeedsPermission,
                    "Not listening",
                    "It needs the microphone. Turn on Hey B and allow it there.");
            }

            // Located the same way the wake screen locates it, from the one
            // method that knows a half-finished download is not an install.
            var bundle = DeviceWakeWord.FindBundle();
            if (bundle is null)
            {
                Android.Util.Log.Info(Tag, "not starting: no wake bundle on this device");
                return new ResidentStatus(ResidentState.NotInstalled,
                    "Not listening",
                    "The wake word is not downloaded yet. Open Hey B to get it.");
            }

            // LISTEN FOR ITS NAME IN THE LANGUAGE THIS PHONE IS SET TO, and
            // REBUILD when that changes — not only when nothing is installed.
            // The phrase is chosen at install time, so a listener built for
            // English keeps waiting for the English phrase after somebody picks
            // another language: the screen says one thing and the microphone
            // waits for another, with nothing to show which.
            var language = _spoken.Current;
            var stale = CircleNeuronService.Listener is not null &&
                        !string.Equals(ResidentWakeWord.InstalledLanguage, language,
                                       StringComparison.OrdinalIgnoreCase);
            if (stale)
            {
                Android.Util.Log.Info(Tag,
                    $"wake word was built for '{ResidentWakeWord.InstalledLanguage ?? "none"}', "
                    + $"language is now '{language}' - rebuilding");

                // STOPPED BEFORE IT IS DROPPED. Android hands out AudioRecord
                // exclusively, so a detector that is merely dereferenced keeps
                // the microphone and the replacement comes up deaf. Awaited, not
                // fire-and-forget: the new detector is built on the next line and
                // must not race the old one for the device.
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
                !ResidentWakeWord.Install(context, bundle, languageCode: language,
                                          keywordsFile: DeviceWakePhrases.KeywordFile(language)))
            {
                return new ResidentStatus(ResidentState.Failed,
                    "Not listening",
                    "The wake word would not load. Open Hey B and check it there.");
            }

            // Subscribed once for the life of the process. The event is static
            // because Android owns the Service instance and may recreate it;
            // re-subscribing per screen would fire the turn once per screen that
            // had ever been open.
            if (!_wired)
            {
                CircleNeuronService.Woke += OnWoke;
                _wired = true;
            }

            // FROM A VISIBLE SCREEN. Android 12 forbids starting a foreground
            // service from the background, so the current Activity is used when
            // there is one - this is called from a control somebody just tapped.
            var starter = (Android.Content.Context?)Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
                          ?? context;
            CircleNeuronService.Start(starter);

            // Started, then told to listen. The service posts its notification
            // first and loads the brain afterwards, so asking for the microphone
            // immediately is safe and does not wait on the model.
            var listening = await CircleNeuronService.StartListeningAsync().ConfigureAwait(false);
            Android.Util.Log.Info(Tag, listening
                ? "resident listening: on"
                : "service started but the listener did not open the microphone");

            // THE OWNER ASKED FOR THIS, and BootReceiver needs to know after a
            // restart. A consent record rather than a cache: it is the difference
            // between restoring something somebody chose and helping ourselves to
            // a foreground service on every boot.
            if (listening) ResidentPrefs.SetRunning(context, true);

            return listening
                ? new ResidentStatus(ResidentState.Listening,
                    "Listening",
                    "It answers to its name with the screen off.")
                : new ResidentStatus(ResidentState.Failed,
                    "Not listening",
                    "The service started but could not open the microphone. "
                    + "Something else may be holding it.");
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error(Tag, "could not start resident assistant: " + ex);
            return new ResidentStatus(ResidentState.Failed, "Not listening", ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<ResidentStatus> StopAsync(CancellationToken ct = default)
    {
        try
        {
            // The microphone back, the models kept. Stopping the SERVICE as well
            // would give the memory back and cost half a gigabyte of reloading
            // the next time somebody speaks, which nobody asked for by turning
            // listening off.
            await CircleNeuronService.StopListeningAsync().ConfigureAwait(false);

            // Turned off deliberately, so it stays off across a reboot.
            ResidentPrefs.SetRunning(Android.App.Application.Context, false);

            return new ResidentStatus(ResidentState.Off,
                "Not listening",
                "Tap to have it answer to its name again.");
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error(Tag, "could not stop resident assistant: " + ex);
            return new ResidentStatus(ResidentState.Failed, "Not listening", ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<ResidentStatus> ResumeAsync(CancellationToken ct = default)
    {
        // ALREADY UP. Android may have kept the service across a restart of the
        // UI, and starting it twice would re-register the listener.
        if (IsListening)
            return new ResidentStatus(ResidentState.Listening, "Listening", string.Empty);

        if (!ResidentPrefs.WasRunning(Android.App.Application.Context))
            return new ResidentStatus(ResidentState.Off, "Not listening",
                "Turn on Answer to its name to have it listen with the screen off.");

        Android.Util.Log.Info(Tag, "resuming: the owner had the assistant on");
        return await StartAsync(ct).ConfigureAwait(false);
    }

    private void OnWoke(object? sender, string phrase)
    {
        // "I HEARD YOU", BEFORE ANYTHING ELSE. Measured on a P30, the wake
        // phrase is followed by thirty to ninety seconds of work, and every
        // other sign of life is on a screen the person who just called from the
        // kitchen doorway is not looking at. This tone is the whole of what they
        // get, so it is played here - in the resident path, where it sounds
        // whether or not any screen is watching - rather than from a page.
        try { Earcon.Woke(); }
        catch (Exception ex) { Android.Util.Log.Warn(Tag, "earcon failed: " + ex.Message); }

        Woke?.Invoke(this, phrase);
    }
}
