// HomeActivity.cs
//
// The first three seconds.
//
// This screen used to be a scrolling console: a wall of explanation, a row of
// buttons named after internal concepts, and a log. Nobody decides to try an
// assistant by reading about one — you find out what it is by hearing it. So
// there is one large thing to press, and pressing it makes the phone talk. The
// claims underneath are three short lines, because a person who has just heard
// it speak Yoruba does not need a paragraph.
//
// VOICE IS THE PRODUCT; TYPING IS THE FALLBACK. It read the other way round: the
// loudest control was "Ask it something", which opened a text box — the ChatGPT
// shape, where the assistant is a thing you write to. But the assistants people
// actually live with are spoken to. Nobody types at Alexa. So the circle IS the
// assistant now, pressing it talks to it, and the text box is a quiet line at the
// bottom for when speaking aloud is not on.
//
// IT NEVER SAID WHETHER IT WAS READY. A finished-looking screen that does nothing
// for half a minute, and no way to tell the difference between thinking and
// broken. Measured on the P30 with everything downloaded: 35 seconds from launch
// to the first answer, because readiness was one gate that waited on the 433 MB
// brain. It is now staged — see Readiness — so the circle comes alive as soon as
// it can HEAR and SPEAK, which is a second or two, and says so in words.
//
// Everything else — the capability probe, the vision demo — is one tap away and
// none of it competes for this screen.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Views.Animations;
using Android.Widget;

namespace CircleAI.Samples.It.Mobile;

/// <summary>Which part of an exchange the mark is showing.</summary>
/// <remarks>
/// Four states and no more. Every one of them is something a person can see the
/// difference between without being taught: nothing happening, it is hearing me,
/// it is working, it is talking. A fifth would be a distinction only the person
/// who wrote it can perceive.
/// </remarks>
public enum MarkState { Idle, Listening, Thinking, Speaking }

[Activity(Label = "Circle AI",   // the launcher name, under the icon
          Icon = "@mipmap/ic_launcher",
          RoundIcon = "@mipmap/ic_launcher_round",
          MainLauncher = true,
          NoHistory = false)]
public class HomeActivity : Activity
{
    /// <summary>
    /// What the phone says when you press the circle, in order.
    /// </summary>
    /// <remarks>
    /// Deliberately not English first. The point being made is that this thing
    /// speaks languages other assistants do not, so the very first sound it makes
    /// should be one of them. isiZulu leads because the eleven-language South
    /// African voice is the one that exists nowhere else.
    /// </remarks>
    static readonly (string Tag, string Label, string Phrase)[] Greetings =
    {
        ("zu",  "isiZulu",    "Sawubona. Ngingakusiza ngani namuhla?"),
        ("sw",  "Kiswahili",  "Habari. Nikusaidie nini leo?"),
        ("yo",  "Yorùbá",     "Pẹlẹ o. Kí ni mo lè ṣe fún ọ?"),
        ("hi",  "हिन्दी",       "नमस्ते। मैं आपकी क्या मदद कर सकता हूँ?"),
        ("ar",  "العربية",     "مرحبا. كيف يمكنني مساعدتك اليوم؟"),
        ("pt",  "Português",  "Olá. Como posso ajudar você hoje?"),
    };

    MarkView _mark = null!;
    TextView _prompt = null!;
    TextView _caption = null!;
#if IT_VOICE_ANDROID
    /// <summary>Reports the language the last turn was answered in. Not a control.</summary>
    TextView? _lang;
#endif
    int _next;
    CancellationTokenSource? _speaking;

    /// <summary>Non-null while first-run setup is downloading. Also the paint lock.</summary>
    CancellationTokenSource? _setup;

    /// <summary>The download bar. Hidden unless something is actually arriving.</summary>
    ProgressBar? _bar;

    /// <summary>Whether the spoken welcome has already been given this run.</summary>
    bool _welcomed;

    // The tour that fills a long download. See SetupTour.
    LinearLayout? _tour;
    TextView? _tourTitle;
    TextView? _tourBody;
    Button?   _tourAction;
    TextView? _tourNext;
    IReadOnlyList<TourStep> _tourSteps = Array.Empty<TourStep>();
    int _tourAt = -1;

    /// <summary>
    /// A one-off message that outlives the next readiness repaint, or null.
    /// </summary>
    /// <remarks>
    /// Readiness re-runs the moment setup ends, so a failure written straight to
    /// the labels survived about a tenth of a second before being overwritten by
    /// "Let's set it up" — leaving the person who just watched a download die with
    /// the same screen they started on and no idea why.
    /// </remarks>
    (string Headline, string Caption)? _note;

    /// <summary>Permission request codes. 1003 is the talk button's, already taken.</summary>
    const int SetupMicRequest = 1004;

