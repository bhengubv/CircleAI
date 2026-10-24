// ResidentListeningControl.cs
//
// "Answer to its name" — the switch, and the thing behind it.
//
// THERE WAS NOTHING BEHIND IT ON ANY HEAD. The only IResidentAssistant
// implementations in the tree were a browser one saying "impossible" and a client
// one saying "open CircleAI" — about an app with no launcher icon. Meanwhile every
// part a wake word needs had been built and left unreachable:
//
//   ScreenUpWakeWord          complete, ZERO callers in the repo
//   ZipformerKwsSpotter       complete, reached only by the above
//   DeviceWakePhrases         writes the keyword file, and its KeywordFile is
//                             documented "Public: the resident wake service reads
//                             the same file the app writes" — a service nobody wrote
//   KWS-Zipformer-HeyB        6.4 MB, downloaded, never opened
//   RECORD_AUDIO              declared in the manifest, never granted, because a
//                             service with no Activity cannot raise the dialog
//
// Five finished pieces and no line joining them. This is that line.
//
// THE NOTIFICATION IS NOT OPTIONAL. CircleNeuronService is started alongside the
// spotter because it owns the foreground notification the microphone is disclosed
// through — "listening for Hey Circle AI, and that sound is not kept". A phone
// holding an open microphone with nothing on the shade is not disclosure, it is the
// absence of it, and that is the one thing this feature must never get wrong.
//
// AND FROM ANDROID 11 THE PLATFORM HAS AN OPINION ABOUT WHO MAY OPEN IT. A
// foreground service started from the BACKGROUND is given no microphone: refused on
// 11, silenced on 12 and 13, and from 14 the microphone-typed start is rejected
// outright. Every start this class makes except one is a background start — the
// process-start resume, the ResidentStart verb, and the fetch's "your bundle just
// landed" — so the honest answer is not to claim to be listening and hope. It
// opens the microphone, ASKS THE PLATFORM whether it is actually being heard
// (AppOps), and when it is not it says so and waits for a visible screen. See
// CanBeHeard.

using System.Linq;
using Android.Content;
using Android.OS;
using CircleAI.Assistant;
using CircleAI.Device;

namespace CircleAI.Assistant.Device;

/// <summary>Turns the wake word on and off, and says what it is doing.</summary>
/// <param name="context">Any context; the application context is right here.</param>
public sealed class ResidentListeningControl(Context context) : IResidentAssistant
{
    private const string Tag = "CircleAI.Wake";

    /// <summary>The directory that actually holds the spotter's graphs.</summary>
    /// <remarks>
    /// THE BUNDLE UNPACKS A LEVEL DOWN, and assuming otherwise fails late and
    /// obscurely: ZipformerKwsSpotter throws "no *encoder*.onnx in ..." from inside
    /// its constructor, which surfaces as the wake loop dying rather than as a model
    /// that is not where it was expected. On the P30 the entry is
    /// KWS-Zipformer-HeyB/ and the graphs are in KWS-Zipformer-HeyB/kws-hey-b/.
    /// <para>
    /// So the encoder is what is searched for, not a path that is guessed. One level
    /// down is enough for every bundle shape shipped so far, and a bundle that nests
    /// deeper should say so rather than be found by a recursive walk of the model
    /// store.
    /// </para>
    /// </remarks>
    private static string? BundleDir()
    {
        var root = Path.Combine(ModelStore.Path, KwsBundle);
        if (!Directory.Exists(root)) return null;
        if (HasEncoder(root)) return IsComplete() ? root : null;

        foreach (var child in Directory.EnumerateDirectories(root))
            if (HasEncoder(child)) return IsComplete() ? child : null;

        return null;
    }

    /// <summary>The keyword spotter's bundle, by its catalogue name.</summary>
    public const string KwsBundle = "KWS-Zipformer-HeyB";

    /// <summary>Whether the wake word listens with <paramref name="model"/>, so its arrival is a reason to start.</summary>
    public static bool ListensWith(string model) => model is KwsBundle or TranscribingWake.ModelId;

