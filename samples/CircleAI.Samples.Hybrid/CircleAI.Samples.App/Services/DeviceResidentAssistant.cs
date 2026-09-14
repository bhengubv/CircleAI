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
using CircleAI.Assistant.Device;

namespace CircleAI.Assistant.Device;

/// <inheritdoc />
public sealed class DeviceResidentAssistant : IResidentAssistant
{
    private const string Tag = "CircleAI.Resident";

    private readonly ISpokenLanguage _spoken;
    private readonly IServiceProvider _services;
    private bool _wired;

    /// <param name="services">
    /// RESOLVED LATE, NOT INJECTED. DeviceWakePhrases already depends on this
    /// class to refresh the listener when a phrase changes; taking IWakePhrases
    /// here would close a cycle the container refuses to build. Asking the
    /// provider at start time keeps both singletons and breaks the loop.
    /// </param>
    public DeviceResidentAssistant(ISpokenLanguage spoken, IServiceProvider services)
    {
        _spoken = spoken;
        _services = services;
    }

    /// <summary>
    /// The screen-up loop, built only on a phone whose vendor refused the
    /// service the microphone.
    /// </summary>
    private ScreenUpWakeWord? _screenUp;

    /// <inheritdoc />
    /// <remarks>
    /// EITHER WAY OF LISTENING COUNTS. Settings reads this straight into the
    /// switch - `_residentOn = Resident.IsListening` - so a phone running on the
    /// fallback would have shown the control off while the microphone was open,
    /// which is the worst of the three possible answers.
    /// </remarks>
    public bool IsListening
        => CircleNeuronService.IsListening || _screenUp is { IsListening: true };

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
                    "It needs the microphone. Open Waking and allow it there.");
            }

            // Located the same way the wake screen locates it, from the one
            // method that knows a half-finished download is not an install.
            var bundle = DeviceWakeWord.FindBundle();
            if (bundle is null)
            {
                Android.Util.Log.Info(Tag, "not starting: no wake bundle on this device");
                return new ResidentStatus(ResidentState.NotInstalled,
                    "Not listening",
                    "The wake word is not downloaded yet. Open Waking to get it.");
            }

            // LISTEN FOR ITS NAME IN THE LANGUAGE THIS PHONE IS SET TO, and for
            // THE PHRASE THE OWNER CHOSE — rebuilding when either moves, not
            // only when nothing is installed.
            //
            // This used to compare LANGUAGES, which is a proxy for the phrase and
            // a coarser one: English to Japanese rebuilt, "Hey B" to
            // "Hey Circle AI" did not. Measured on a P30 on 2026-09-06 with the
            // screen, the settings table and the keywords file all reading
            // "Hey Circle AI" while the microphone reported
            // closest="Hey B" 2/3 tokens.
            var language = _spoken.Current;

            // ONE OWNER FOR THE PHRASE, EVERY TIME. The file the listener reads
            // is rewritten from the same store the screens read, before it is
            // read - see DeviceWakePhrases.EnsureCurrent for the Redmi 12 that
            // showed "Hey Circle AI" and listened for "Hey B".
            if (_services.GetService(typeof(IWakePhrases)) is DeviceWakePhrases phrases)
                phrases.EnsureCurrent(language);

            var keywords = DeviceWakePhrases.KeywordFile(language);
            if (CircleNeuronService.Listener is not null &&
                ResidentWakeWord.Built.IsStaleFor(language, keywords))
            {
                await RebuildAsync(language).ConfigureAwait(false);
            }

            if (CircleNeuronService.Listener is null &&
                !ResidentWakeWord.Install(context, bundle, languageCode: language,
                                          keywordsFile: keywords))
            {
                return new ResidentStatus(ResidentState.Failed,
                    "Not listening",
                    "The wake word would not load. Open Waking and check it there.");
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
            if (listening)
            {
                ResidentPrefs.SetRunning(context, true);

                // The service has it. Anything this process was holding has to go
                // or the two fight over one AudioRecord.
                await StopScreenUpAsync().ConfigureAwait(false);

                return new ResidentStatus(ResidentState.Listening,
                    "Listening",
                    "It answers to its name with the screen off.");
            }

            // THE SERVICE COULD NOT HOLD THE MICROPHONE, WHICH ON THIS PHONE MAY
            // SIMPLY BE THE ANSWER. Huawei, Xiaomi, Oppo and Vivo stop foreground
            // services on their own schedule whatever the notification says. This
            // used to return Failed and stop, so the product's headline feature
            // was absent on a large share of exactly the handsets it is for.
            var fallback = await StartScreenUpAsync(bundle, keywords).ConfigureAwait(false);
            if (fallback)
            {
                ResidentPrefs.SetRunning(context, true);

                return new ResidentStatus(ResidentState.ScreenOnly,
                    "Listening while the app is open",
                    "This phone stops background listening. It still answers to "
                    + "its name while the app is on screen.");
            }

            return new ResidentStatus(ResidentState.Failed,
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
            await StopScreenUpAsync().ConfigureAwait(false);

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

    /// <summary>Drops the running detector so the next Install builds a fresh one.</summary>
    /// <remarks>
    /// STOPPED BEFORE IT IS DROPPED. Android hands out AudioRecord exclusively,
    /// so a detector that is merely dereferenced keeps the microphone and the
    /// replacement comes up deaf. Awaited rather than fire-and-forget: the
    /// replacement is built immediately afterwards and must not race the old one
    /// for the device.
    /// </remarks>
    private static async Task RebuildAsync(string language)
    {
        Android.Util.Log.Info(Tag,
            $"wake word was built for '{ResidentWakeWord.Built.Language ?? "none"}' "
            + $"from '{ResidentWakeWord.Built.Keywords ?? "the bundle's own"}', "
            + $"and that has changed — rebuilding for '{language}'");

        var old = CircleNeuronService.Listener;
        CircleNeuronService.Listener = null;
        if (old is null) return;

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

    /// <inheritdoc />
    public async Task<ResidentStatus> RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            // NOTHING TO CATCH UP. A listener that is off will be built from
            // whatever is current the moment somebody turns it on, and starting
            // it here would turn choosing a phrase into switching the microphone
            // on for somebody who had switched it off.
            if (!IsListening)
                return new ResidentStatus(ResidentState.Off, "Not listening", string.Empty);

            var language = _spoken.Current;
            if (!ResidentWakeWord.Built.IsStaleFor(language, DeviceWakePhrases.KeywordFile(language)))
                return new ResidentStatus(ResidentState.Listening, "Listening", string.Empty);

            await RebuildAsync(language).ConfigureAwait(false);
            return await StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error(Tag, "could not refresh the resident listener: " + ex);
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

    /// <summary>Open the microphone in this process, for as long as the app is up.</summary>
    /// <remarks>
    /// TIED TO THE SCREEN, DELIBERATELY. A microphone that is always open is a
    /// promise about privacy, and this fallback exists because the phone refused
    /// the one form of always-open Android sanctions - a foreground service with
    /// a notification somebody can see. Holding the mic silently after the app
    /// goes away would be quietly doing the thing the notification exists to
    /// declare.
    /// </remarks>
    private async Task<bool> StartScreenUpAsync(string bundle, string? keywords)
    {
        try
        {
            if (_screenUp is null)
            {
                _screenUp = new ScreenUpWakeWord(bundle, keywords);
                _screenUp.Woke += OnWoke;

                // Subscribed once, for the life of this singleton. Unsubscribed in
                // StopScreenUpAsync along with the listener itself.
                AppLifecycle.Paused += OnWentAway;
                AppLifecycle.Resumed += OnCameBack;
            }

            _screenUp.Start();

            // START IS NOT LISTENING. It spawns the loop, and the loop opens the
            // microphone - so asking immediately reports the task, not the
            // device. A phone that will not give this process the mic either
            // fails inside that first moment, and saying "listening" for it would
            // be the same lie the service just told.
            await Task.Delay(250).ConfigureAwait(false);

            var up = _screenUp.IsListening;
            Android.Util.Log.Info(Tag, up
                ? "the service could not hold the microphone; listening on screen instead"
                : "neither the service nor this process could open the microphone");

            return up;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error(Tag, "screen-up wake word would not start: " + ex);
            return false;
        }
    }

    /// <summary>Close the in-process microphone and forget the loop.</summary>
    private async Task StopScreenUpAsync()
    {
        var loop = _screenUp;
        if (loop is null) return;
        _screenUp = null;

        AppLifecycle.Paused -= OnWentAway;
        AppLifecycle.Resumed -= OnCameBack;
        loop.Woke -= OnWoke;

        try { await loop.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            Android.Util.Log.Warn(Tag, "screen-up wake word would not stop: " + ex.Message);
        }
    }

    /// <summary>Off screen: give the microphone back.</summary>
    /// <remarks>
    /// THE LOOP IS KEPT, THE CAPTURE IS NOT. Rebuilding the spotter on every
    /// resume would reload the Zipformer models, which is seconds; stopping the
    /// capture is what actually releases AudioRecord.
    /// </remarks>
    private void OnWentAway()
    {
        var loop = _screenUp;
        if (loop is null) return;

        _ = Task.Run(async () =>
        {
            try { await loop.StopAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                Android.Util.Log.Warn(Tag, "could not release the microphone: " + ex.Message);
            }
        });
    }

    /// <summary>Back on screen: take it again, unless the service has it now.</summary>
    private void OnCameBack()
    {
        var loop = _screenUp;
        if (loop is null) return;

        // THE SERVICE MAY HAVE COME BACK WHILE WE WERE AWAY - the owner may have
        // granted the battery exemption in the settings screen they just left to.
        // Two things holding one AudioRecord is one of them getting silence.
        if (CircleNeuronService.IsListening) return;

        try { loop.Start(); }
        catch (Exception ex)
        {
            Android.Util.Log.Warn(Tag, "could not reopen the microphone: " + ex.Message);
        }
    }

    private void OnWoke(object? sender, string phrase)
    {
        // "I HEARD YOU", BEFORE ANYTHING ELSE. Measured on a P30, the wake
        // phrase is followed by thirty to ninety seconds of work, and every
        // other sign of life is on a screen the person who just called from the
        // kitchen doorway is not looking at. This tone is the whole of what they
        // get, so it is played here - in the resident path, where it sounds
        // whether or not any screen is watching - rather than from a page.
        // "YES?" IN A VOICE, WHEN THERE IS ONE READY; THE TONE OTHERWISE. Rendered
        // at warm-up and played from disk, so it is as instant as the tone was
        // and says "I'm here" where the tone said "beep". It plays inside the
        // settle before the turn's microphone opens, which is why the line is
        // short. See AckBank.
        _ = Task.Run(async () =>
        {
            try
            {
                if (!await AckBank.PlayAsync(_spoken.Current, AckBank.Woke).ConfigureAwait(false))
                    Earcon.Woke();
            }
            catch (Exception ex) { Android.Util.Log.Warn(Tag, "acknowledgement failed: " + ex.Message); }
        });

        Woke?.Invoke(this, phrase);
    }
}