    Readiness _ready = new(ReadyStage.Waking, "Getting ready", "", false);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActionBar?.Hide();
        // The process wiring — the phonemizer factory AND the platform memory probe
        // — now happens in ItApplication.OnCreate, which runs before any activity on
        // every entry path. This screen used to install the voice half itself and
        // never knew about the memory half, so the launcher was measuring the GC
        // heap and concluding the phone could not run anything.
        BuildUi();
        _ = CheckReadyAsync();
    }

    protected override void OnResume()
    {
        base.OnResume();
        // Re-checked on every return, because someone may have just turned
        // something on in the abilities screen and come straight back here
        // expecting the circle to be alive.
        _ = CheckReadyAsync();
    }

    /// <summary>
    /// Works out what it can do right now and says so.
    /// </summary>
    /// <remarks>
    /// A FILESYSTEM CHECK, NOT A MODEL LOAD, so it answers in milliseconds. The
    /// old screen had no readiness notion at all and the chat screen found out by
    /// loading the brain — 35 seconds. What a person needs to know first is not
    /// "has the 433 MB model finished initialising" but "will pressing this do
    /// anything", and that is answerable from what is on disk.
    /// </remarks>
    async Task CheckReadyAsync()
    {
        try
        {
            // Read on the UI thread and captured, so the worker never touches an
            // Activity context.
            var declined = SetupPrefs.Declined(this);

            var next = await Task.Run(() =>
            {
                var store = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                    "CircleAI", "Models");

                using var registry = new CircleAI.Core.Models.ModelRegistryService();
                using var loader = new CircleAI.Inference.BundleModelLoader(store, registry);

                bool Has(CircleAI.Core.ModelModality m) => registry.AllModels
                    .Where(e => e.Modality == m)
                    .Any(e => loader.ModelExists(e.Name));

                var voice = Has(CircleAI.Core.ModelModality.Tts);
                var ears  = Has(CircleAI.Core.ModelModality.Asr);
                var brain = Has(CircleAI.Core.ModelModality.Chat);

#if IT_VOICE_ANDROID
                // The wake bundle is found the same way the wake-word screen finds
                // it, so the two can never disagree about whether it is there.
                var bundle = WakeWordActivity.FindBundle(this);
                const bool speech = true;
#else
                // The chat-only APK has no speech stack at all, so there is nothing
                // to listen with and the screen must not offer to.
                string? bundle = null;
                const bool speech = false;
#endif
                // WHAT IS STILL MISSING, counted in the pass that is already
                // walking the disk. Computing it here rather than in SetupAsync is
                // what stops the auto-finish below from looping: setup ends by
                // re-running this check, and a version that asked "is there a plan?"
                // by calling setup would call setup forever once the plan was empty.
                var pending = CircleAI.Samples.It.FirstRun.Plan(
                    registry, loader, CircleAI.Core.DeviceProbe.Snapshot(),
                    speech, declined.Contains).Count;

                return (voice, ears, brain, bundle, pending);
            });

            RunOnUiThread(() =>
            {
#if IT_VOICE_ANDROID
                // THE HEADLINE HAS TO BE TRUE. A bundle on disk is not a phone that
                // is listening: without the microphone permission nothing opens, so
                // promising "Say Hey B" there would be a lie the person discovers by
                // talking to something deaf. Permission is knowable only here, which
                // is why the readiness line is built here and not in the worker.
                var mic = CheckSelfPermission(Android.Manifest.Permission.RecordAudio)
                          == Android.Content.PM.Permission.Granted;
                var canWake = next.bundle is not null && next.voice && next.ears && mic;
#else
                const bool canWake = false;
#endif
                var anything = next.voice || next.ears || next.brain;
                Apply(Readiness.From(next.voice, next.ears, next.brain, anything, canWake));

                // FINISH WHAT WAS STARTED. Setup only ran when NOTHING was
                // installed, so a phone that got most of the way — an interrupted
                // download, or an upgrade from the chat-only build, which never
                // fetched a wake bundle — stayed permanently half-provisioned with
                // no route out. Nothing on this screen was wrong; it simply said
                // "Tap and talk" forever and never mentioned that hands-free
                // existed and was missing.
                //
                // Only once something is installed, because that means somebody
                // already agreed to download models on this phone. A virgin
                // install still waits to be asked. Declines are remembered, so
                // "Turn off" is not quietly undone (see SetupPrefs).
                if (anything && next.pending > 0 && _setup is null && _note is null)
                    _ = SetupAsync();

#if IT_VOICE_ANDROID
                // Warm the transcriber the moment it exists on disk, not on the
                // first sentence somebody speaks — see WarmEars.
                if (next.ears) WarmEars();

                // And the brain, for the same reason: its load is the largest
                // single wait in a turn, and it has no business being inside one.
                // Started only once the model is actually on disk, so a phone
                // still downloading is not asked to load a file that is half
                // there.
                if (next.brain && _session is null && _brainLoading is null)
                    _ = WarmBrainAsync();

                if (canWake) StartHandsFree(next.bundle!);
                else _ = StopHandsFreeAsync();
#endif
            });
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("CircleAI.It", "readiness check failed: " + ex);
        }
    }

    void Apply(Readiness r)
    {
        _ready = r;

        // STATE ALWAYS, PAINT ONLY WHEN NOTHING ELSE OWNS THE WORDS. Readiness is
        // re-checked whenever a download finishes a part, and it would otherwise
        // overwrite "the brain — 41%" with its own headline a few times a minute.
        // The state still updates underneath, so the tap does the right thing the
        // instant setup ends.
        if (_setup is not null) { _mark.SetBusy(true); return; }

        var (headline, caption) = _note ?? (r.Headline, r.Caption);
        _prompt.Text = headline;
        _caption.Text = caption;
        _caption.Visibility = string.IsNullOrEmpty(caption) ? ViewStates.Gone : ViewStates.Visible;

        // The circle keeps breathing until it can actually be used, so "alive"
        // and "usable" are the same signal rather than two things to reconcile.
        _mark.SetBusy(!r.CanTalk);
    }

    // ── first run ────────────────────────────────────────────────────────────

    /// <summary>Asks for the microphone, then fetches what this phone needs.</summary>
    /// <remarks>
    /// THE MICROPHONE IS ASKED FOR FIRST AND THE ANSWER DOES NOT GATE THE
    /// DOWNLOAD. Asking first means the one dialog a new person sees arrives while
    /// they are still deciding to try this, rather than four minutes later when
    /// they finally press the circle and get a permission sheet instead of an
    /// answer. Refusing is a legitimate choice — the typed path works without a
    /// microphone — so a no still downloads everything and simply leaves the wake
    /// word unable to start.
    /// </remarks>
    void StartSetup()
    {
#if IT_VOICE_ANDROID
        if (CheckSelfPermission(Android.Manifest.Permission.RecordAudio)
            != Android.Content.PM.Permission.Granted)
        {
            RequestPermissions([Android.Manifest.Permission.RecordAudio], SetupMicRequest);
            return;   // resumed in OnRequestPermissionsResult, granted or not
        }
#endif
        _ = SetupAsync();
    }

    /// <inheritdoc/>
    public override void OnRequestPermissionsResult(
        int requestCode, string[] permissions, Android.Content.PM.Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        // The download runs either way — see StartSetup.
        if (requestCode == SetupMicRequest) { _ = SetupAsync(); return; }

        // 1003 is the talk button asking for the microphone. Granting it makes the
        // phone able to WAKE, and readiness is what starts the wake loop — without
        // this the person granted permission and then had to work out for themselves
        // that the screen would only change if they left it and came back.
        if (requestCode == 1003) _ = CheckReadyAsync();
    }

    /// <summary>
    /// Says hello out loud, as soon as it can, while the rest is still arriving.
    /// </summary>
    /// <remarks>
    /// THE WAIT IS THE ONE MOMENT IT HAS SOMEBODY'S ATTENTION, and on a slow link
    /// that moment is three quarters of an hour long. Setup fetches the voice
    /// first on purpose — it is about 110 MB against the brain's many gigabytes —
    /// so the phone can speak within a minute or two on almost any connection,
    /// and everything after that is a wait it can fill itself.
    /// <para>
    /// NOT FILLER. It says the two things a new person actually needs — that they
    /// can talk to it, and that it is still getting ready — in a real voice, in
    /// one of the languages it exists to speak. A tutorial delivered by the
    /// product demonstrating itself is worth more than a progress screen, and it
    /// costs nothing extra: those bytes were already on the phone.
    /// </para>
    /// <para>
    /// Once per run, and never in place of the bar. Somebody who has seen this
    /// before should not be talked at again every time they open the app.
    /// </para>
    /// </remarks>
    async Task OfferWelcomeAsync()
    {
#if IT_VOICE_ANDROID
        if (_welcomed) return;

        try
        {
            var store = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "CircleAI", "Models");

            // Only once the voice is genuinely on disk. Asking it to speak
            // before then produces silence, which reads as broken at exactly
            // the moment the product is trying to prove it is not.
            var ready = await Task.Run(() =>
            {
                using var registry = new CircleAI.Core.Models.ModelRegistryService();
                using var loader = new CircleAI.Inference.BundleModelLoader(store, registry);
                return registry.AllModels
                    .Where(e => e.Modality == CircleAI.Core.ModelModality.Tts)
                    .Any(e => loader.ModelExists(e.Name));
            });
            if (!ready) return;

            _welcomed = true;

            // The device's own language when it is one we speak, else English.
            // Somebody in Soweto should not be welcomed in a language they did
            // not choose because the catalogue happened to be alphabetical.
            var tag  = WelcomeTag();
            var line = WelcomeLine(tag);
            var wav  = System.IO.Path.Combine(FilesDir!.AbsolutePath, "welcome.wav");

            // The same path the greeting carousel uses, so there is one way to
            // make this phone speak and not two that drift apart.
            var report = await CircleAI.Samples.It.Voice.ItTtsProbe.RunCataloguedAsync(
                store, tag, line, wav, _ => { }, _setup?.Token ?? CancellationToken.None);

            if (System.IO.File.Exists(wav) &&
                report.Contains("SYNTHESIS OK", StringComparison.Ordinal))
            {
                await MainActivity.PlayWavStaticAsync(wav);
            }
        }
        catch (Exception ex)
        {
            // A welcome that cannot be spoken is not a failure of setup.
            Android.Util.Log.Info("CircleAI.It", "welcome skipped: " + ex.Message);
        }
#else
        await Task.CompletedTask;
