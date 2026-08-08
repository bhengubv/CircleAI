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

            var lastStep = -1;
            var progress = new Progress<CircleAI.Samples.It.SetupProgress>(p => RunOnUiThread(() =>
            {
                _caption.Text = $"{p.Title} — {p.Fraction * 100:0}%";

                // Each finished part makes the phone able to do something new, and
                // the wake loop only starts when readiness notices the bundle
                // arrive. Re-checking on the step boundary is what makes it possible
                // to start talking to it while the brain is still downloading.
                if (p.Index != lastStep) { lastStep = p.Index; _ = CheckReadyAsync(); }
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
        // say hello in one of the 74 languages instead of doing nothing — because
        // "nothing happens" is indistinguishable from "broken", and hearing it
        // speak is the fastest way to understand what this is.
        _mark.Clickable = true;
        _mark.Click += (s, e) =>
        {
            // A DOWNLOAD IS ALREADY RUNNING. Not a greeting and not a turn: the
            // caption is saying what it is fetching, and a stray tap must not throw
            // away four minutes of somebody's data.
            if (_setup is not null) return;

            // "TAP TO START" HAS TO START SOMETHING. CanTalk is false for two very
            // different reasons and this line used to treat them as one: parts still
            // ARRIVING (a greeting is right — it says "alive, nearly there"), and
            // NOTHING INSTALLED AT ALL, where nothing is coming and the greeting is
            // the whole of what happens. A fresh install had no path to a working
            // assistant anywhere on this screen while the screen offered one.
            if (_ready.Stage == ReadyStage.NeedsSetup) { StartSetup(); return; }

            if (!_ready.CanTalk) { SpeakNext(); return; }
#if IT_VOICE_ANDROID
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
                     "74 languages, spoken out loud",
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

        Quiet("74 languages", typeof(LanguagePickerActivity));
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

            await using var mic = new AndroidAudioCapture();
            var audio = await turn.ListenAsync(mic, cts.Token);

            if (audio.Length == 0)
            {
                Phase(MarkState.Idle, _ready.Headline, "I did not catch that.");
                return;
            }

            Phase(MarkState.Thinking, "Thinking", "");

            var (listener, lStatus) = await CircleAI.Samples.It.Voice.ItListener
                .TryCreateAsync(store, _ => { });
            if (listener is null) { Phase(MarkState.Idle, _ready.Headline, lStatus); return; }
            await using var ears = listener;

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
                Phase(MarkState.Idle, _ready.Headline, "I did not catch that.");
                return;
            }

            // What they said, shown while it thinks. Voice-first does not mean
            // never showing anything — it means not making them read to be
            // understood. Seeing their own words is how they know it heard right.
            Phase(MarkState.Thinking, "Thinking", $"“{heard}”");

            // LOAD THE VOICE WHILE IT THINKS. Not after. The synthesiser needs
            // nothing from the answer, so waiting for one before starting the other
            // simply added its load time to every turn. Started here, un-awaited, it
            // is normally ready before the first sentence exists.
            var voice = CircleAI.Samples.It.Voice.ItSpeaker.TryCreateAsync(store, _ => { });

            _session ??= await Task.Run(async () =>
            {
                var s = new CircleAI.Samples.It.ItSession(
                    ApplicationInfo?.NativeLibraryDir, batteryPercent: () => 100);
                await s.StartAsync();
                return s;
            });

            // SPEAK AS IT WRITES. The old code waited for the last word before the
            // first sound, so a 25-75 s answer was 25-75 s of silence. Sentences go
            // to the mouth the moment they are complete; the rest of the answer is
            // still being written while the first is being said.
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
    void StartHandsFree(string bundleDir)
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
    Task StopHandsFreeAsync() => _handsFree?.StopAsync() ?? Task.CompletedTask;

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
    /// Closes the microphone when the screen goes away.
    /// </summary>
    /// <remarks>
    /// AN OPEN MICROPHONE IS A PROMISE, and this screen only ever promised to
    /// listen while it is in front of you. Leaving the wake loop running behind
    /// another app would also quietly take the mic away from that app, which is
    /// the kind of thing people never forgive an assistant for.
    /// </remarks>
    protected override void OnPause()
    {
        base.OnPause();
        _ = StopHandsFreeAsync();
    }
#endif

    protected override void OnDestroy()
    {
        _speaking?.Cancel();
#if IT_VOICE_ANDROID
        _turn?.Cancel();
        _ = StopHandsFreeAsync();
#endif
        base.OnDestroy();
    }

    /// <summary>
    /// The brand mark, drawn large: a ring with sound leaving it. Same shape as
    /// the launcher icon, so the thing on the home screen and the thing you press
    /// are recognisably one object.
    /// </summary>
    sealed class MarkView : View
    {
        readonly Paint _ring = new(PaintFlags.AntiAlias) { Color = Ui.Blue };
        readonly Paint _arc  = new(PaintFlags.AntiAlias) { Color = Ui.Blue };
        readonly Paint _halo = new(PaintFlags.AntiAlias) { Color = Ui.Blue };
        bool _busy;

        MarkState _state = MarkState.Idle;
        float _level;          // 0..1, the voice arriving right now
        float _shown;          // smoothed, so the arcs move like breath not static
        long  _tick;           // frame counter, drives Thinking and Speaking

        public MarkView(Context c) : base(c)
        {
            _ring.SetStyle(Paint.Style.Stroke);
            _arc.SetStyle(Paint.Style.Stroke);
            _arc.StrokeCap = Paint.Cap.Round;
            _halo.SetStyle(Paint.Style.Fill);
            _halo.Alpha = 28;
        }

        public void SetBusy(bool busy)
        {
            _busy = busy;
            if (busy)
            {
                var pulse = new AlphaAnimation(1f, 0.45f)
                {
                    Duration = 700,
                    RepeatCount = Animation.Infinite,
                    RepeatMode = RepeatMode.Reverse,
                };
                StartAnimation(pulse);
            }
            else ClearAnimation();
            Invalidate();
        }

        /// <summary>Which part of the exchange this is.</summary>
        public void SetState(MarkState s)
        {
            if (_state == s) return;
            _state = s;
            _tick = 0;
            // Listening and Speaking both ride a real audio level — one coming in,
            // one going out — so only the states with no sound of their own start
            // from zero.
            if (s is not (MarkState.Listening or MarkState.Speaking)) _level = _shown = 0;
            ClearAnimation();           // the states animate themselves; alpha would fight them
            Invalidate();
        }

        /// <summary>
        /// How loud the microphone is hearing you, 0 to 1.
        /// </summary>
        /// <remarks>
        /// THE ANSWER TO "CAN IT HEAR ME", given without words. It is the question
        /// everybody asks silently the moment they start talking to a machine, and
        /// a spinner cannot answer it — a spinner spins whether the microphone is
        /// working or muted or pointed at a wall. Arcs that move with your own
        /// voice answer it instantly, in a way a child and a grandparent both read
        /// without being told.
        /// <para>
        /// It is a REAL level off the microphone, not a decorative animation. That
        /// distinction is the whole value: a fake meter would be lying about the
        /// one thing the person is trying to find out.
        /// </para>
        /// </remarks>
        public void SetLevel(float level)
        {
            _level = Math.Clamp(level, 0f, 1f);
            // Speaking redraws on its own clock already, so nudging it here would
            // just queue duplicate frames.
            if (_state == MarkState.Listening) Invalidate();
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            float w = Width, h = Height;
            float cx = w / 2f, cy = h / 2f;
            float r = Math.Min(w, h) / 2f;

            _ring.StrokeWidth = r * 0.11f;
            _arc.StrokeWidth  = r * 0.085f;

            // Chase the level rather than snapping to it. Raw frame-to-frame RMS
            // jitters hard enough to look like a fault; easing turns the same data
            // into something that reads as breathing.
            _shown += (_level - _shown) * (_level > _shown ? 0.45f : 0.12f);

            _halo.Alpha = _state switch
            {
                MarkState.Listening => (int)(28 + 46 * _shown),
                MarkState.Speaking  => (int)(40 + 34 * _shown),
                _ => 28,
            };
            canvas.DrawCircle(cx, cy, r * 0.98f, _halo);

            // The ring, open on the right where the sound leaves.
            var ringR = r * 0.46f;
            var ringBox = new RectF(cx - ringR, cy - ringR, cx + ringR, cy + ringR);
            canvas.DrawArc(ringBox, -50f, 280f, false, _ring);

            // THREE PHASES, THREE DIFFERENT MOTIONS — not three brightnesses.
            //
            // They used to be told apart by alpha alone, and Speaking was not
            // animated at all: a flat Alpha=255 at the idle spread, which is a lit
            // circle sitting still. So "I am hearing you", "I am thinking" and "I am
            // answering" all looked like the same glowing mark, and the one moment a
            // person most needs feedback — the long wait — was the least legible.
            //
            // Motion is what the eye reads first, so each phase gets its own:
            //
            //   LISTENING  arcs SCALE with your voice. Reactive, driven by the mic —
            //              it moves because YOU are moving it.
            //   THINKING   a bright band TRAVELS outward through the arcs, over and
            //              over. Directional and repeating: work in progress, no
            //              claim about how much is left.
            //   SPEAKING   arcs FIRE in sequence from the inside out, riding the
            //              output level. Sound leaving the phone, the mirror image
            //              of Listening pulling it in.
            for (var i = 0; i < 3; i++)
            {
                var spread = _state switch
                {
                    MarkState.Listening => 0.16f + 0.16f * i + 0.07f * _shown * (i + 1),
                    // Speaking breathes outward on the beat rather than with input.
                    MarkState.Speaking  => 0.16f + 0.16f * i + 0.05f * SpeakPulse(i),
                    _ => 0.16f + 0.16f * i,
                };
                var ar = ringR + r * spread;
                var box = new RectF(cx - ar, cy - ar, cx + ar, cy + ar);

                _arc.Alpha = _state switch
                {
                    MarkState.Listening => (int)Math.Clamp(70 + 185 * _shown * (1f - 0.22f * i), 40, 255),
                    MarkState.Thinking  => (int)Math.Clamp(60 + 195 * TravelBand(i), 40, 255),
                    MarkState.Speaking  => (int)Math.Clamp(80 + 175 * SpeakPulse(i), 60, 255),
                    _ => _busy ? 255 : i switch { 0 => 235, 1 => 150, _ => 80 },
                };
                canvas.DrawArc(box, -34f, 68f, false, _arc);
            }

            // Thinking and Speaking both animate on their own clock, so they ask for
            // the next frame. Listening is redrawn by arriving audio levels and Idle
            // is static — self-scheduling there would spin the CPU for no gain.
            if (_state is MarkState.Thinking or MarkState.Speaking)
            {
                _tick++;
                PostInvalidateOnAnimation();
            }
        }

        /// <summary>
        /// A bright band sweeping outward through the three arcs, for THINKING.
        /// </summary>
        /// <remarks>
        /// Deliberately a travelling position rather than a per-arc sine. Three arcs
        /// fading in and out on their own phases read as a shimmer; one band moving
        /// through them reads as something being carried from here to there, which is
        /// what "working on it" should look like. Returns 0..1 for how lit arc
        /// <paramref name="i"/> is right now.
        /// </remarks>
        float TravelBand(int i)
        {
            // Sweeps 0 -> 3 then wraps, so the band leaves the outer arc and
            // re-enters at the ring, over and over.
            var head = (_tick * 0.045f) % 3.2f;
            var d = Math.Abs(head - i);
            return Math.Max(0f, 1f - d);           // triangular falloff either side
        }

        /// <summary>
        /// Arcs firing outward in sequence, for SPEAKING, scaled by output loudness.
        /// </summary>
        /// <remarks>
        /// The inner arc leads and the outer follows, so the motion pushes AWAY from
        /// the mark — the opposite direction to Listening, which pulls inward as you
        /// talk. When the player reports a real output level the pulse rides it, so
        /// the mark moves with the words rather than to a metronome; with no level it
        /// still beats steadily, because a silent-looking mark during a spoken answer
        /// is the bug this replaces.
        /// </remarks>
        float SpeakPulse(int i)
        {
            var phase = _tick * 0.11f - i * 0.8f;   // inner arc leads, outer trails
            var beat  = 0.5 + 0.5 * Math.Sin(phase);
            var gain  = 0.55f + 0.45f * _shown;     // louder speech, wider swing
            return (float)(beat * gain);
        }
    }
}