    /// <summary>Whether everything the spotter opens is on disk.</summary>
    /// <remarks>
    /// AN ENCODER IS NOT A BUNDLE. Finding one was the whole test, and on the P30's
    /// fresh install on 2026-10-23 the microphone was allowed thirteen seconds into
    /// setup, while this bundle was still arriving: the encoder was here, keywords.txt
    /// was not. The spotter built its three sessions, threw FileNotFound on the
    /// keywords, the loop died - and the status had already said "Listening".
    /// <para>
    /// ASKED OF THE CATALOGUE, NOT OF A LIST OF FILE NAMES WRITTEN OUT HERE. The
    /// first fix for that hand-listed five names, which made a SECOND owner of "is
    /// this bundle complete" - and it disagreed with the loader's, which counted the
    /// bundle present as soon as its largest file arrived. Two definitions, opposite
    /// answers, and the state between them was unrecoverable: every fetch path
    /// believed the bundle was here and this one refused to start, for ever, with a
    /// hint telling the person to run a setup that would skip it.
    /// </para>
    /// <para>
    /// It also hand-picked five of the seven catalogued files, so a start that landed
    /// between keywords.txt and bpe.model passed. BundleModelLoader.ModelPresent now
    /// requires every file of a bundle with no weight-file anchor, which is the one
    /// owner of the question - and it is the same call the resume and the first-run
    /// plan make, so all three agree by construction.
    /// </para>
    /// </remarks>
    private static bool IsComplete()
    {
        // ASKED SPARINGLY, BECAUSE THE WAKE SCREEN ASKS EVERY SECOND. The status verb
        // polls at 1 Hz while that screen is open and it lands in RefreshAsync, which
        // reads this - and the answer costs an embedded-registry parse. Once a bundle
        // is complete it stays complete, so a yes is final; a no is re-asked, but not
        // more often than a download could plausibly finish.
        if (_complete) return true;
        if (_askedAt.IsRunning && _askedAt.Elapsed < AskAgainAfter) return false;
        _askedAt.Restart();

        try
        {
            using var registry = new CircleAI.Core.Models.ModelRegistryService();
            using var loader = new CircleAI.Inference.BundleModelLoader(ModelStore.Path, registry);
            _complete = loader.ModelPresent(KwsBundle);
            return _complete;
        }
        catch (Exception ex)
        {
            // CANNOT TELL IS NOT THE SAME AS NOT THERE, and it is said out loud. A
            // registry that will not open would otherwise read exactly like a bundle
            // that never downloaded, and the advice for those two is opposite.
            Android.Util.Log.Warn(Tag, "could not ask the catalogue what the wake bundle needs: " + ex.Message);
            return false;
        }
    }

    private static bool _complete;
    private static readonly System.Diagnostics.Stopwatch _askedAt = new();
    private static readonly TimeSpan AskAgainAfter = TimeSpan.FromSeconds(3);