#endif
    }

    /// <summary>
    /// Decides what the person can usefully do while the rest downloads.
    /// </summary>
    /// <remarks>
    /// Driven off the LIVE ETA rather than a guess, so the same build shows a
    /// full setup flow on a P30 over 48 Mbps and nothing at all on a phone where
    /// the brain lands before anybody finished reading. Re-evaluated only while
    /// no step is on screen — a tour that reshuffles under somebody mid-read is
    /// worse than one that is slightly out of date.
    /// </remarks>
    void OfferTour(TimeSpan remaining, bool voice, bool wake)
    {
        if (_tour is null) return;

        // THE TOUR GROWS AS THE PHONE DOES, and getting this wrong hid the best
        // card entirely. Steps are gated on what has landed; when the tour is
        // first offered only the voice-free ones qualify, because the voice is
        // still two minutes away. Computing it once meant "Build your CV" — which
        // needs the voice to ask questions out loud, and is the whole reason the
        // tour exists — could never appear, no matter how long the download ran.
        //
        // So it is recomputed as each part arrives, and only ADDED to: the step
        // somebody is reading is never replaced underneath them, and steps they
        // have already passed do not come back.
        var fresh = SetupTour.For(remaining, voice, wake);
        if (fresh.Count == 0) return;

        var seen  = _tourSteps.Take(Math.Max(0, _tourAt + 1)).Select(s => s.Title).ToHashSet();
        var added = fresh.Where(s => !seen.Contains(s.Title)).ToList();
        if (added.Count == 0) return;

        // Everything already read, then everything newly possible.
        _tourSteps = _tourSteps.Take(Math.Max(0, _tourAt + 1)).Concat(added).ToList();

        // Nothing on screen — either the tour has not started or the person
        // finished it before the interesting steps became available.
        if (_tourAt < 0 || _tourAt >= _tourSteps.Count - added.Count)
            ShowTourStep(_tourSteps.Count - added.Count);
    }

    /// <summary>
    /// Asks the phone to stop killing the assistant in the background.
    /// </summary>
    /// <remarks>
    /// NO CODE FIXES THIS — only the owner can. Huawei, Xiaomi, Oppo and Vivo
    /// each stop foreground services on their own schedule regardless of what
    /// Android permits, and the phone does not tell anybody it has done it. The
    /// assistant simply stops answering an hour after it is put down, which
    /// reads as our bug and is not one.
    /// <para>
    /// Two requests, because they are two different switches: the AOSP battery
    /// optimisation exemption, which is a real dialog, and the vendor's own
    /// protected-apps list, which is a settings screen the app can only open —
    /// it cannot be granted programmatically, by design.
    /// </para>
    /// <para>
    /// Every intent here is tried and allowed to fail. These activities differ
    /// per vendor and per firmware, and an assistant that crashes trying to ask
    /// for permission to keep running is worse than one that quietly cannot.
    /// </para>
    /// </remarks>
    void AskToKeepRunning()
    {
        // The standard one first. On phones that honour it, this is the whole fix.
        try
        {
            var pm = (Android.OS.PowerManager?)GetSystemService(PowerService);
            if (pm is not null && !pm.IsIgnoringBatteryOptimizations(PackageName))
            {
                var intent = new Intent(
                    Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations,
                    Android.Net.Uri.Parse("package:" + PackageName));
                StartActivity(intent);
                return;
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Info("CircleAI.It", "battery exemption unavailable: " + ex.Message);
        }

        // Then the vendor's own list. Huawei first — it is the phone this was
        // built and measured on, and the one most likely to kill the service.
        foreach (var (pkg, cls) in new[]
        {
            ("com.huawei.systemmanager", "com.huawei.systemmanager.startupmgr.ui.StartupNormalAppListActivity"),
            ("com.huawei.systemmanager", "com.huawei.systemmanager.optimize.process.ProtectActivity"),
            ("com.miui.securitycenter",  "com.miui.permcenter.autostart.AutoStartManagementActivity"),
            ("com.coloros.safecenter",   "com.coloros.safecenter.permission.startup.StartupAppListActivity"),
            ("com.vivo.permissionmanager","com.vivo.permissionmanager.activity.BgStartUpManagerActivity"),
        })
        {
            try
            {
                var intent = new Intent();
                intent.SetComponent(new ComponentName(pkg, cls));
                intent.SetFlags(ActivityFlags.NewTask);
                StartActivity(intent);
                return;
            }
            catch { /* not this vendor, or not this firmware */ }
        }

        // Nothing to open. Say so rather than leaving a button that does nothing.
        _note = ("Keep it running",
                 "Find Circle AI in your battery settings and allow it to run in the background.");
        Apply(_ready);
    }

    /// <summary>Which speech parts are on disk right now.</summary>
    /// <remarks>
    /// Asked of the filesystem, not of readiness: readiness also weighs the
    /// microphone permission, and a tour step that teaches somebody to grant
    /// that permission must not be hidden because they have not granted it yet.
    /// </remarks>
    (bool Voice, bool Wake) SpeechOnDisk()
    {
        try
        {
            var store = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "CircleAI", "Models");

            using var registry = new CircleAI.Core.Models.ModelRegistryService();
            using var loader = new CircleAI.Inference.BundleModelLoader(store, registry);

            var voice = registry.AllModels
                .Where(e => e.Modality == CircleAI.Core.ModelModality.Tts)
                .Any(e => loader.ModelExists(e.Name));

#if IT_VOICE_ANDROID
            var wake = WakeWordActivity.FindBundle(this) is not null;
#else
            const bool wake = false;
#endif
            return (voice, wake);
        }
        catch { return (false, false); }
    }

    /// <summary>Puts one step on screen, or ends the tour.</summary>
    void ShowTourStep(int index)
    {
        if (_tour is null) return;

        if (index < 0 || index >= _tourSteps.Count)
        {
            // Out of steps, or skipped past the end. The bar is still there and
            // still honest; there is simply nothing left worth doing.
            _tourAt = int.MaxValue;
            _tour.Visibility = ViewStates.Gone;
            return;
        }

        _tourAt = index;
        var step = _tourSteps[index];

        _tourTitle!.Text = step.Title;
        _tourBody!.Text  = step.Body;
        _tourNext!.Text  = index == _tourSteps.Count - 1 ? "Done" : "Skip";

        if (string.IsNullOrEmpty(step.Action))
        {
            // A step with nothing to press is something to read. Hiding the
            // button rather than showing a dead one.
            _tourAction!.Visibility = ViewStates.Gone;
        }
        else
        {
            _tourAction!.Visibility = ViewStates.Visible;
            _tourAction.Text = step.Action;
        }

        _tour.Visibility = ViewStates.Visible;
    }

    /// <summary>Runs the current step's action, then moves on.</summary>
    /// <remarks>
    /// Each action is an EXISTING screen, not a copy of one built for the tour.
    /// The language picker and the wake-word screen are the same ones reachable
    /// from the finished app, so nobody is taught a flow that disappears once
    /// setup ends.
    /// </remarks>
    void RunTourAction()
    {
        if (_tourAt < 0 || _tourAt >= _tourSteps.Count) return;
        var step = _tourSteps[_tourAt];

        switch (step.Title)
        {
            case "Your language":
                StartActivity(new Intent(this, typeof(LanguagePickerActivity)));
                break;

            case "Let it hear you":
                if (CheckSelfPermission(Android.Manifest.Permission.RecordAudio)
                    != Android.Content.PM.Permission.Granted)
                    RequestPermissions([Android.Manifest.Permission.RecordAudio], SetupMicRequest);
                break;

            case "Keep it awake":
                AskToKeepRunning();
                break;

            case "Build your CV while you wait":
                StartActivity(new Intent(this, typeof(CareerActivity)));
                break;

#if IT_VOICE_ANDROID
            case "Say “Hey B”":
                StartActivity(new Intent(this, typeof(WakeWordActivity)));
                break;
#endif
        }

        // Advanced immediately rather than on return: the person has been sent
        // somewhere, and coming back to the card they just acted on reads as
        // though the action did not take.
        ShowTourStep(_tourAt + 1);
    }

    /// <summary>The phone's own language, when it is one we can speak.</summary>
    /// <remarks>
    /// Falls back to English rather than to the first entry in a list — being
    /// greeted in a language nobody in the room speaks is worse than English,
    /// which at least reads as a default rather than as a mistake.
    /// </remarks>
    static string WelcomeTag()
    {
        try
        {
            var tag = Java.Util.Locale.Default?.Language?.ToLowerInvariant() ?? "en";
            return tag is "zu" or "xh" or "af" or "st" or "sw" ? tag : "en";
        }
        catch { return "en"; }
    }

    /// <summary>What it says while the rest downloads, in the phone's language.</summary>
    static string WelcomeLine(string tag) => tag switch
    {
        "zu" => "Sawubona. Ngiyalanda okusele. Ungakhuluma nami manje.",
        "xh" => "Molo. Ndisalanda okuseleyo. Ungathetha nam ngoku.",
        "af" => "Hallo. Ek laai nog die res af. Jy kan nou al met my praat.",
        "st" => "Dumela. Ke sa jarolla tse ling. O ka bua le nna hona joale.",
        "sw" => "Habari. Bado ninapakua sehemu iliyobaki. Unaweza kuzungumza nami sasa.",
        _    => "Hello. I am still downloading the rest, but you can talk to me now.",
    };

    /// <summary>Downloads the plan, narrating it where the readiness line goes.</summary>
    async Task SetupAsync()
    {
        if (_setup is not null) return;

        var cts = new CancellationTokenSource();
        _setup = cts;
        _note  = null;   // trying again clears whatever went wrong last time
        _prompt.Text = "Setting it up";
        _caption.Text = "Working out what this phone needs…";
        _caption.Visibility = ViewStates.Visible;
        _mark.SetBusy(true);

        try
        {
            var store = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "CircleAI", "Models");

            using var registry = new CircleAI.Core.Models.ModelRegistryService();
            using var loader = new CircleAI.Inference.BundleModelLoader(store, registry);

            // WHAT THIS BUILD CAN ACTUALLY DO, not what the catalogue offers. The
            // chat-only APK has no speech stack compiled in, so fetching a voice,
            // ears and a wake bundle there spends 140 MB of somebody's data on
            // files nothing in the binary can open.
#if IT_VOICE_ANDROID
            const bool speech = true;
#else
            const bool speech = false;
#endif
            var probe = CircleAI.Core.DeviceProbe.Snapshot();
            var declined = SetupPrefs.Declined(this);
            var steps = await Task.Run(
                () => CircleAI.Samples.It.FirstRun.Plan(
                    registry, loader, probe, speech, declined.Contains), cts.Token);

            if (steps.Count == 0)
            {
                // Nothing to fetch and yet nothing installed: every model in the
                // catalogue was refused by the fit check. That is a real answer and
                // it belongs on screen, not in a log.
                _note = ("This phone is too small",
                         $"Nothing in the catalogue fits {probe.UsableRamGb:0.#} GB of memory.");
                return;
            }

            // HOW BIG THIS IS, BEFORE A BYTE MOVES. On a metered link 22.8 GB is
            // a spending decision, not a wait, and it is not ours to make quietly.
            var totalBytes = steps.Sum(s => s.Model.TotalBytes);
            _prompt.Text = "Setting it up";
            _caption.Text = $"{totalBytes / 1e9:0.0} GB to download";

            _bar!.Visibility = ViewStates.Visible;
            _bar.Progress = 0;

            var lastStep = -1;
            var lastOffered = -1;
            var progress = new Progress<CircleAI.Samples.It.SetupProgress>(p => RunOnUiThread(() =>
            {
                // The whole line: what is arriving, how fast, and when it ends.
                // See SetupProgress.Describe — the wait is minutes on one phone
                // and most of an hour on another, and a bare percentage is only
                // honest on the fast one.
                _caption.Text = p.Describe();
                _bar.Progress = (int)(Math.Clamp(p.Fraction, 0f, 1f) * 1000);

                // Each finished part makes the phone able to do something new, and
                // the wake loop only starts when readiness notices the bundle
                // arrive. Re-checking on the step boundary is what makes it possible
                // to start talking to it while the brain is still downloading.
                if (p.Index != lastStep)
                {
                    lastStep = p.Index;
                    _ = CheckReadyAsync();
                    _ = OfferWelcomeAsync();
                }

                // WHAT THERE IS TIME FOR, from the rate actually being achieved.
                // Offered on the step boundary rather than every report, so the
                // card cannot appear and vanish as the estimate wobbles — and
                // only once each part lands, which is what makes its own step
                // safe to demonstrate.
                if (p.Index != lastOffered && p.Remaining > TimeSpan.Zero)
                {
                    lastOffered = p.Index;
                    var done = _tourSteps;   // keep a stable view for the check below
                    _ = Task.Run(() =>
                    {
                        var (voice, wake) = SpeechOnDisk();
                        RunOnUiThread(() => OfferTour(p.Remaining, voice, wake));
                    });
                }
            }));

            await CircleAI.Samples.It.FirstRun.RunAsync(loader, steps, progress, cts.Token);
        }
        catch (System.OperationCanceledException)
        {
            // Stopped on purpose. Whatever landed stays; the plan resumes from there.
            // Qualified: Android.OS has an OperationCanceledException of its own and
            // the unqualified name is ambiguous in an activity.
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("CircleAI.It", "setup failed: " + ex);
            _note = ("That did not finish",
                     ex is System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException
                         ? "Check the connection and tap to try again."
                         : "Tap to try again.");
        }
        finally
        {
            _setup = null;
            _mark.SetBusy(false);
            if (_bar is not null) _bar.Visibility = ViewStates.Gone;
            if (_tour is not null) _tour.Visibility = ViewStates.Gone;
            await CheckReadyAsync();

            // IF ENOUGH LANDED THAT IT CAN TALK, THAT IS THE MORE USEFUL TRUTH than
            // the failure. Setup fetches the brain last, so the common partial
            // failure is a phone that can hear and speak but not think yet — and
            // telling that person "That did not finish" while hiding "Tap and talk"
            // buries the thing they can actually do. The missing part is finishable
            // from the abilities screen, and readiness keeps saying it is missing.
            if (_note is not null && _ready.CanTalk) { _note = null; Apply(_ready); }
        }
    }

    void BuildUi()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Ui.Bg);
        root.SetGravity(GravityFlags.CenterHorizontal);

        var pad = Ui.Dp(this, 24);

        // Wordmark, small. The product name is not the pitch.
        // "Circle AI", with the space. Set solid, the two capitals collide —
        // "CircleAI" reads as one long word with a stutter in the middle, and at a
        // glance the eye lands on "leAI". The same fix the voice needed: the
        // synthesiser said it as one mangled word until it was written apart.
        // A product name has to survive being seen quickly and said aloud.
        var name = Ui.Label(this, "Circle AI", 18f, Ui.InkSoft, bold: true);
        name.SetPadding(pad, Ui.Dp(this, 28), pad, 0);
        name.Gravity = GravityFlags.Center;
        root.AddView(name, Ui.Fill());

        // ── the thing you press ──────────────────────────────────────────
        _mark = new MarkView(this);
        var markSize = Ui.Dp(this, 200);
        var markLp = new LinearLayout.LayoutParams(markSize, markSize);
        markLp.TopMargin = Ui.Dp(this, 40);
        markLp.Gravity = GravityFlags.CenterHorizontal;
        // PRESSING THE CIRCLE TALKS TO IT. That is the product, and it is the only
        // large control on the screen. Before it can listen, pressing it makes it
        // say hello in one of the catalogued languages instead of doing nothing — because
        // "nothing happens" is indistinguishable from "broken", and hearing it
        // speak is the fastest way to understand what this is.
        _mark.Clickable = true;
        _mark.Click += (s, e) =>
        {
            // A DOWNLOAD IS ALREADY RUNNING. Not a greeting and not a turn: the
            // caption is saying what it is fetching, and a stray tap must not throw
            // away four minutes of somebody's data.
            // EVERY EXIT SAYS WHICH ONE IT TOOK. A tap that does nothing visible has
            // four possible explanations here and the log distinguished none of
            // them, so "it stalls" could not be turned into a cause — three
            // hypotheses were reasoned out and all three were wrong. A press is a
            // deliberate act by a person; it should never be silent to us.
            Android.Util.Log.Info("CircleAI.It",
                $"tap: setup={(_setup is not null)} stage={_ready.Stage} canTalk={_ready.CanTalk}");

            if (_setup is not null)
            {
                Android.Util.Log.Info("CircleAI.It", "tap -> ignored (setup running)");
                return;
            }

            // "TAP TO START" HAS TO START SOMETHING. CanTalk is false for two very
            // different reasons and this line used to treat them as one: parts still
            // ARRIVING (a greeting is right — it says "alive, nearly there"), and
            // NOTHING INSTALLED AT ALL, where nothing is coming and the greeting is
            // the whole of what happens. A fresh install had no path to a working
            // assistant anywhere on this screen while the screen offered one.
            if (_ready.Stage == ReadyStage.NeedsSetup)
            {
                Android.Util.Log.Info("CircleAI.It", "tap -> StartSetup");
                StartSetup();
                return;
            }

            if (!_ready.CanTalk)
            {
                Android.Util.Log.Info("CircleAI.It", "tap -> SpeakNext (cannot talk yet)");
                SpeakNext();
                return;
            }
#if IT_VOICE_ANDROID
            Android.Util.Log.Info("CircleAI.It", "tap -> TalkOnce");
            TalkOnce();
#else
            var talk = new Intent(this, typeof(MainActivity));
            talk.PutExtra(MainActivity.StartListeningExtra, true);
            StartActivity(talk);
#endif
        };
        root.AddView(_mark, markLp);

        // The headline is set by Readiness, not hard-coded, so the screen can
        // never claim to be usable before it is — the exact failure this replaces.
        _prompt = Ui.Label(this, "Getting ready", 20f, Ui.Ink, bold: true);
        _prompt.Gravity = GravityFlags.Center;
        _prompt.SetPadding(pad, Ui.Dp(this, 28), pad, 0);
        root.AddView(_prompt, Ui.Fill());

        _caption = Ui.Label(this, "You can talk to it in a moment.", 15f, Ui.InkSoft);
        _caption.Gravity = GravityFlags.Center;
        _caption.SetPadding(pad, Ui.Dp(this, 8), pad, 0);
        root.AddView(_caption, Ui.Fill());

        // A BAR, BECAUSE THE WAIT IS NOT ONE LENGTH. Hidden until something is
        // actually downloading, so the finished state stays as quiet as it was.
        // On a fast phone it barely appears; on a P30 over 48 Mbps it is the
        // difference between a screen that is working and a screen that is stuck.
        _bar = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal)
        {
            Max = 1000,
            Indeterminate = false,
            Visibility = ViewStates.Gone,
        };
        var barLp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Ui.Dp(this, 6));
        barLp.SetMargins(pad * 2, Ui.Dp(this, 16), pad * 2, 0);
        root.AddView(_bar, barLp);

        // THE WAIT, SPENT. See SetupTour: on a slow link this is forty-five
        // minutes of setup somebody has to do anyway, and on a fast one it never
        // appears at all. Built here and hidden, so the finished screen is the
        // same screen it always was.
        _tour = new LinearLayout(this) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
        _tour.Background = Ui.Rounded(this, Ui.Surface, 14f);
        _tour.SetPadding(Ui.Dp(this, 18), Ui.Dp(this, 16), Ui.Dp(this, 18), Ui.Dp(this, 16));
        var tourLp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        tourLp.SetMargins(pad, Ui.Dp(this, 20), pad, 0);

        _tourTitle = Ui.Label(this, "", 17f, Ui.Blue, bold: true);
        _tourBody  = Ui.Label(this, "", 14.5f, Ui.InkSoft);
        _tourBody.SetPadding(0, Ui.Dp(this, 6), 0, 0);
        _tourAction = Ui.Action(this, "", primary: false);
        var actionLp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        actionLp.TopMargin = Ui.Dp(this, 12);

        _tourNext = Ui.Label(this, "Skip", 13.5f, Ui.Blue);
        _tourNext.Gravity = GravityFlags.Center;
        _tourNext.SetPadding(0, Ui.Dp(this, 12), 0, 0);
        _tourNext.Clickable = true;
        _tourNext.Click += (_, _) => ShowTourStep(_tourAt + 1);
        _tourAction.Click += (_, _) => RunTourAction();

        _tour.AddView(_tourTitle, Ui.Fill());
        _tour.AddView(_tourBody, Ui.Fill());
        _tour.AddView(_tourAction, actionLp);
        _tour.AddView(_tourNext, Ui.Fill());
        root.AddView(_tour, tourLp);

        // Spacer, so the claims sit low and the circle owns the upper half.
        var spacer = new View(this);
        root.AddView(spacer, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        // ── three claims, three lines ────────────────────────────────────
        //
        // THE THIRD LINE USED TO SAY "nothing sent anywhere" AND IT STOPPED BEING
        // TRUE. Adding web search means a question about today's weather sends
        // those search words off the phone. Everything else still stays — the
        // conversation, the memory, the identity, the rest of the answer — but
        // "nothing" is now false, and a privacy claim that is subtly false is worse
        // than one that is honestly narrower.
        //
        // So the line says what actually happens: no account, and the only thing
        // that ever leaves is a search you asked for. That is still a stronger
        // promise than any mainstream assistant makes, and it has the advantage of
        // being checkable.
        var claims = new LinearLayout(this) { Orientation = Orientation.Vertical };
        claims.SetPadding(pad, 0, pad, Ui.Dp(this, 16));
        foreach (var line in new[]
                 {
                     "10 plus languages, spoken out loud",
                     "Runs on the phone — works with no signal",
                     "Free, no account — only searches leave the phone",
                 })
        {
            var row = Ui.Label(this, "·   " + line, 15f, Ui.InkSoft);
            row.SetPadding(0, Ui.Dp(this, 6), 0, 0);
            claims.AddView(row);
        }
        root.AddView(claims, Ui.Fill());

        // ── where to go next ─────────────────────────────────────────────
        var nav = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        nav.SetPadding(pad, 0, pad, Ui.Dp(this, 28));

        // ONE thing to do, then two quiet ways to explore.
        //
        // It was three buttons of near-equal weight — a menu, not a path. Three
        // choices of the same size is the screen refusing to say what it is for,
        // and the person has to read all three and rank them before they can move.
        // The hero above is already the loudest thing here ("tap to hear it
        // speak"), so a second shouting button next to two more competes with it
        // and with itself.
        //
        // TYPING IS THE FALLBACK, so it is a link and not the loudest control on
        // the screen. It used to be a full-width blue button reading "Ask it
        // something", which made the text box the headline act and quietly
        // announced this as a thing you write to. The circle above is the product;
        // this is here for the library, the late-night kitchen, and anyone who
        // would simply rather not talk out loud.
#if IT_VOICE_ANDROID
        // NOT A SETTING. A READ-OUT.
        //
        // This was a picker: one tap, eleven languages, remembered. Wrong shape.
        // A picker is a persistent control for a property that is not persistent
        // — South African households do not hold one language for a whole
        // conversation. A clan moves through two or three of them inside a single
        // exchange, so a language chosen on the first turn is wrong by the third,
        // and being answered in the one you have just stopped speaking is the
        // exact insult this is meant to avoid.
        //
        // So every turn decides for itself, and this line only reports what the
        // words said. It stays empty until a turn has actually been heard —
        // announcing "Speaking English" before anyone has spoken is a claim the
        // phone has not earned.
        var lang = Ui.Label(this, "", 14f, Ui.InkSoft);
        lang.Gravity = GravityFlags.Center;
        lang.SetPadding(0, Ui.Dp(this, 6), 0, Ui.Dp(this, 6));
        lang.Visibility = ViewStates.Gone;
        var llp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        llp.LeftMargin = llp.RightMargin = pad;
        root.AddView(lang, llp);
        _lang = lang;
#endif

        var typeInstead = Ui.Label(this, "Or type instead", 15f, Ui.Blue, bold: true);
        typeInstead.Gravity = GravityFlags.Center;
        typeInstead.SetPadding(0, Ui.Dp(this, 14), 0, Ui.Dp(this, 14));   // 48dp target
        typeInstead.Clickable = true;
        typeInstead.Click += (s, e) => StartActivity(new Intent(this, typeof(MainActivity)));
        var clp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        clp.LeftMargin = clp.RightMargin = pad;
        root.AddView(typeInstead, clp);

        // Two quiet, equal siblings. Text buttons rather than outlined boxes: an
        // outline reads as "a thing to press NOW", and these are for later.
        nav.SetPadding(pad, Ui.Dp(this, 4), pad, Ui.Dp(this, 24));

        void Quiet(string text, Type screen)
        {
            var b = Ui.Label(this, text, 15f, Ui.Blue, bold: true);
            b.SetPadding(0, Ui.Dp(this, 14), 0, Ui.Dp(this, 14));   // 48dp target
            b.Gravity = GravityFlags.Center;
            b.Clickable = true;
            b.Click += (s, e) => StartActivity(new Intent(this, screen));
            nav.AddView(b, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        }

        Quiet("10 plus languages", typeof(LanguagePickerActivity));
        Quiet("What it can do", typeof(AbilitiesActivity));

        root.AddView(nav, Ui.Fill());
        SetContentView(root);
    }

    async void SpeakNext()
    {
        _speaking?.Cancel();
        var cts = new CancellationTokenSource();
        _speaking = cts;

        var (tag, label, phrase) = Greetings[_next % Greetings.Length];
        _next++;

        _caption.Text = $"{label} — one of 74";
        _prompt.Text = "…";
        _mark.SetBusy(true);

        try
        {
#if IT_VOICE_ANDROID
            var store = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "CircleAI", "Models");
            var wav = System.IO.Path.Combine(FilesDir!.AbsolutePath, $"home-{tag}.wav");

            // First press of a language fetches its voice, which is not instant on
            // a cheap phone. Say what is happening in words, not a spinner alone —
            // an unexplained wait is where people leave.
            var report = await CircleAI.Samples.It.Voice.ItTtsProbe.RunCataloguedAsync(
                store, tag, phrase, wav,
                line => RunOnUiThread(() =>
                {
                    if (line.Contains("%", StringComparison.Ordinal)) _prompt.Text = "Getting the voice…";
                    else if (line.StartsWith("downloaded", StringComparison.OrdinalIgnoreCase)) _prompt.Text = "Almost there…";
                }),
                cts.Token);

            if (cts.IsCancellationRequested) return;

            if (System.IO.File.Exists(wav) && report.Contains("SYNTHESIS OK", StringComparison.Ordinal))
            {
                _prompt.Text = phrase;
                await MainActivity.PlayWavStaticAsync(wav);
                _prompt.Text = "Tap again for another language";
            }
            else
            {
                _prompt.Text = "Could not speak that one — try another";
            }
#else
            // SHOWN, NOT SPOKEN. The chat-only APK deliberately ships without the
            // speech stack, so there is nothing here that can talk. The greeting is
            // still worth making: the whole point of the tap is "this thing knows
            // your language", and that lands from seeing it written just as well as
            // from hearing it — without 60 MB of ONNX Runtime in the package.
            await Task.Yield();
            _prompt.Text = phrase;
            _caption.Text = $"{label} — tap again for another";
#endif
        }
        catch (System.OperationCanceledException) { }
        catch (Exception ex)
        {
            _prompt.Text = ex.Message.Length > 70 ? "Something went wrong" : ex.Message;
        }
        finally
        {
            if (!cts.IsCancellationRequested) _mark.SetBusy(false);
        }
    }

#if IT_VOICE_ANDROID
    CancellationTokenSource? _turn;

    /// <summary>
    /// One exchange, on this screen: listen, think, answer aloud.
    /// </summary>
    /// <remarks>
    /// THE CIRCLE IS THE INTERFACE FOR THE WHOLE TURN. Handing off to the chat
    /// screen the moment someone pressed it put a transcript in front of a person
    /// who had chosen to speak — the text interface reasserting itself at exactly
    /// the moment they opted out of it. Here they press it, talk, and hear the
    /// answer; there is nothing to read unless they want to read.
    /// <para>
    /// Every phase is on the mark, because a voice interface with no visible state
    /// is indistinguishable from a broken one. Listening moves with your voice,
    /// thinking runs a wave, speaking lights up. Nobody has to be told which is
    /// which.
    /// </para>
    /// </remarks>
    async void TalkOnce()
    {
        if (_turn is not null) { _turn.Cancel(); return; }   // a second press stops it

        if (CheckSelfPermission(Android.Manifest.Permission.RecordAudio)
            != Android.Content.PM.Permission.Granted)
        {
            RequestPermissions([Android.Manifest.Permission.RecordAudio], 1003);
            Apply(_ready with { Headline = "Let it hear you", Caption = "Allow the microphone to talk to it." });
            return;
        }

        var cts = new CancellationTokenSource();
        _turn = cts;

        var store = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "CircleAI", "Models");

        try
        {
            // HAND THE MICROPHONE OVER BEFORE ASKING FOR IT. Android gives out
            // AudioRecord exclusively, so the wake loop has to be fully closed —
            // awaited, not merely cancelled — or this capture opens onto nothing and
            // the turn ends in "I did not catch that" while the person is talking.
            await StopHandsFreeAsync();

            Phase(MarkState.Listening, "Listening", "Say what you need.");

            var turn = new VoiceTurn();
            turn.Level += (_, lvl) => RunOnUiThread(() => _mark.SetLevel(lvl));

            // EVERY STAGE OF THE WAIT, TIMED. The complaint was that it listens
            // long after the request, then thinks, then finally replies — and
            // nothing measured which of those was which, so "it is slow" could
            // not be turned into a fix. Now each leg prints.
            var leg = System.Diagnostics.Stopwatch.StartNew();
            long micOpenMs = 0, speechEndMs = 0;

            await using var mic = new AndroidAudioCapture();
            micOpenMs = leg.ElapsedMilliseconds;

            turn.SpeechStarted += (_, _) =>
                Android.Util.Log.Info("CircleAI.It", $"turn: speech began at {leg.ElapsedMilliseconds} ms");

            var audio = await turn.ListenAsync(mic, cts.Token);
            speechEndMs = leg.ElapsedMilliseconds;

            Android.Util.Log.Info("CircleAI.It",
                $"turn: mic={micOpenMs} ms | listened={speechEndMs - micOpenMs} ms " +
                $"| {audio.Length / 32000.0:F1} s of audio");

            if (audio.Length == 0)
            {
                Phase(MarkState.Idle, _ready.Headline, "I did not catch that.");
                return;
            }

            Phase(MarkState.Thinking, "Thinking", "");

            // HEARD YOU. Said the instant speech ends, before any model runs.
            //
            // The complaint this answers: "it takes too long from hearing the
            // input to thinking — it is so unnatural I forget I am waiting."
            // Between the last word and the first token there are several
            // seconds of transcription and prefill, and a screen the person is
            // not looking at cannot carry that. A person would say "mm" in that
            // gap; silence reads as not having been heard at all.
            Earcon.Heard();

            var stage = System.Diagnostics.Stopwatch.StartNew();

            var ears = await EnsureEarsAsync(store);
            if (ears is null) { Phase(MarkState.Idle, _ready.Headline, _earsStatus); return; }
            var loadMs = stage.ElapsedMilliseconds;

            // EVERY TURN DECIDES ITS OWN LANGUAGE.
            //
            // A setting was the wrong shape for this. South African households do
            // not speak one language — a clan moves through two or three inside a
            // single conversation — so a language chosen once is wrong by the third
            // sentence, and answering in the language somebody was NOT just speaking
            // is the exact insult this is meant to avoid.
            //
            // Whisper's own detection is not the answer either: that is audio LID on
            // a tiny model, and it cannot separate the Nguni languages. The
            // transcript, though, is easy to read — "ngi-" and "ndi-" tell isiZulu
            // from isiXhosa in almost any sentence about oneself.
            //
            // So: guess from the words, per turn. When the guess is unsure it
            // returns null and the stored language stands, which keeps a two-word
            // "yes" from throwing the conversation into another language.
            var transcript = await ears.Transcriber.TranscribeAsync(audio, cts.Token);
            var heard      = transcript.Text?.Trim();

            // WHERE THE SILENCE GOES. Three numbers, because "it feels slow" and
            // "the ears took four seconds" are different problems with different
            // fixes, and until now nothing measured the gap between somebody
            // finishing a sentence and the model starting to answer.
            // AND WHAT IT ACTUALLY HEARD. This logged a character COUNT, which is
            // the one detail that cannot be reasoned from: a turn that ends with
            // "I did not catch that" and a turn that ends in a real answer both
            // print "30 chars". A whole evening went into guessing at the content
            // of a string the device already had. Print the string.
            var transcribeMs = stage.ElapsedMilliseconds - loadMs;
            Android.Util.Log.Info("CircleAI.It",
                $"heard: ears={loadMs} ms | transcribe={transcribeMs} ms " +
                $"| {audio.Length / 32000.0:F1} s of audio | “{heard}”");

            var guess      = CircleAI.Samples.It.LanguageGuess.Detect(heard);
            var spokenLang = guess ?? SpokenLanguage.Current(this);
            if (guess is not null) SpokenLanguage.Set(this, guess);
            Android.Util.Log.Info("CircleAI.It",
                $"language: guess={guess ?? "unsure"} using={spokenLang} (whisper said {transcript.LanguageCode})");

            // Say which one it settled on. Silent language switching is unnerving —
            // when the answer comes back in a language you did not expect there is
            // no way to tell a detection slip from the model wandering off.
            RunOnUiThread(() =>
            {
                if (_lang is null) return;
                _lang.Text = "Answering in " + SpokenLanguage.NameOf(spokenLang);
                _lang.Visibility = ViewStates.Visible;
            });
            if (!IsSomethingSaid(heard))
            {
                // SAY SO IN THE LOG, NOT ONLY ON THE SCREEN. This return is the
                // quietest way a turn can end — no voice load, no generation, no
                // error — and read from a log it is indistinguishable from a hang.
                // Whisper emits bracketed annotations like [音楽] for non-speech,
                // which strip to nothing here, so a turn full of noise lands
                // exactly on this line.
                Android.Util.Log.Info("CircleAI.It",
                    $"turn: ended early — nothing said in “{heard}”");
                Phase(MarkState.Idle, _ready.Headline, "I did not catch that.");
                return;
            }

            // What they said, shown while it thinks. Voice-first does not mean
            // never showing anything — it means not making them read to be
            // understood. Seeing their own words is how they know it heard right.
            Phase(MarkState.Thinking, "Thinking", $"“{heard}”");

            // THE VOICE, LOADED ONCE AND KEPT. Starting it un-awaited here was
            // already right — it overlaps with thinking — but it was STARTED
            // AFRESH EVERY TURN, so every answer paid a synthesiser load that
            // the previous answer had already paid. Held for the life of the
            // screen, the second turn onward has a mouth ready before there is
            // anything to say with it.
            // THE VOICE FOLLOWS THE LANGUAGE, AND ONLY ONE FITS AT A TIME.
            //
            // English needs a voice with a real pronunciation model — Vits-11ZA is
            // grapheme-driven and measured 0.17 word error rate on English against
            // Piper lessac's 0.00 — but loading both alongside the language model
            // put this phone into its low-memory killer and cost a whole answer.
            // So the language decides which one is resident, and a switch drops
            // the other. A held voice from the previous turn is reused only when
            // it is still the right family.
            // BRACKETING A SILENT GAP. A Japanese turn stops dead between the
            // language line and ItSpeaker's first log line — process alive, no CPU,
            // no exception, nothing for minutes. Two guesses have already been
            // wrong about it (graph optimisation, then a download), so this stops
            // guessing and marks each step instead.
            Android.Util.Log.Info("CircleAI.It", "voice: choosing family");
            var wantFamily = CircleAI.Samples.It.Voice.ItSpeaker.FamilyFor(spokenLang);
            Android.Util.Log.Info("CircleAI.It",
                $"voice: want={wantFamily} held={(_voice is null ? "none" : _voice.Status.ToString())}");
            if (_voice is { IsCompletedSuccessfully: true } held &&
                held.Result.Item1 is { } spk && spk.Family != wantFamily)
            {
                Android.Util.Log.Info("CircleAI.It",
                    $"voice: switching {spk.Family} -> {wantFamily}, releasing the old model");
                spk.Dispose();
                _voice = null;
            }

            Android.Util.Log.Info("CircleAI.It", "voice: calling TryCreateAsync");
            _voice ??= CircleAI.Samples.It.Voice.ItSpeaker.TryCreateAsync(
                store, _ => { }, default, spokenLang);
            var voice = _voice;
            Android.Util.Log.Info("CircleAI.It", "voice: TryCreateAsync started (not awaited here)");

            // THE BRAIN, ALREADY LOADING BEFORE THEY SPOKE. This used to be
            // `_session ??= await Task.Run(... StartAsync() ...)` — the model
            // load, measured on this phone at 10.5 to 22.9 seconds, sitting in
            // the middle of the turn with the person waiting. It is the second
            // half of the same mistake the transcriber made: a multi-second
            // load placed exactly where somebody is listening for an answer.
            var brainWait = System.Diagnostics.Stopwatch.StartNew();
            _session = await WarmBrainAsync();
            if (_session is null)
            {
                Phase(MarkState.Idle, _ready.Headline, "The brain is not ready yet.");
                return;
            }
            // CAPTURED, NOT RE-READ LATER. The first version of the summary line
            // below asked this stopwatch for the wait after the answer had been
            // generated — it was never stopped, so it reported the whole rest of
            // the turn and printed "brain 13909 | answer 13909", two different
            // things with one number. A running stopwatch is a clock, not a
            // measurement.
            var brainWaitMs = brainWait.ElapsedMilliseconds;
            Android.Util.Log.Info("CircleAI.It", $"turn: brain waited {brainWaitMs} ms");

            // SPEAK AS IT WRITES. The old code waited for the last word before the
            // first sound, so a 25-75 s answer was 25-75 s of silence. Sentences go
            // to the mouth the moment they are complete; the rest of the answer is
            // still being written while the first is being said.
            var spokenStartMs = leg.ElapsedMilliseconds;
            await using var spoken = new SpokenReply(
                voice,
                lvl => RunOnUiThread(() => _mark.SetLevel(lvl)),
                cts.Token,
                spokenLang);

            var firstWords = true;
            var seenToolCall = false;
            var watch = new System.Text.StringBuilder();

            // ANSWER IN THE LANGUAGE YOU WERE ASKED IN — both halves of it.
            //
            // The VOICE follows the detected language, so isiZulu in means isiZulu
            // out from the same speaker rather than a South African reading English.
            // And the MODEL is told, in the turn itself, to reply in that language:
            // setting the voice alone would produce English words spoken with Zulu
            // phonetics, which is worse than either.
            var replyIn = CircleAI.Samples.It.Voice.ItSpeaker.NameForLanguage(spokenLang);
            var asked   = replyIn is null
                ? heard
                : $"{heard}\n\n(Reply only in {replyIn}.)";

            var reply = await _session.RunTurnStreamingAsync(
                asked,
                _ => { },
                chunk =>
                {
                    // NEVER READ A TOOL CALL ALOUD. The streaming path is the raw
                    // generator — it does not execute tools — so when the model
                    // decides to search, the call arrives here as ordinary text and
                    // was spoken verbatim: a person asked for the weather and the
                    // phone recited a line of JSON at them.
                    watch.Append(chunk);
                    if (!seenToolCall && LooksLikeToolCall(watch)) seenToolCall = true;
                    if (seenToolCall) return;

                    // The mark flips to Speaking on the FIRST chunk, not when the
                    // answer is complete — by then the phone is already talking.
                    if (firstWords)
                    {
                        firstWords = false;
                        Phase(MarkState.Speaking, "", "");
                    }
                    spoken.Add(chunk);
                },
                _ => { });

            // THE FAST PATH CANNOT USE TOOLS, SO EARN IT BACK ONLY WHEN NEEDED.
            // Streaming exists to get sound out early and is right for the great
            // majority of turns, which need no tool at all. When the model asks for
            // one, that whole answer is void: re-run through the agentic path, which
            // executes the call, feeds the result back, and answers from it. Two
            // passes, but only for the turns that genuinely reach the world.
            if (seenToolCall)
            {
                Phase(MarkState.Thinking, "Looking it up", "");
                var tooled = await _session.RunToolTurnAsync(heard);
                reply = tooled.Answer;
                Android.Util.Log.Info("CircleAI.It",
                    $"tool turn ran: [{string.Join(", ", tooled.ToolsCalled)}]");

                if (!string.IsNullOrWhiteSpace(reply))
                {
                    Phase(MarkState.Speaking, "", "");
                    spoken.Add(reply);
                }
            }

            await spoken.FinishAsync();

            // THE WHOLE TURN, ON ONE LINE, IN ORDER.
            //
            // Every stage already printed its own number and that was not the
            // same thing. Reconstructing a turn meant reading five lines spread
            // through a log that other components write to as well, subtracting
            // timestamps by hand, and hoping none of it had scrolled away — which
            // is how a chain that was mostly two model loads went unnoticed for
            // as long as it did. The question a person asks is "how long from me
            // finishing to it answering", and until now nothing answered it.
            //
            // SPEECH-END RELATIVE, not mic-open relative: the seconds spent
            // waiting for somebody to finish talking are not a cost, and mixing
            // them in flatters every other number here.
            var firstSound = spoken.FirstSoundMs >= 0
                ? (spokenStartMs + spoken.FirstSoundMs - speechEndMs).ToString() + " ms"
                : "never";
            Android.Util.Log.Info("CircleAI.It",
                $"TURN: heard {audio.Length / 32000.0:F1} s | " +
                $"transcribe {transcribeMs} | brain {brainWaitMs} | " +
                $"answer {leg.ElapsedMilliseconds - spokenStartMs} | " +
                $"first sound {firstSound} | total {leg.ElapsedMilliseconds - speechEndMs} ms " +
                $"after they stopped talking");

            if (cts.IsCancellationRequested) return;

            // SILENCE IS THE ONE ANSWER A DISTANT LISTENER CANNOT READ. If nothing
            // was spoken the turn produced text on a screen nobody is looking at,
            // which is indistinguishable from the thing being broken. Say so with a
            // sound, since words are exactly what is unavailable.
            if (!spoken.SpokeAnything)
            {
                Earcon.CannotSpeak();

                // THE APOLOGY MUST NOT EAT THE ANSWER. The first version put the
                // reply in the caption and then overwrote that same caption with
                // "it is written above" — pointing at text it had just destroyed,
                // on a screen where nothing was written above at all. Seen on the
                // P30 the first time a wake-word turn ran end to end.
                //
                // There is ONE caption line. The answer gets it, because the answer
                // is what the person asked for. The failure — and why — goes in the
                // headline, which is also where a reason is most use.
                var why = spoken.FailureReason is { Length: > 0 } reason
                    ? $"Could not speak it — {reason}"
                    : "Could not speak it";
                Phase(MarkState.Idle, why, reply);
                return;
            }

            // Spoken and finished. Show the answer too, for anyone who is looking
            // as well as listening, and put the invitation back.
            Phase(MarkState.Idle, _ready.Headline, reply);
        }
        catch (System.OperationCanceledException)
        {
            Phase(MarkState.Idle, _ready.Headline, _ready.Caption);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("CircleAI.It", "voice turn failed: " + ex);
            Phase(MarkState.Idle, _ready.Headline, "That did not work. Try again?");
        }
        finally
        {
            if (ReferenceEquals(_turn, cts)) _turn = null;
            cts.Dispose();

            // Take the microphone back, however the turn ended. In the finally
            // deliberately: if this only ran on the happy path, one failed turn
            // would leave the phone permanently deaf to its own name, and the only
            // way back would be to leave the screen and return.
            _handsFree?.Start();
        }
    }

    /// <summary>Is the model asking to run a tool rather than answering?</summary>
    /// <remarks>
    /// Qwen wraps these in &lt;tool_call&gt;, which is the reliable marker and the
    /// one the agentic parser keys on. The bare-JSON check is a safety net: the tag
    /// can be split across streamed chunks or dropped entirely by a quantised model
    /// that has half-remembered the format, and either way the text is a request to
    /// act, not a sentence to read out.
    /// <para>
    /// Checked against the ACCUMULATED text, never a single chunk — "&lt;tool" and
    /// "_call&gt;" routinely arrive separately.
    /// </para>
    /// </remarks>
    static bool LooksLikeToolCall(System.Text.StringBuilder acc)
    {
        // Only the head matters: a tool call is what the model says INSTEAD of an
        // answer, so it comes first. Scanning the whole buffer forever would let a
        // long answer that merely mentions the words trip this.
        var head = acc.Length <= 400 ? acc.ToString() : acc.ToString(0, 400);

        if (head.Contains("<tool_call", StringComparison.OrdinalIgnoreCase)) return true;

        return head.Contains("\"name\"", StringComparison.Ordinal)
            && head.Contains("\"arguments\"", StringComparison.Ordinal);
    }

    /// <summary>Did the transcriber actually hear words, or just describe silence?</summary>
    /// <remarks>
    /// WHISPER ANSWERS "NOTHING" IN WORDS, and they are words that will otherwise
    /// be shown to a person as if they said them. Caught on the P30: the screen
    /// read Thinking, "[BLANK_AUDIO]" — the model's own marker for silence, quoted
    /// back at the user as their question, and then sent to the brain to be
    /// answered. It emits a family of these — [BLANK_AUDIO], [SILENCE], (music),
    /// *coughs* — whenever there is sound but no speech, which is every noisy room
    /// this is meant to work in.
    /// <para>
    /// Anything entirely inside brackets is the transcriber describing the audio
    /// rather than transcribing it, so it counts as nothing said.
    /// </para>
    /// </remarks>
    static bool IsSomethingSaid(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var stripped = System.Text.RegularExpressions.Regex.Replace(
            text, @"[\[\(\*][^\]\)\*]*[\]\)\*]", " ").Trim();

        // A stray letter or two is transcriber noise, not a question.
        return stripped.Length >= 2 && stripped.Any(char.IsLetter);
    }

    CircleAI.Samples.It.ItSession? _session;

    // ── hands free ───────────────────────────────────────────────────────────

    HandsFree? _handsFree;

    /// <summary>Starts listening for the wake phrase, if it is not already.</summary>
    /// <remarks>
    /// RESIDENT FIRST, ACTIVITY AS THE FALLBACK. The microphone belongs to the
    /// foreground service, not to this screen: opened here it closes the moment
    /// the phone goes in a pocket, which made "always listening" mean "listening
    /// while you are looking at it".
    /// <para>
    /// The in-activity HandsFree loop stays as the fallback for the case the
    /// service cannot take the microphone — no permission yet, or a vendor that
    /// has killed the service — because a wake word that works only while the
    /// app is open is still better than one that does not work at all.
    /// </para>
    /// </remarks>
    void StartHandsFree(string bundleDir)
    {
        _ = StartResidentAsync(bundleDir);
    }

    /// <summary>Hands the microphone to the service, falling back to this screen.</summary>
    async Task StartResidentAsync(string bundleDir)
    {
        if (CircleAI.Device.CircleNeuronService.IsListening) return;

        // Closed first: AudioRecord is exclusive, so an activity-scoped loop
        // still holding it would make the service's open fail and look like the
        // resident path is broken.
        await StopHandsFreeAsync();

        var ok = await ResidentAssistant.StartAsync(this, bundleDir, OnResidentWoke);
        if (ok)
        {
            // Recorded so BootReceiver knows this was the owner's choice and not
            // something the app helped itself to.
            ResidentPrefs.SetRunning(this, true);
            return;
        }

        Android.Util.Log.Warn("CircleAI.It", "resident listening unavailable — falling back to this screen");
        StartHandsFreeInActivity(bundleDir);
    }

    /// <summary>The service heard the phrase. Same handling as an in-app wake.</summary>
    void OnResidentWoke(object? sender, string phrase)
    {
        Earcon.Woke();
        RunOnUiThread(() =>
        {
            if (_turn is not null)
            {
                Android.Util.Log.Warn("CircleAI.It", $"woke on \"{phrase}\" but a turn is already running — ignored");
                return;
            }
            Android.Util.Log.Info("CircleAI.It", $"woke on \"{phrase}\" (resident)");
            TalkOnce();
        });
    }

    // ── the ears, held open ──────────────────────────────────────────────────

    CircleAI.Samples.It.Voice.ItListener? _ears;
    string _earsStatus = "";
    Task<CircleAI.Samples.It.Voice.ItListener?>? _earsLoading;

    /// <summary>
    /// The transcriber, loaded once and kept.
    /// </summary>
    /// <remarks>
    /// IT WAS BEING LOADED AND THROWN AWAY ON EVERY SINGLE TURN. TalkOnce called
    /// TryCreateAsync inside the turn and held it with `await using`, so whisper
    /// — 78 MB, read off eMMC and initialised — was built after the person
    /// stopped speaking and destroyed before they could speak again. That load
    /// sits exactly in the gap somebody described as "so unnatural I forget I am
    /// waiting for a reply", and it was paid in full every time.
    /// <para>
    /// One instance, for the life of the screen. The memory is the point of
    /// keeping it: a resident transcriber costs RAM continuously, which is a real
    /// price on a 3.7 GB phone — but the alternative is paying its load in the
    /// one place a person is actually waiting.
    /// </para>
    /// <para>
    /// Concurrent callers share one load. Two turns starting close together used
    /// to build two copies of whisper on a phone that cannot hold two.
    /// </para>
    /// </remarks>
    Task<CircleAI.Samples.It.Voice.ItListener?> EnsureEarsAsync(string store)
    {
        if (_ears is not null) return Task.FromResult<CircleAI.Samples.It.Voice.ItListener?>(_ears);
        if (_earsLoading is not null) return _earsLoading;

        _earsLoading = Load();
        return _earsLoading;

        async Task<CircleAI.Samples.It.Voice.ItListener?> Load()
        {
            try
            {
                var (listener, status) = await CircleAI.Samples.It.Voice.ItListener
                    .TryCreateAsync(store, _ => { });
                _earsStatus = status;
                _ears = listener;
                return listener;
            }
            finally { _earsLoading = null; }
        }
    }

    Task<CircleAI.Samples.It.ItSession?>? _brainLoading;

    /// <summary>The synthesiser, started once and reused across turns.</summary>
    Task<(CircleAI.Samples.It.Voice.ItSpeaker?, string)>? _voice;

    /// <summary>
    /// The brain, loaded once and kept — started before anybody speaks.
    /// </summary>
    /// <remarks>
    /// SAME MISTAKE AS THE EARS, ONE LAYER OVER. The session was built lazily
    /// inside the turn, so the very first thing somebody said paid for the model
    /// load — 10.5 s on a good run, 22.9 s on a cold one, measured on this
    /// phone. From the outside that is the assistant "thinking" for half a
    /// minute before it has begun to think at all.
    /// <para>
    /// Concurrent callers share one load: two turns starting together used to
    /// build two sessions, and two copies of a 550 MB model is not something a
    /// 3.7 GB phone survives.
    /// </para>
    /// </remarks>
    Task<CircleAI.Samples.It.ItSession?> WarmBrainAsync()
    {
        if (_session is not null) return Task.FromResult<CircleAI.Samples.It.ItSession?>(_session);
        if (_brainLoading is not null) return _brainLoading;

        _brainLoading = Load();
        return _brainLoading;

        async Task<CircleAI.Samples.It.ItSession?> Load()
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // One brain per process, not per screen — see ItSessionHost.
                var alreadyWarm = ItSessionHost.IsWarm;
                var s = await ItSessionHost.GetAsync(this);
                _session = s;

                // SAYS WHAT IT MEASURES, which is not what ItSessionHost measures.
                // Both printed "brain warm in N ms" and they are different
                // quantities — that one is how long the model took to load, this
                // one is how long THIS caller waited for it. Two identical
                // sentences reporting two different things is worse than either
                // being missing, because the log looks like it loaded twice.
                Android.Util.Log.Info("CircleAI.It",
                    alreadyWarm
                        ? $"brain: already warm ({sw.ElapsedMilliseconds} ms to hand over)"
                        : $"brain: waited {sw.ElapsedMilliseconds} ms for the shared load");
                return s;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("CircleAI.It", "brain load failed: " + ex);
                return null;
            }
            finally { _brainLoading = null; }
        }
    }

    /// <summary>
    /// Loads the transcriber before anybody speaks.
    /// </summary>
    /// <remarks>
    /// Warmed as soon as readiness says the model is on disk, so the FIRST turn
    /// is as quick as the rest. Without this the fix above only helps from the
    /// second sentence onward — and the first one is the one that decides whether
    /// somebody thinks this thing works.
    /// </remarks>
    void WarmEars()
    {
        if (_ears is not null || _earsLoading is not null) return;

        var store = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "CircleAI", "Models");

        _ = Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ok = await EnsureEarsAsync(store) is not null;
            Android.Util.Log.Info("CircleAI.It",
                ok ? $"ears warm in {sw.ElapsedMilliseconds} ms" : "ears not available: " + _earsStatus);
        });
    }

    /// <summary>The old activity-scoped loop, kept as the fallback.</summary>
    void StartHandsFreeInActivity(string bundleDir)
    {
        if (_handsFree is not null) { _handsFree.Start(); return; }

        var hf = new HandsFree(bundleDir);

        // Arrives off the UI thread, and a wake mid-turn must not start a second
        // one — TalkOnce treats a re-entrant call as "stop", which would cancel the
        // very turn the wake just began.
        hf.Woke += (_, phrase) =>
        {
            // ANSWERED BEFORE THE UI THREAD EVEN GETS IT. Everything else about a
            // wake — the circle changing, the caption — is on a screen the person
            // who just called from another room is not looking at, and the first
            // answer is 30-90 s away. One sound here is the whole difference
            // between "it heard me" and "did that work?".
            Earcon.Woke();

            RunOnUiThread(() =>
            {
                // SAY WHEN A WAKE IS THROWN AWAY. This guard is right — a wake in
                // the middle of a turn must not start a second one — but it used to
                // drop the phrase in silence, so a stuck turn would make the phone
                // beep at every "Hey B" and then do nothing, forever, with no trace
                // of why. If this line starts repeating, the turn never cleared.
                if (_turn is not null)
                {
                    Android.Util.Log.Warn("CircleAI.It", $"woke on \"{phrase}\" but a turn is already running — ignored");
                    return;
                }
                Android.Util.Log.Info("CircleAI.It", $"woke on \"{phrase}\"");
                TalkOnce();
            });
        };

        _handsFree = hf;
        hf.Start();
    }

    /// <summary>Releases the microphone and waits until it is genuinely released.</summary>
    /// <remarks>
    /// BOTH HOLDERS, because there are now two. AudioRecord is exclusive: a turn
    /// that closes the activity loop while the SERVICE still has the microphone
    /// records silence and ends in "I did not catch that" while somebody is
    /// talking to it — the same failure the original comment describes, one
    /// layer further out.
    /// </remarks>
    async Task StopHandsFreeAsync()
    {
        if (_handsFree is not null) await _handsFree.StopAsync();
        await ResidentAssistant.StopListeningAsync();
    }

    void Phase(MarkState state, string headline, string caption) => RunOnUiThread(() =>
    {
        _mark.SetState(state);
        if (headline.Length > 0) _prompt.Text = headline;
        _caption.Text = caption;
        _caption.Visibility = caption.Length == 0 ? ViewStates.Gone : ViewStates.Visible;
    });