    private static bool HasEncoder(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*encoder*.onnx").Any();
        }
        catch
        {
            return false;
        }
    }

    private ScreenUpWakeWord? _ears;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc />
    public bool IsListening => _ears?.IsListening == true;

    /// <inheritdoc />
    /// <remarks>Forwarded from the spotter, so a host can act on the phrase.</remarks>
    public event EventHandler<string>? Woke;

    /// <inheritdoc />
    public Task<ResidentStatus> StartAsync(CancellationToken ct = default) => StartAsync(false, ct);

    /// <summary>
    /// Starts listening, saying whether the caller has a visible screen of ours.
    /// </summary>
    /// <param name="fromVisibleScreen">
    /// True only from MicrophoneActivity, which IS the visible screen. Everything else
    /// - the process-start resume, the ResidentStart verb, the fetch's landed hook -
    /// is a background start, whatever the app on the other side of the link is doing:
    /// the platform judges the uid that asks, and that uid is this service's.
    /// </param>
    /// <remarks>
    /// IT IS STILL TRIED FROM THE BACKGROUND, because on Android 10 it works, on 12
    /// and 13 it works while a bound client of ours has a window up, and on 14+ it
    /// works whenever the service record still carries the while-in-use flag from an
    /// earlier visible start. The flag is what this parameter is really about: a start
    /// made while a screen of ours is up latches it for the life of the service
    /// record, so the background start that comes minutes later is allowed.
    /// </remarks>
    public async Task<ResidentStatus> StartAsync(bool fromVisibleScreen, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ears is { IsListening: true }) return Listening();

            // THE MODEL FIRST, because the honest refusal is specific. Without the
            // bundle the spotter throws deep inside its first audio chunk, which
            // surfaces as "it stopped" rather than "you have not downloaded it yet".
            //
            // AND BOTH REFUSALS ARE LOGGED, BECAUSE THEY WERE NOT. The start at boot
            // printed "resuming: the owner had it on" and threw the answer away, so a
            // phone that could never listen looked, in its own log, exactly like one
            // that was listening.
            var bundle = BundleDir();
            if (bundle is null)
            {
                Android.Util.Log.Info(Tag, "not listening: the wake word is not downloaded yet");
                return NotDownloaded();
            }

            // The microphone is a runtime permission and only an Activity can ask for
            // it. MicrophoneActivity does, when a client trusted with Voice starts it -
            // the app's "Allow the microphone" buttons, and switching this on. Until
            // then, say so rather than opening a recorder that returns silence for ever.
            if (!MicrophoneAllowed())
            {
                Android.Util.Log.Info(Tag, "not listening: the microphone has not been allowed");
                return NeedsMicrophone();
            }

            // THE FILE IS BROUGHT UP TO DATE BEFORE IT IS READ, which is what
            // DeviceWakePhrases.EnsureCurrent was written for and what nothing
            // had ever called it for.
            //
            // Its own documentation says "Called at every start of resident
            // listening. Idempotent and cheap." It had ZERO callers in the
            // repository. So the keyword file was only ever rewritten when
            // somebody added or chose a phrase in the app, and a phone kept
            // whatever spelling the build of that moment produced - for ever.
            //
            // That is not hypothetical. On 2026-10-11 the spelling of the
            // owner's own phrase was corrected in WakePhraseBook (the product is
            // "B!", said "Bee", and the keyword file had been asking for the
            // initialism letter B, which the acoustic model never emits). The
            // corrected build was installed on the P30 and changed nothing at
            // all, because the file on disk had been written by the older one
            // and nothing re-wrote it. The fix was in the APK and could not
            // reach the phone.
            //
            // BEFORE THE FILE'S EXISTENCE IS TESTED, which is the ordering this method
            // nearly lost: the line below decides between the owner's phrase and the
            // bundle's built-in one by asking whether that file is there, and on a
            // fresh install EnsureCurrent is what puts it there. Tested first, every
            // first start would listen for "Hey B" whatever the owner had chosen -
            // which is exactly the log line this whole area was investigated for
            // ("listening, keywords=built-in").
            //
            // Swallowed, because a file that cannot be refreshed is a reason to
            // listen with yesterday's phrase, not a reason not to listen.
            try { (CircleNeuronLinkService.WakePhrases as DeviceWakePhrases)?.EnsureCurrent("en"); }
            catch (Exception ex)
            {
                Android.Util.Log.Warn(Tag, "could not refresh the wake phrases: " + ex.Message);
            }

            // THE LISTENER BEFORE THE SERVICE, AND THE ORDER DECIDES THE SERVICE'S TYPE.
            //
            // This used to call Start here and install the listener seventy lines
            // later, and CircleNeuronService.ClaimedTypes reads the listener to decide
            // whether to claim FOREGROUND_SERVICE_TYPE_MICROPHONE. OnStartCommand runs
            // on the main looper while this method runs on whatever thread asked - the
            // fetch service's download thread, a binder thread - so which of the two
            // got there first was a race, and the losing order claimed dataSync only.
            // A foreground service without the microphone type is given no microphone
            // capability on any Android from 11 on, so the spotter would have opened a
            // recorder that could only ever return silence, on a phone where every
            // other condition was right.
            //
            // Set first, cleared on every failure below (LetGoAsync), so the service
            // can never see a listener that is not about to run.
            var keywords = DeviceWakePhrases.KeywordFile("en");
            var ears = new ScreenUpWakeWord(bundle, File.Exists(keywords) ? keywords : null);
            ears.Woke += (_, phrase) =>
            {
                Android.Util.Log.Info(Tag, $"woke on \"{phrase}\"");
                Woke?.Invoke(this, phrase);
            };

            CircleNeuronService.Listener = ears;
            _ears = ears;

            // THE DISCLOSURE, BEFORE THE MICROPHONE. CircleNeuronService owns the
            // ongoing notification; starting the spotter first would open a recorder
            // with nothing on the shade to say so.
            try { CircleNeuronService.Start(context); }
            catch (Exception ex)
            {
                Android.Util.Log.Warn(Tag, "foreground service did not start: " + ex.Message);
            }

            // THROUGH THE SERVICE, NOT PAST IT, AND THE SCREEN SHOWED WHY.
            //
            // This used to call ears.Start() directly and keep the spotter to
            // itself. CircleNeuronService.IsListening - the one static three other
            // places read to answer "is the microphone open" - therefore stayed
            // false while the microphone was open, and Settings printed both
            // answers at once on a P30 on 2026-10-01: "Answer to its name" ticked,
            // because that reads THIS object, and the Waking ability still offering
            // "Turn on", because that reads the static. Same screen, same second.
            //
            // AND TWO THINGS THE PRIVATE LOOP COULD NOT HAVE. StartListeningAsync
            // takes the PARTIAL WAKE LOCK, without which "it answers with the screen
            // off" lasts until the CPU suspends and then silently does not - the
            // failure measured on a P30 on 2026-09-05, eleven minutes of somebody
            // talking to a phone that was not scheduled to hear them. And it writes
            // the microphone disclosure on the shade, naming the phrase, which is
            // the one thing this feature must never get wrong.
            var up = await CircleNeuronService.StartListeningAsync(ct).ConfigureAwait(false);
            if (!up)
            {
                await LetGoAsync().ConfigureAwait(false);
                return new ResidentStatus(ResidentState.Failed,
                    "It could not start listening",
                    "The microphone did not open. Something else on this phone may be holding it.");
            }

            // AND IS IT ACTUALLY BEING HEARD? The microphone opened, the loop is
            // running, the shade says so - and on Android 12 and 13 a background
            // start is handed a SILENCED stream, which looks identical from in here.
            // See CanBeHeard: this is the question the status used to answer by
            // assuming.
            if (!CanBeHeard())
            {
                Android.Util.Log.Warn(Tag,
                    "the microphone opened but this phone is silencing it - a foreground service "
                    + "started from the background gets no microphone from Android 11 on. "
                    + "Waiting for a screen of ours to be up"
                    + (fromVisibleScreen ? " (and this start WAS from a visible screen, which is worth a bug)" : ""));

                await LetGoAsync().ConfigureAwait(false);

                // THE SHADE SAYS IT TOO, and it is the only surface left: the person
                // may be nowhere near the app. WaitingToBeAsked already carries the
                // "tap to switch it back on" line and the tap target, which is exactly
                // the one visible moment this needs.
                CircleNeuronService.WaitingToBeAsked = true;
                CircleNeuronService.WaitingBecause   = "until an app of ours is open";
                CircleNeuronService.RefreshNotification();
                return WaitingForAScreen();
            }

            // AND WRITE IT DOWN, WHICH NOTHING HAS EVER DONE. ResidentPrefs.SetRunning
            // had zero callers in the repo - so the record the boot path reads, and
            // the record that is supposed to make an explicit "off" survive a
            // restart, was never written by anybody. WasRunning defaults to true, so
            // the failure was silent in the direction nobody notices: turning
            // listening OFF did not stick, and the file's own remarks say it does.
            Remember(true);

            Android.Util.Log.Info(Tag, $"listening, keywords={(File.Exists(keywords) ? keywords : "built-in")}");
            return Listening();
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error(Tag, "could not start listening: " + ex);
            try { await LetGoAsync().ConfigureAwait(false); } catch { /* already failing */ }
            return new ResidentStatus(ResidentState.Failed, "It could not start listening", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Put the microphone down and leave nothing installed behind it.</summary>
    /// <remarks>
    /// A LISTENER LEFT ON THE SERVICE IS A SERVICE THAT CLAIMS THE MICROPHONE TYPE.
    /// Every failure path below has to clear it, or the next start - and on Android
    /// 14+ every later startForeground, including the one at boot - claims a type the
    /// platform will refuse, and the service does not come up at all.
    /// </remarks>
    private async Task LetGoAsync()
    {
        try { await CircleNeuronService.StopListeningAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { Android.Util.Log.Warn(Tag, "could not stop the listener: " + ex.Message); }

        CircleNeuronService.Listener = null;

        var ears = _ears;
        _ears = null;
        if (ears is not null)
        {
            try { await ears.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Android.Util.Log.Warn(Tag, "could not dispose the listener: " + ex.Message); }
        }
    }

    /// <summary>
    /// Whether the platform is actually letting this process hear the microphone.
    /// </summary>
    /// <remarks>
    /// GRANTED IS NOT THE SAME AS ALLOWED, and from Android 12 the difference is
    /// SILENCE WITH NO ERROR. RECORD_AUDIO declares RECORD_BACKGROUND_AUDIO as its
    /// background permission, so the platform keeps its app-op at MODE_FOREGROUND -
    /// and evaluates it, per call, against what this uid is doing at that moment. A
    /// foreground service that was started from the background does not qualify: the
    /// op evaluates to MODE_IGNORED, AudioPolicyService silences the stream, and
    /// CheckSelfPermission still answers GRANTED. The recorder initialises, the loop
    /// reads zeros, IsListening is true, and the only evidence anywhere is a line in
    /// the system log we do not write.
    /// <para>
    /// unsafeCheckOpNoThrow is the same evaluation the audio stack makes, asked of
    /// our own uid, and it is the honest answer to "can it hear". Asked AFTER the
    /// start, because the answer depends on the process state the start just created.
    /// </para>
    /// <para>
    /// TRUE WHEN IT CANNOT TELL. On Android 10 there is no while-in-use restriction
    /// to fail, and a phone whose AppOps will not answer must not have a working wake
    /// word taken away from it on a guess - the measured failure is the other
    /// direction, and every refusal here is a feature switching itself off.
    /// </para>
    /// </remarks>
    private bool CanBeHeard()
    {
        // ANDROID 11 IS WHERE THE RESTRICTION STARTS, so nothing older is ever asked:
        // the P30 (API 29) has a working wake word started from the background, and a
        // check that could answer "no" on it would switch a measured, working feature
        // off on the one phone this product is calibrated against.
        if (Build.VERSION.SdkInt < BuildVersionCodes.R) return true;

        try
        {
            var ops = (Android.App.AppOpsManager?)context.GetSystemService(Context.AppOpsService);
            if (ops is null) return true;

            var mode = ops.UnsafeCheckOpNoThrow(
                Android.App.AppOpsManager.OpstrRecordAudio,
                Android.OS.Process.MyUid(),
                context.PackageName!);

            if (mode == Android.App.AppOpsManagerMode.Allowed) return true;

            Android.Util.Log.Warn(Tag, $"AppOps says record_audio is {mode} for this process");
            return false;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Info(Tag, "could not ask AppOps whether the microphone is live: " + ex.Message);
            return true;
        }
    }

    /// <inheritdoc />
    public async Task<ResidentStatus> StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ears is not null)
            {
                // THE SERVICE STOPS IT, so the wake lock is released and the shade
                // stops saying the microphone is open in the same breath. Stopping
                // the spotter behind the service's back left both behind.
                await LetGoAsync().ConfigureAwait(false);
                Android.Util.Log.Info(Tag, "stopped listening");
            }

            // A NO IS A DECISION AND HAS TO OUTLIVE THE PROCESS - WHETHER OR NOT
            // ANYTHING WAS LISTENING WHEN IT WAS MADE.
            //
            // This sat inside the "if" above, and WasRunning defaults to YES, so an
            // "off" given while nothing happened to be listening wrote nothing at all
            // and the default won. That is not a corner: on a fresh install the
            // microphone is not granted for the first few seconds, so the switch is
            // off, and somebody who turns it off again - or turns it off before the
            // bundle has downloaded - was recorded as having asked for it. Every
            // later path then trusted that value: the resume at process start, the
            // grant in MicrophoneActivity, the fetch's landed hook. The owner said no
            // and the phone started listening anyway.
            Remember(false);

            // AND NOTHING IS WAITING FOR A SCREEN ANY MORE. Left set, the shade would
            // keep inviting somebody to switch on the thing they just switched off.
            CircleNeuronService.WaitingToBeAsked = false;
            CircleNeuronService.WaitingBecause   = null;
            CircleNeuronService.RefreshNotification();

            // THE SERVICE STAYS UP. It hosts the brain as well as the microphone, and
            // stopping the wake word is not a reason to unload a model every other
            // app on the link is using.
            return Off();
        }
        catch (Exception ex)
        {
            return new ResidentStatus(ResidentState.Failed, "It could not stop listening", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// RESUMING IS NOT STARTING, AND THIS USED TO BE THE SAME METHOD. The interface
    /// says resuming "restores a choice; it does not make one" and returns Off when
    /// the owner had it off - and this forwarded straight to StartAsync, which reads
    /// no preference and writes `true` on its way out.
    /// <para>
    /// So every launch of the app switched listening back on. MainLayout calls
    /// ResumeAsync on first render, it crossed the link as ResidentStart because
    /// there was no resume verb, the service started the microphone and recorded the
    /// owner as wanting it - while the Waking checkbox, which reads the stored
    /// setting, still showed off. An explicit off survived exactly until the next
    /// time the app was opened.
    /// </para>
    /// </remarks>
    public Task<ResidentStatus> ResumeAsync(CancellationToken ct = default)
    {
        if (!Wanted())
        {
            Android.Util.Log.Info(Tag, "not resuming: the owner turned it off");
            return Task.FromResult(Off());
        }

        return StartAsync(false, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ASKED, NOT REMEMBERED. The loop can end without anyone calling Stop — the
    /// microphone is revoked, the OS reclaims the service, the spotter throws — so
    /// this reads the loop rather than a flag set when it was started.
    /// <para>
    /// AND "NOT LISTENING" WAS NOT THE WHOLE TRUTH. On by default and never given
    /// the microphone, this said Off - so Settings showed a switch nobody had
    /// touched, off, inviting "Turn it on to wake it by name", when the owner had
    /// it on and the only thing missing was one Allow. Off is for an owner who
    /// switched it off; a wanted listener with no microphone says what it needs.
    /// </para>
    /// <para>
    /// THREE REASONS, NOT ONE. A wanted listener that is not running is waiting for
    /// the microphone, for its bundle, or for a visible screen to open the microphone
    /// in - and "Turn it on to wake it by name" is wrong for all three. The switch in
    /// Settings shows this hint, which is the only place a person is told why.
    /// </para>
    /// </remarks>
    public Task<ResidentStatus> RefreshAsync(CancellationToken ct = default)
    {
        if (IsListening) return Task.FromResult(Listening());
        if (!Wanted()) return Task.FromResult(Off());
        if (!MicrophoneAllowed()) return Task.FromResult(NeedsMicrophone());
        if (BundleDir() is null) return Task.FromResult(NotDownloaded());
        return Task.FromResult(WaitingForAScreen());
    }

    /// <summary>Whether the owner has listening on. Defaults to yes; an explicit no is obeyed.</summary>
    private bool Wanted()
    {
        try { return ResidentPrefs.WasRunning(context); }
        catch (Exception ex)
        {
            // A PREFERENCE THAT CANNOT BE READ IS NOT A NO. Answering to its name is
            // the product, and the default is on; a SharedPreferences file that will
            // not open must not turn the feature off silently.
            Android.Util.Log.Warn(Tag, "could not read whether the owner had it on: " + ex.Message);
            return true;
        }
    }

    private void Remember(bool running)
    {
        try { ResidentPrefs.SetRunning(context, running); }
        catch (Exception ex) { Android.Util.Log.Warn(Tag, "could not record it: " + ex.Message); }
    }

    private bool MicrophoneAllowed() =>
        context.CheckSelfPermission(Android.Manifest.Permission.RecordAudio)
            == Android.Content.PM.Permission.Granted;

    private static ResidentStatus NeedsMicrophone() => new(
        ResidentState.NeedsPermission, MicrophoneAsk.Needed, MicrophoneAsk.NeededHint);

    private static ResidentStatus WaitingForAScreen() => new(
        ResidentState.NeedsForeground, MicrophoneAsk.NeedsForeground, MicrophoneAsk.NeedsForegroundHint);

    private static ResidentStatus NotDownloaded() => new(
        ResidentState.Unsupported,
        "The wake word is not downloaded",
        "It is being fetched; it starts listening on its own when it arrives.");

    private static ResidentStatus Listening() => new(
        ResidentState.Listening,
        "Listening",
        "It answers to its name with the screen off. " + MicrophoneAsk.SoundNotKept);

    private static ResidentStatus Off() => new(
        ResidentState.Off,
        "Not listening",
        "Turn it on to wake it by name.");
}