#endif

#if IT_VOICE_ANDROID
    /// <summary>
    /// Closes the ACTIVITY'S microphone when the screen goes away.
    /// </summary>
    /// <remarks>
    /// AN OPEN MICROPHONE IS A PROMISE, and this screen only ever promised to
    /// listen while it is in front of you. Leaving the activity's wake loop
    /// running behind another app would also quietly take the mic away from that
    /// app, which is the kind of thing people never forgive an assistant for.
    /// <para>
    /// THE SERVICE IS A DIFFERENT PROMISE and is deliberately left alone. It
    /// holds the microphone with a persistent notification saying so, which the
    /// owner turned on and can turn off from that notification — the same
    /// arrangement Auto Shazam uses. Stopping it here would undo the entire
    /// point: an assistant you can call from the next room, rather than one that
    /// listens only while you are looking at it.
    /// </para>
    /// </remarks>
    protected override void OnPause()
    {
        base.OnPause();
        if (!CircleAI.Device.CircleNeuronService.IsListening)
            _ = (_handsFree?.StopAsync() ?? Task.CompletedTask);
    }
#endif

    protected override void OnDestroy()
    {
        _speaking?.Cancel();
#if IT_VOICE_ANDROID
        _turn?.Cancel();
        // Same reasoning as OnPause: the activity's loop goes, the service stays.
        if (!CircleAI.Device.CircleNeuronService.IsListening)
            _ = (_handsFree?.StopAsync() ?? Task.CompletedTask);

        // The resident transcriber goes with the screen that owns it. Kept for
        // the life of that screen so no turn pays its load, released here so it
        // is not holding tens of megabytes on a phone that has closed the app.
        var ears = _ears;
        _ears = null;
        if (ears is not null) _ = ears.DisposeAsync();
#endif
        base.OnDestroy();
    }
}
