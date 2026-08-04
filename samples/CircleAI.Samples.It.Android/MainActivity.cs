// MainActivity.cs
//
// IT! on Android — the sample a developer actually drives. Type a message, watch
// the concierge pick which organ answers, watch the reply stream in word by word.
// The whole brain is the shared ItSession (same C# the desktop console runs).

using System.Linq;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using CircleAI.Samples.It;

namespace CircleAI.Samples.It.Mobile;

// The chat screen. No longer the launcher — HomeActivity is, because this one
// opens by downloading 433 MB and then showing a log, which is the wrong first
// impression of anything. Reached deliberately, from "Ask it something".
// Exported so a test script can drive it: the voice sweeps launch it with
// --es tts_lang / --ei tts_speaker, and once it stopped being the launcher it
// stopped being exported by default, which broke every scripted run with a
// permission denial. It holds nothing private — it is the chat screen of a
// sample app — and the alternative is synthesising taps at fixed screen
// coordinates, which has already landed in the wrong app once.
// SingleTop so a second intent reaches the RUNNING instance instead of stacking a
// new one. Without it the only way to deliver fresh parameters was to force-stop
// and relaunch, which threw away the warm ONNX session with the process — 122 MB
// reloaded from storage for every comparison, turning a four-second synthesis into
// a minute. Tuning a voice by ear means dozens of those, and per-language work
// means dozens more.
[Activity(Label = "Ask CircleAI",
          ParentActivity = typeof(HomeActivity),
          Exported = true,
          LaunchMode = Android.Content.PM.LaunchMode.SingleTop,
          WindowSoftInputMode = SoftInput.AdjustResize)]
public class MainActivity : Activity
{
    ItSession? _session;
    TextView _transcript = null!;
    ScrollView _scroll = null!;
    EditText _input = null!;
    Button _send = null!;
    Button _tools = null!;
    Button _cv = null!;
    Button _vision = null!;
#if IT_VOICE_ANDROID
    Button _talk = null!;
    Button _tts = null!;
#endif

    static readonly Color Bg    = Color.ParseColor("#080d14");
    static readonly Color Panel = Color.ParseColor("#0f1927");
    static readonly Color Ink   = Color.ParseColor("#eef6ff");
    static readonly Color Muted = Color.ParseColor("#5f7a95");
    static readonly Color Blue  = Color.ParseColor("#2196F3");

    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Teach the platform-neutral device probe how to read THIS phone. Was
        // thirty lines of ActivityManager here, which is exactly the shape every
        // head was expected to copy and none of them did. One call now, and the
        // device service makes it for anything hosted in it.
        CircleAI.Device.AndroidDeviceMemory.Install(this);

#if IT_VOICE_ANDROID
        // On-device TTS phonemes come from the SEPARATE espeak G2P app
        // (com.bhengubv.espeakng) across a process boundary — espeak-ng is GPL and is
        // never linked into CircleAI. If that app is absent, TTS degrades to text
        // (OutOfProcessEspeakPhonemizer throws a clear reason, caught by RunTts).
        CircleAI.Samples.It.Voice.ItSpeaker.MobilePhonemizerFactory =
            voice => new OutOfProcessEspeakPhonemizer(this, voice);

        // Start warming ToucanTTS now. Its three graphs take minutes to load off
        // storage but only ~4 s to synthesise once resident, so the load belongs at
        // app start, in the background — not in front of the user's first sentence.
        try
        {
            var warmRoot = GetExternalFilesDir(null)?.AbsolutePath;
            if (!string.IsNullOrEmpty(warmRoot))
            {
                var warmDir = System.IO.Path.Combine(warmRoot, "toucan");
                var warmLangFile = System.IO.Path.Combine(warmDir, "lang.txt");
                if (System.IO.File.Exists(System.IO.Path.Combine(warmDir, "toucan_stage_a.ort")) ||
                    System.IO.File.Exists(System.IO.Path.Combine(warmDir, "toucan_stage_a.onnx")))
                {
                    var warmLang = System.IO.File.Exists(warmLangFile)
                        ? (await System.IO.File.ReadAllTextAsync(warmLangFile)).Trim()
                        : "zul";
                    CircleAI.Samples.It.Voice.ItTtsProbe.PreloadToucan(
                        warmDir, System.IO.Path.Combine(warmDir, "nchlt"), warmLang);
                    Append("[tts] warming ToucanTTS in the background…\n");
                }
            }
        }
        catch { /* warming is an optimisation, never a startup dependency */ }
#endif

        BuildUi();

        // Written for somebody who has never heard of this project. The old text
        // opened with "CircleAI Neuron" and told them to tap "Caps" for a
        // "per-modality sweep" — three pieces of in-house vocabulary before the
        // first full stop.
        Append("Everything here runs on this phone.\n");
        Append("No account. No cloud. Nothing you type or say is sent anywhere.\n\n");

        Append("Tap Languages to hear it speak in any of 74 languages.\n");
        Append("Tap What it can do to see what this phone can run — that one\n");
        Append("needs no download and answers straight away.\n\n");

        Append("Type a message below and it will answer. Try telling it your\n");
        Append("name, then asking what your name is — it remembers, on device.\n\n");

        Append("Getting ready. The first answer needs a model that fits this\n");
        Append("phone, about 433 MB, so it takes a few minutes on the first run.\n");
        Append("After that it starts from what is already here.\n\n");

        try
        {
            // Android: tell the SDK where libmnnbridge.so / libMNN.so actually live.
            var nativeLibDir = ApplicationInfo?.NativeLibraryDir;

            // The download and the native model load are heavy (and the load is
            // a blocking native call) — keep them off the UI thread or Android ANRs.
            _session = await Task.Run(async () =>
            {
                var s = new ItSession(nativeLibDir, batteryPercent: ReadBatteryPercent);
                await s.StartAsync();
                return s;
            });

            Append($"status: {_session.StatusLine}\n\n");
            _send.Enabled = true;
            _tools.Enabled = true;
            _input.Enabled = true;
#if IT_VOICE_ANDROID
            _talk.Enabled = true;
#endif
            _input.RequestFocus();
        }
        catch (Exception ex)
        {
            // A stack trace is the correct thing to KEEP and the wrong thing to
            // SHOW. This used to print the whole exception on the first screen: a
            // stranger opening the app met "QwenTextGenerator..ctor(String
            // modelPath, UInt32 contextSize, Nullable`1 threads...)" and reasonably
            // concluded the thing was broken. Say what happened, say what still
            // works, and keep the detail on disk for whoever can use it.
            Append("The chat model could not start on this phone.\n\n");
            Append($"Reason: {Summarise(ex)}\n\n");
            Append("Languages and What it can do still work — they do not need\n");
            Append("the chat model. Tap Languages to hear the phone speak.\n");

            try
            {
                // The exception alone does not say WHY the load failed. MNN's
                // "load failed" arrives after the config parsed successfully, so
                // the useful evidence is what is actually on disk beside it — a
                // missing or short weight file looks identical from the C# side.
                // Written to external storage as well, because a Release build's
                // private directory cannot be read over adb at all.
                var report = new System.Text.StringBuilder()
                    .AppendLine(ex.ToString())
                    .AppendLine()
                    .AppendLine("── model storage ──")
                    .ToString() + DescribeModelStorage();

                await System.IO.File.WriteAllTextAsync(
                    System.IO.Path.Combine(FilesDir!.AbsolutePath, "startup-error.txt"), report);

                var ext = GetExternalFilesDir(null)?.AbsolutePath;
                if (!string.IsNullOrEmpty(ext))
                    await System.IO.File.WriteAllTextAsync(
                        System.IO.Path.Combine(ext, "startup-error.txt"), report);
            }
            catch { /* the message above already told them what matters */ }
        }
    }

    /// <summary>Every model file on this device, with its real size on disk.</summary>
    string DescribeModelStorage()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var root = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "CircleAI", "Models");
            sb.AppendLine($"root: {root}");
            if (!System.IO.Directory.Exists(root)) return sb.AppendLine("  (does not exist)").ToString();

            foreach (var dir in System.IO.Directory.EnumerateDirectories(root))
            {
                sb.AppendLine($"  {System.IO.Path.GetFileName(dir)}/");
                foreach (var f in System.IO.Directory.EnumerateFiles(dir, "*", System.IO.SearchOption.AllDirectories))
                    sb.AppendLine($"    {new System.IO.FileInfo(f).Length,14:N0}  {System.IO.Path.GetRelativePath(dir, f)}");
            }

            // Free RAM at the moment of failure, since "not enough memory" and
            // "file missing" produce the same MNN return code.
            if (GetSystemService(Android.Content.Context.ActivityService) is Android.App.ActivityManager am)
            {
                var mi = new Android.App.ActivityManager.MemoryInfo();
                am.GetMemoryInfo(mi);
                sb.AppendLine($"\nfree RAM: {mi.AvailMem / 1_000_000:N0} MB of {mi.TotalMem / 1_000_000:N0} MB" +
                              $"   lowMemory={mi.LowMemory}  threshold={mi.Threshold / 1_000_000:N0} MB");
            }
        }
        catch (Exception ex) { sb.AppendLine("  (could not read: " + ex.Message + ")"); }
        return sb.ToString();
    }

    /// <summary>One sentence a non-developer can act on, from an exception.</summary>
    static string Summarise(Exception ex)
    {
        var msg = ex.GetBaseException().Message;
        // Checked first, because the MNN message ALSO contains the word "RAM" and
        // a full internal file path — matching on "memory" further down let the
        // raw path through onto the first screen.
        if (msg.Contains("MNN model load failed", StringComparison.OrdinalIgnoreCase))
            return "the model file on this phone could not be opened. Re-downloading it usually fixes it.";
        if (msg.Contains("memory", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("OutOfMemory", StringComparison.OrdinalIgnoreCase))
            return "not enough free memory. Closing other apps usually fixes it.";
        if (msg.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return "the phone blocked a file or network request.";
        if (msg.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return "part of the model is missing — it may still be downloading.";
        if (ex is System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException)
            return "the download could not reach the internet.";
        return msg.Length > 160 ? msg[..160] + "…" : msg;
    }

    /// <summary>
    /// Accepts a new set of parameters without restarting, so the loaded voice
    /// stays loaded.
    /// </summary>
    /// <remarks>
    /// The tuning knobs arrive as intent extras, which <see cref="OnCreate"/>
    /// reads — and OnCreate runs once. Delivering new values therefore used to
    /// mean force-stopping the app, which killed the process, which discarded the
    /// cached ONNX session. Every comparison then paid a fresh 122 MB model load:
    /// about a minute per variant, for a synthesis that takes four seconds warm.
    ///
    /// Handling the intent here keeps the session alive between runs. Android
    /// leaves <see cref="Activity.Intent"/> pointing at the ORIGINAL intent unless
    /// it is reassigned, so that assignment is the whole fix — without it the new
    /// extras are delivered and then ignored, which looks exactly like the
    /// parameter having no effect.
    /// </remarks>
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent is null) return;

        Intent = intent;
        if (intent.GetBooleanExtra("run_tts", false))
        {
            Append("\n───────────────\n");
            RunTts();
        }
    }

    void BuildUi()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Ui.Bg);

        // Header. There used to be two of these stacked — the ActionBar said "IT!"
        // and so did the view right under it. One title is enough.
        ActionBar?.Hide();
        var header = new LinearLayout(this) { Orientation = Orientation.Vertical };
        header.SetBackgroundColor(Ui.Surface);
        header.SetPadding(Ui.Dp(this, 20), Ui.Dp(this, 22), Ui.Dp(this, 20), Ui.Dp(this, 16));
        header.AddView(Ui.Label(this, "CircleAI", 24f, Ui.Ink, bold: true));
        var tagline = Ui.Label(this, "Free AI that runs on your phone", 14f, Ui.InkSoft);
        tagline.SetPadding(0, Ui.Dp(this, 4), 0, 0);
        header.AddView(tagline);
        root.AddView(header, Ui.Fill());

        // transcript (fills the middle)
        _scroll = new ScrollView(this);
        _scroll.VerticalScrollBarEnabled = false;      // house rule: no visible scrollbars
        _transcript = new TextView(this) { TextSize = 15f };   // 13sp was unreadable on the P30
        _transcript.SetTextColor(Ui.Ink);
        _transcript.SetLineSpacing(0f, 1.25f);
        _transcript.SetPadding(Ui.Dp(this, 18), Ui.Dp(this, 16),
                               Ui.Dp(this, 18), Ui.Dp(this, 16));
        _transcript.SetTextIsSelectable(true);
        _scroll.AddView(_transcript);
        root.AddView(_scroll, Ui.Fill(1f));

        // Utility-probe row — its OWN line above the input, so the button strip
        // never crowds the text box off the screen. A phone fits ~4 buttons per
        // row (1080 px portrait); keeping the always-available probes here and the
        // model-driven Talk/TTS in the input row below means neither line ever
        // overflows — no horizontal scroll to reach a button (voice builds used to
        // push Send + TTS off the right edge). Weighted so the three share the row.
        // Every one of these used to be an in-house word — Sweep, Caps, Vision,
        // TTS. Nobody outside this project could guess what any of them did. They
        // now say what happens when you press them.
        var probes = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        probes.SetBackgroundColor(Ui.Surface);
        probes.SetPadding(Ui.Dp(this, 12), Ui.Dp(this, 10), Ui.Dp(this, 12), Ui.Dp(this, 4));

        var cell = new Func<LinearLayout.LayoutParams>(() =>
        {
            var p = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            p.SetMargins(Ui.Dp(this, 4), 0, Ui.Dp(this, 4), Ui.Dp(this, 6));
            return p;
        });

        // Needs no model and no network, so it answers immediately — the right
        // thing to offer while the chat model is still downloading.
        _cv = Ui.Action(this, "What it can do", primary: false);
        _cv.Enabled = true;
        _cv.Click += (s, e) => RunCapabilities();
        probes.AddView(_cv, cell());

        // Reads an image on the device (fetches a small vision model on first tap).
        _vision = Ui.Action(this, "Read an image", primary: false);
        _vision.Enabled = true;
        _vision.Click += (s, e) => RunVision();
        probes.AddView(_vision, cell());


#if IT_VOICE_ANDROID
        // THE headline feature, so it gets a row to itself above the others rather
        // than a third of one. Sharing the row wrapped the word to "Languag / es",
        // which is the sort of detail that decides whether somebody trusts the rest.
        var hero = new LinearLayout(this) { Orientation = Orientation.Vertical };
        hero.SetBackgroundColor(Ui.Surface);
        hero.SetPadding(Ui.Dp(this, 16), Ui.Dp(this, 12), Ui.Dp(this, 16), 0);
        var languages = Ui.Action(this, "Speak in 74 languages", primary: true);
        languages.Click += (s, e) => StartActivity(new Intent(this, typeof(LanguagePickerActivity)));
        hero.AddView(languages, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        root.AddView(hero, Ui.Fill());
#endif

        root.AddView(probes, Ui.Fill());

        // Two per row, not three. At 1080 px three of these labels ellipsized to
        // "What it…", "Read a…", "Use the…" — every button on the screen truncated,
        // which is worse than the jargon it replaced. Two per row leaves each one
        // enough width to say what it does.
        var probes2 = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        probes2.SetBackgroundColor(Ui.Surface);
        probes2.SetPadding(Ui.Dp(this, 12), 0, Ui.Dp(this, 12), Ui.Dp(this, 8));

#if IT_VOICE_ANDROID
        _talk = Ui.Action(this, "Use the mic", primary: false);
        _talk.Enabled = false;
        _talk.Click += (s, e) => ToggleVoiceLoop();
        probes2.AddView(_talk, cell());
#endif

        // A developer diagnostic, kept but not competing with the real features.
        _tools = Ui.Action(this, "Run the tool check", primary: false);
        _tools.Enabled = false;
        _tools.Click += (s, e) => RunFullSweep();
        probes2.AddView(_tools, cell());
        root.AddView(probes2, Ui.Fill());

        // input row — the text box plus the model-driven actions (Talk/TTS in
        // voice builds) and Send. With the probes on their own line above, this
        // holds the weighted input + at most three buttons, which fits portrait.
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetBackgroundColor(Ui.Surface);
        row.SetPadding(Ui.Dp(this, 16), Ui.Dp(this, 6), Ui.Dp(this, 16), Ui.Dp(this, 16));
        row.SetGravity(GravityFlags.CenterVertical);

        // The hint used to be "Say something to IT!" and the field was so narrow it
        // rendered as "Say some" — the app's own prompt to the user was cut off.
        _input = new EditText(this) { Hint = "Type a message", Enabled = false };
        _input.SetTextColor(Ui.Ink);
        _input.SetHintTextColor(Ui.InkSoft);
        _input.SetSingleLine(true);
        _input.TextSize = 16f;
        _input.Background = Ui.Rounded(this, Ui.Raised, 10f);
        _input.SetPadding(Ui.Dp(this, 14), Ui.Dp(this, 12), Ui.Dp(this, 14), Ui.Dp(this, 12));
        _input.SetMinimumHeight(Ui.Dp(this, 48));
        _input.ImeOptions = ImeAction.Send;
        // Send on the IME action OR a plain Enter. (A hardware/adb Enter arrives
        // with an unspecified action id, so checking ImeAction.Send alone misses it.)
        _input.EditorAction += (s, e) =>
        {
            var isEnter = e.Event is not null && e.Event.KeyCode == Keycode.Enter;
            if (e.ActionId is ImeAction.Send or ImeAction.Done or ImeAction.Unspecified || isEnter)
            {
                Send();
                e.Handled = true;
            }
        };
        var inputLp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        inputLp.RightMargin = Ui.Dp(this, 8);
        row.AddView(_input, inputLp);

#if IT_VOICE_ANDROID
        // The microphone lives with the other feature buttons, not in the typing
        // row. Sharing that row with the text box squeezed the box until its own
        // placeholder read "Type a messag" — the input is the most-used control on
        // the screen and should not lose space to anything.
        _talk.Enabled = false;

        // The old "TTS" button lives on in the Languages screen, which does the
        // same thing but lets the user choose which of the 74 they hear. Keeping a
        // second, English-only speak button here would just be the worse version of
        // the feature sitting next to the better one.
        _tts = Ui.Action(this, "TTS", primary: false);
        _tts.Visibility = ViewStates.Gone;
#endif

        _send = Ui.Action(this, "Send", primary: true);
        _send.Enabled = false;
        _send.Click += (s, e) => Send();
        row.AddView(_send, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        root.AddView(row, Ui.Fill());

        SetContentView(root);

        // Let a script run the TTS probe directly:
        //   adb shell am start -n <pkg>/.MainActivity --ez run_tts true
        //
        // Sweeping thirty-one languages otherwise means synthesising a tap at fixed
        // screen coordinates, once per language. That is not a test — it is a guess
        // about where a button is. It has already misfired: after this app lost
        // focus the taps went into a DIFFERENT app entirely and opened its country
        // picker, while the sweep reported progress. An explicit trigger cannot
        // land in the wrong place.
        // If the last run left a breadcrumb, it died without running a single
        // handler — a stack overflow, an OOM kill, or a native crash. That is the
        // only evidence such a death leaves, so say it plainly on the way back up.
        try
        {
            var ext = GetExternalFilesDir(null)?.AbsolutePath;
            if (!string.IsNullOrEmpty(ext))
            {
                var stateDir = System.IO.Path.Combine(ext, "vut");

                // Every diagnostic lands here, wherever the failing asset lives.
                CircleAI.Samples.It.DeviceDiagnostics.DiagnosticsDirectory = stateDir;

                var died = CircleAI.Samples.It.DeviceDiagnostics.PreviousCrash(stateDir);
                if (died is not null)
                {
                    Append($"\n⚠ THE PREVIOUS RUN DIED — no handler ran.\n" +
                           $"  it was in: {died}\n" +
                           $"  that means a stack overflow, an out-of-memory kill, or a native crash;\n" +
                           $"  none of those are catchable, so this note is the only record.\n\n");
                    CircleAI.Samples.It.DeviceDiagnostics.EndRisky(stateDir);
                }
            }
        }
        catch { /* a diagnostic must never be the reason startup fails */ }

        // The resident device service, on demand:
        //   --ez run_service true    start it, bind, report what the phone reports
        //   --ez stop_service true   stop it and release the models
        //
        // Behind a flag because this sample is also the voice test rig, and a
        // second process holding a 122 MB model while the rig loads its own is the
        // fastest way to OOM a P30.
        if (Intent?.GetBooleanExtra("stop_service", false) == true) StopDeviceService();
        if (Intent?.GetBooleanExtra("run_service",  false) == true) RunDeviceService();

#if IT_VOICE_ANDROID
        if (Intent?.GetBooleanExtra("run_tts", false) == true) RunTts();
#endif
    }

    CircleAI.Device.CircleNeuronConnection? _neuron;

    /// <summary>
    /// Starts the resident service, binds to it, and writes down what happened.
    /// </summary>
    /// <remarks>
    /// The report goes to the EXTERNAL dir for the same reason every other one
    /// does: a Release APK will not surrender its private FilesDir over adb, and a
    /// claim that can only be screenshotted is a claim nobody can check.
    /// </remarks>
    async void RunDeviceService()
    {
        var log = new System.Text.StringBuilder();
        void Say(string s) { Append(s + "\n"); log.AppendLine(s); }

        try
        {
            Say("===== RESIDENT DEVICE SERVICE =====");
            Say(CircleAI.Device.AndroidDeviceMemory.Describe());

            // What the service will host. The sample has no chat model staged, so
            // this is the same AIOptions the typed UI uses — the point being that
            // ONE process owns it, not that this particular brain is special.
            CircleAI.Device.CircleNeuronService.OptionsFactory ??= () => new CircleAI.Hosting.AIOptions
            {
                ModelStorageDirectory = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                    "CircleAI", "Models"),
            };

            Say("[service] starting + binding…");
            var started = System.Diagnostics.Stopwatch.StartNew();
            var (node, connection) = await CircleAI.Device.CircleNeuronConnection.ConnectAsync(
                this, TimeSpan.FromSeconds(90));
            _neuron = connection;

            Say($"[service] status : {CircleAI.Device.CircleNeuronService.Status}");
            Say($"[service] elapsed: {started.Elapsed:mm\\:ss}");
            Say(node is null
                ? "[service] NO NODE — see status above"
                : $"[service] node   : {node.Id}  ready={node.IsReady}  engine={node.EngineLabel}");

            // The claim worth proving: bind AGAIN and get the SAME object back. If
            // these differ, every app is loading its own copy and the service is
            // decoration.
            var (again, second) = await CircleAI.Device.CircleNeuronConnection.ConnectAsync(
                this, TimeSpan.FromSeconds(10));
            Say(ReferenceEquals(node, again)
                ? "[service] SHARED — a second bind returned the same node"
                : "[service] NOT SHARED — a second bind built another node");
            second.Dispose();
        }
        catch (Exception ex)
        {
            Say($"[service] FAILED: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                var ext = GetExternalFilesDir(null)?.AbsolutePath;
                if (!string.IsNullOrEmpty(ext))
                    await System.IO.File.WriteAllTextAsync(
                        System.IO.Path.Combine(ext, "service-result.txt"), log.ToString());
            }
            catch { /* mirroring is a convenience, never fail the run */ }
        }
    }

    void StopDeviceService()
    {
        try
        {
            _neuron?.Dispose();
            _neuron = null;
            CircleAI.Device.CircleNeuronService.Stop(this);
            Append("[service] stopped — models released\n");
        }
        catch (Exception ex) { Append($"[service] stop failed: {ex.Message}\n"); }
    }

    // The offline capability sweep — runnable the instant the app opens (no
    // model, no network). Two halves that together prove the recent work on the
    // actual phone: (1) the on-device model-selector verdict for every modality,
    // so the catalogued vision/TTS/ASR models are shown resolving and device-fit
    // gating is honest (vision NothingFits on a 3 GB phone, not a crash); (2)
    // every document KIND rendered to a real PDF in internal storage.
    async void RunCapabilities()
    {
        _cv.Enabled = false;
        Append("\n===== ON-DEVICE CAPABILITY SWEEP =====\n");
        // Accumulate the whole sweep so it can be pulled off the phone as one
        // file — a clean end-to-end check over adb, not a screen scrape.
        var log = new System.Text.StringBuilder();
        log.Append("CircleAI on-device capability sweep\n\n");
        try
        {
            Append("\n[models] what this phone can run (from the embedded registry):\n");
            var report = await Task.Run(CapabilitySweep.BuildModelReport);
            Append(report);
            log.Append("[models]\n").Append(report).Append('\n');

            Append("\n[documents] rendering every kind to PDF (pure-managed, offline):\n");
            log.Append("[documents]\n");
            // Render off the UI thread — PDFsharp is synchronous CPU work.
            var docs = await Task.Run(() => CapabilitySweep.RenderDocumentSuiteAsync());
            foreach (var (label, doc) in docs)
            {
                var path = System.IO.Path.Combine(FilesDir!.AbsolutePath, doc.SuggestedFileName);
                await System.IO.File.WriteAllBytesAsync(path, doc.Bytes);
                var line = $"  {label,-13} {doc.Bytes.Length,8:N0} bytes  {doc.SuggestedFileName}\n";
                Append(line);
                log.Append(line);
            }

            Append("\n[media] rendering an artifact from each media library:\n");
            log.Append("\n[media]\n");
            var media = await Task.Run(() => CapabilitySweep.RenderMediaSuiteAsync());
            foreach (var (label, bytes, fileName) in media)
            {
                var path = System.IO.Path.Combine(FilesDir!.AbsolutePath, fileName);
                await System.IO.File.WriteAllBytesAsync(path, bytes);
                var line = $"  {label,-13} {bytes.Length,8:N0} bytes  {fileName}\n";
                Append(line);
                log.Append(line);
            }

            Append("\n[systems] one verdict from each remaining library:\n");
            log.Append("\n[systems]\n");
            var verdicts = await Task.Run(() => CapabilitySweep.BuildSystemVerdictsAsync());
            foreach (var (label, verdict) in verdicts)
            {
                var line = $"  {label,-20} {verdict}\n";
                Append(line);
                log.Append(line);
            }

            var reportPath = System.IO.Path.Combine(FilesDir!.AbsolutePath, "capability-report.txt");
            await System.IO.File.WriteAllTextAsync(reportPath, log.ToString());

            // This used to print two `adb exec-out run-as` command lines. The person
            // reading it is holding a phone; telling them to open a desktop shell is
            // the app admitting it was only ever built for its own authors.
            Append("\nDone. The full report and the PDFs are saved on this phone.\n");
        }
        catch (Exception ex)
        {
            // Full exception on failure — a device-only issue (font resource not
            // found on ARM, a registry parse error, etc.) needs the detail.
            Append($"[caps] FAILED: {ex}\n");
        }
        finally
        {
            _cv.Enabled = true;
        }
        Append("===== CAPABILITY SWEEP DONE =====\n\n");
    }

    // On-device vision — the real proof for #51. Renders a test image, then runs
    // the best-fitting VLM (SmolVLM-256M on this phone; the 3B is gated off free
    // RAM) on it via KimiVlGenerator. First tap downloads the ~311 MB model over
    // Wi-Fi; later taps load from cache.
    async void RunVision()
    {
        _vision.Enabled = false;
        Append("\n===== ON-DEVICE VISION =====\n");
        Append("[vision] rendering a test image, then running a VLM on it…\n");
        try
        {
            var img = await Task.Run(() => CapabilitySweep.MakeTestImagePng());
            Append($"[vision] test image: {img.Length:N0} bytes PNG\n");

            var (model, desc) = await Task.Run(() =>
                CapabilitySweep.RunVisionProbeAsync(ApplicationInfo?.NativeLibraryDir, img, line => Append(line + "\n")));

            Append($"\n[vision] model:       {model}\n");
            Append($"[vision] DESCRIPTION: {desc}\n");

            var path = System.IO.Path.Combine(FilesDir!.AbsolutePath, "vision-result.txt");
            await System.IO.File.WriteAllTextAsync(path, $"model: {model}\n\n{desc}\n");
            Append("[vision] OK — a VLM ran on the phone. files/vision-result.txt\n");
        }
        catch (Exception ex)
        {
            // Full exception — a device-only vision issue (native VLM load, the
            // MNN image bridge, a non-Qwen template) needs the detail.
            Append($"[vision] FAILED: {ex}\n");
        }
        finally
        {
            _vision.Enabled = true;
        }
        Append("===== VISION DONE =====\n\n");
    }

    async void Send()
    {
        var text = _input.Text?.Trim();
        if (string.IsNullOrEmpty(text) || _session is null) return;

        _input.Text = "";
        _send.Enabled = false;
        Append($"you > {text}\n");

        try
        {
            // Routing line arrives first, then the reply streams in chunk by chunk.
            // Real decoding blocks in native code, so run the turn off the UI
            // thread — Append() already marshals each chunk back for rendering.
            //
            // onThinking is supplied, so the model's <think> trace streams too —
            // rendered dimmed so it reads as scratchpad, not answer. Without it
            // the plain StreamAsync path filters reasoning out entirely.
            await Task.Run(() => _session.RunTurnStreamingAsync(
                text,
                line => Append(line + "\n"),
                chunk => Append(chunk),
                think => AppendThinking(think)));
            Append("\n");
        }
        catch (Exception ex)
        {
            Append("\nERROR: " + ex.Message + "\n");
        }

        _send.Enabled = true;
        _input.RequestFocus();   // keep the caret in the box so you can just keep typing
    }

    /// <summary>
    /// Runs the tool probes and reports the thing that actually matters: whether
    /// a tool RAN. A confident, plausible answer with an empty invocation list
    /// means the model invented the number — the exact failure a fake generator
    /// emitting a canned &lt;tool_call&gt; can never expose.
    /// </summary>
    async void RunFullSweep()
    {
        if (_session is null) return;

        _tools.Enabled = false;
        _send.Enabled  = false;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var battery = ReadBatteryPercent();

        Append("\n===== CIRCLEAI ON-DEVICE SWEEP =====\n");
        Append($"status: {_session.StatusLine}\n");
        Append($"ground truth: battery={(battery is null ? "?" : battery + "%")}, SKU-1001=R249.99\n");
        Append("(the model cannot know either without calling a tool)\n\n");

        try
        {
            Append("[1] multi-turn memory\n");
            await Task.Run(() => _session!.RunTurnAsync("my name is Thabo", l => Append(l + "\n")));
            await Task.Run(() => _session!.RunTurnAsync("what is my name?",  l => Append(l + "\n")));

            Append("\n[2] concierge routing\n");
            await Task.Run(() => _session!.RunTurnAsync("hi", l => Append(l + "\n")));
            await Task.Run(() => _session!.RunTurnAsync(
                "solve x^2 = 49 step by step", l => Append(l + "\n")));

            // Expected to disappoint: ISkillStore is never populated, so IT!
            // has no factual basis for describing itself. Included precisely so
            // the gap shows up in the transcript rather than being asserted.
            Append("\n[3] self-knowledge\n");
            await Task.Run(() => _session!.RunTurnAsync("what can you do?", l => Append(l + "\n")));

            Append("\n[4] tool calling\n");
            foreach (var probe in ItSession.ToolProbes)
            {
                Append($"you > {probe}\n");
                var turn = await Task.Run(() => _session!.RunToolTurnAsync(probe));

                Append(turn.ToolsCalled.Count == 0
                    ? "   !! NO tool call — answered from nothing\n"
                    : "   -> called: " + string.Join(", ", turn.ToolsCalled) + "\n");
                Append($"IT! > {turn.Answer}\n");
            }

            // Specialist should be evicted first and the generalist keep
            // serving — so this turn must still answer, not throw.
            Append("\n[5] brownout under Critical pressure\n");
            await _session!.SignalCriticalMemoryAsync();
            Append("   fired Critical\n");
            await Task.Run(() => _session!.RunTurnAsync("still there?", l => Append(l + "\n")));
            await _session!.ClearMemoryPressureAsync();
            Append("   cleared\n");
        }
        catch (Exception ex)
        {
            Append("\nSWEEP ERROR: " + ex + "\n");
        }

        sw.Stop();
        Append($"\n===== SWEEP DONE in {sw.Elapsed:mm\\:ss} =====\n\n");

        _tools.Enabled = true;
        _send.Enabled  = true;
    }

#if IT_VOICE_ANDROID
    CircleAI.Voice.VoiceLoop? _voiceLoop;

    // How THIS person says borrowed words, learned from listening to them. Kept in
    // private storage: it never leaves the device and is never merged with anyone
    // else's, so two phones will pronounce the same word differently. Loaded once
    // and held, because both the ear (learning) and the mouth (speaking) use it.
    CircleAI.Voice.PersonalRespellings? _respellings;

    string RespellingsPath =>
        System.IO.Path.Combine(FilesDir!.AbsolutePath, "respellings.json");

    /// <summary>
    /// The language being spoken, which decides whether these spellings apply at all.
    /// </summary>
    /// <remarks>
    /// The test harness passes it (<c>--es tts_host zu</c>); otherwise it comes from
    /// the phone's own language. A phone set to English gets an empty table and
    /// learns nothing, which is correct — these are isiZulu letter values, and
    /// applying them to an English speaker's transcript would teach nonsense.
    /// </remarks>
    string VoiceHostLanguage =>
        Intent?.GetStringExtra("tts_host")
        ?? Java.Util.Locale.Default?.Language
        ?? "en";

    CircleAI.Voice.PersonalRespellings Respellings =>
        _respellings ??= CircleAI.Voice.PersonalRespellings.Load(RespellingsPath);

    /// <summary>
    /// English pronunciation for words no table has, or null when unavailable.
    /// </summary>
    /// <remarks>
    /// Out-of-process espeak — it is GPL-3.0 and CircleAI never links it. Absent
    /// (the separate app is not installed) the curated table still works and
    /// unknown words are left as written.
    /// </remarks>
    CircleAI.Voice.IPhonemizer? TryEnglishPhonemizer()
    {
        try { return CircleAI.Samples.It.Voice.ItSpeaker.MobilePhonemizerFactory?.Invoke("en-us"); }
        catch { return null; }
    }

    /// <summary>
    /// Learns from one thing the person said, and remembers it across restarts.
    /// </summary>
    /// <remarks>
    /// Written only when something actually changed. The commonest transcript by
    /// far contains no borrowed word at all, and re-serialising the table on every
    /// utterance would put a flash write on the critical path of every turn — on a
    /// phone whose storage is the slowest thing in it.
    /// </remarks>
    /// <returns>The words whose spelling changed because of this utterance.</returns>
    System.Collections.Generic.IReadOnlyList<string> LearnFromWhatTheySaid(string? heard, string hostLanguage)
    {
        var none = System.Array.Empty<string>();
        try
        {
            var table = CircleAI.Voice.LoanwordRespeller.Table(hostLanguage);
            if (table.Count == 0) return none;      // not a language these spellings fit

            var changed = Respellings.LearnFrom(heard, table);

            // Save on ANY change, not only a changed spelling. The sixth hearing
            // confirms without altering anything, and partial progress towards a
            // word is worth keeping — saving only on `changed` meant a word could
            // never reach a persisted Confirmed state on the phone.
            if (!Respellings.HasUnsavedChanges) return none;
            Respellings.Save(RespellingsPath);
            if (changed.Count == 0) return none;
            foreach (var word in changed)
                Append($"[voice] learned: {word} → {Respellings.Respell(word)}\n");
            return changed;
        }
        catch (Exception ex)
        {
            // Learning is a bonus, never a reason to lose a turn. A full disk or a
            // locked file must not take the conversation down with it.
            Append($"[voice] could not learn: {ex.Message}\n");
            return none;
        }
    }
    // Held as fields, not locals: these own native ONNX/whisper handles that
    // must outlive the setup method and be disposed deterministically.
    CircleAI.Samples.It.Voice.ItSpeaker?  _speaker;
    CircleAI.Samples.It.Voice.ItListener? _listener;

    /// <summary>
    /// Hands-free mode: wake word -> VAD -> Whisper -> IT! -> Piper -> speaker,
    /// using the real microphone and speaker. Only compiled when the APK is
    /// built with -p:ItVoiceOnAndroid=true, because it pulls ONNX Runtime and
    /// whisper.cpp natives into the package.
    /// </summary>
    /// <summary>
    /// Installs a side-loaded wake bundle into the model store, if one is there.
    /// </summary>
    /// <remarks>
    /// Verified against the catalogue's published SHA-256 before it is trusted —
    /// a model that arrived by Bluetooth or memory card is held to exactly the
    /// standard a downloaded one is. Silent when there is nothing to import, and
    /// never fatal: the loop can still fall back to fetching one.
    /// </remarks>
    async Task ImportSideloadedWakeWordAsync(string store)
    {
        try
        {
            var folder = ResidentWakeWord.SideloadedBundleFolder(this);
            if (folder is null) return;

            using var registry = new CircleAI.Core.Models.ModelRegistryService();
            var importer = new CircleAI.Inference.SideloadedBundleImporter(registry, store);
            var result = await importer.ImportAsync("KWS-Zipformer-HeyB", folder);

            if (result.Usable)
                Append($"[voice] wake word: {result.Detail} ({result.Files} files verified)\n");
            else
                Append($"[voice] side-loaded wake word not used: {result.Detail}\n");
        }
        catch (Exception ex)
        {
            Append($"[voice] side-load check skipped: {ex.Message}\n");
        }
    }

    async void ToggleVoiceLoop()
    {
        // Captured into a local: the brain lambda below outlives this method,
        // and the compiler cannot carry a null-check on a mutable field into a
        // closure (CS8602). The local is provably non-null for the loop's life.
        var brain = _session;
        if (brain is null) return;

        if (_voiceLoop is not null)
        {
            await _voiceLoop.DisposeAsync();
            _voiceLoop = null;

            // Release the mic and the two models — leaving them resident would
            // hold AudioRecord open and keep ~100 MB of ASR/TTS weights in a
            // process that already runs the chat brain.
            if (_listener is not null) { await _listener.DisposeAsync(); _listener = null; }
            _speaker?.Dispose(); _speaker = null;

            RunOnUiThread(() => _talk.Text = "Use the mic");
            Append("\n[voice] stopped listening\n");
            return;
        }

        // RECORD_AUDIO is a runtime permission; without it AudioRecord silently
        // yields nothing and the loop would look broken rather than blocked.
        if (CheckSelfPermission(Android.Manifest.Permission.RecordAudio) != Android.Content.PM.Permission.Granted)
        {
            RequestPermissions([Android.Manifest.Permission.RecordAudio], 1001);
            Append("\n[voice] microphone permission requested — tap Talk again once granted\n");
            return;
        }

        Append("\n[voice] setting up ears and mouth…\n");
        try
        {
            var store = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "CircleAI", "Models");

            var (speaker, sStatus) = await CircleAI.Samples.It.Voice.ItSpeaker.TryCreateAsync(store, s => Append(s + "\n"));
            if (speaker is null) { Append($"[voice] OFF: {sStatus}\n"); return; }
            _speaker = speaker;

            var (listener, lStatus) = await CircleAI.Samples.It.Voice.ItListener.TryCreateAsync(store, s => Append(s + "\n"));
            if (listener is null) { Append($"[voice] OFF: {lStatus}\n"); return; }
            _listener = listener;

            // A bundle the owner already copied onto the phone counts as installed.
            // Without this the loop asks the catalogue for a wake model that is
            // sitting on the device already, and spends their data re-fetching it —
            // or, as happened here, fails outright because the bucket is behind.
            await ImportSideloadedWakeWordAsync(store);

            // One mic, one Whisper instance, shared by the wake detector and the
            // pipeline — a second AudioRecord on the same device would fail to
            // open, and a second Whisper would double the RAM for no gain.
            var mic = new AndroidAudioCapture();
            // Access list. One phrase today (the product default); a host that
            // wants to limit who can drive it by voice passes more here.
            var (wake, wakeReason) = await listener.CreateWakeDetectorAsync(mic, storageDir: store);
            Append($"[voice] wake: {wakeReason}\n");
            var pipeline = new CircleAI.Voice.VoicePipeline(wake, listener.Transcriber, mic);

            _voiceLoop = new CircleAI.Voice.VoiceLoop(
                pipeline,
                // The brain: same ItSession the typed UI uses, so voice turns
                // land in the same memory and see the same tools.
                async (heard, ct) =>
                {
                    Append($"\nyou (voice): {heard}\n");
                    return await brain.RunTurnStreamingAsync(
                        heard,
                        line  => Append(line + "\n"),
                        chunk => Append(chunk),
                        think => AppendThinking(think)).ConfigureAwait(false);
                },
                // The mouth, respelling borrowings on the way out — including
                // anything this person has taught us. Without this the learning
                // would change nothing anyone hears.
                speaker.RespellingEngine(
                    VoiceHostLanguage,
                    Respellings,
                    TryEnglishPhonemizer()),
                new AndroidAudioPlayer());

            _voiceLoop.Faulted += (s, ex) => Append($"[voice] turn failed: {ex.Message}\n");

            // THE LEARNING SEAM. Every turn already produces a transcript of what
            // this person said, in their own spelling — so a borrowed word arrives
            // written the way THEY say it, at no cost to them. They are not
            // correcting anything or filling in a form; they asked their phone to
            // do something, and the answer to "how do you say WiFi" came with it.
            _voiceLoop.Exchanged += (s, e) => LearnFromWhatTheySaid(e.Heard, VoiceHostLanguage);
            await _voiceLoop.StartAsync();
            RunOnUiThread(() => _talk.Text = "Stop listening");
            Append("[voice] listening — say \"hey b\"\n");
        }
        catch (Exception ex)
        {
            Append($"[voice] failed: {ex.Message}\n");
        }
    }

    // On-device TTS — the pull-able proof for #56. Non-interactive: no mic, no
    // "hey b". Selects the best Piper voice this phone can hold, downloads it
    // (~113 MB first tap), loads it through ONNX Runtime, and synthesises a fixed
    // phrase to a WAV. Reports exactly which stage it reached; on mobile the last
    // step (grapheme→phoneme) needs libespeak-ng, which this build does not bundle
    // (and, being GPL-3.0, cannot be linked in-process without contaminating the
    // permissive licence) — so the honest result is "everything up to synthesis
    // ran on the phone; G2P is the wall," captured verbatim from the device.
    async void RunTts()
    {
        _tts.Enabled = false;
        Append("\n===== ON-DEVICE TTS =====\n");
        Append("[tts] selecting a voice, downloading it, loading it, then synthesising…\n");
        try
        {
            var store = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "CircleAI", "Models");
            var wavPath = System.IO.Path.Combine(FilesDir!.AbsolutePath, "tts-result.wav");

            // Voice-under-test: if a model was sideloaded to files/vut/model.onnx
            // (e.g. a kasanoma African voice pushed over adb), prove THAT on the phone
            // — any language, using the espeak voice from its own config — instead of
            // the catalogued English default. The phrase comes from files/vut/phrase.txt.
            // ToucanTTS (3-stage) takes priority when its assets are sideloaded:
            // it is the only permissive voice for isiZulu, Sepedi, siSwati and
            // Tshivenda, and it is driven by OUR NchltPhonemizer, not a neural G2P.
            // CATALOGUE PROOF, and it must come before every sideload branch below.
            // Those branches load a model already on the phone, which proves the
            // engine while skipping the download entirely — so if a stale sideloaded
            // voice is lying around, the "it fetched from our bucket" claim would be
            // made by a run that never fetched anything.
            //   adb shell am start -n <pkg>/.MainActivity --ez run_tts true --es tts_lang zu
            var catalogueLang = Intent?.GetStringExtra("tts_lang");
            if (!string.IsNullOrWhiteSpace(catalogueLang))
            {
                var catPhrase = Intent?.GetStringExtra("tts_phrase") ?? "Sawubona umhlaba.";

                // Optional speaker id. The South African model carries 130 voices
                // with no published mapping to language, so choosing the one that
                // sounds native is an ear exercise, not a lookup: this lets a
                // sweep play the same sentence in several of them for comparison.
                //   --es tts_lang zu --ei tts_speaker 118
                long? speaker = Intent?.HasExtra("tts_speaker") == true
                    ? Intent.GetIntExtra("tts_speaker", 0)
                    : null;
                if (speaker is not null) Append($"[tts] speaker {speaker}\n");

                // Force a language id, overriding what the tag resolves to. Needed
                // to hear one model speak a language it is not selected for: asking
                // for "en" picks the best English voice in the WHOLE catalogue (the
                // 22 kHz Piper one), so the South African model's own English can
                // only be reached by pinning its id.
                //   --es tts_lang zu --ei tts_langid 1   → SA model, English
                long? forcedLangId = Intent?.HasExtra("tts_langid") == true
                    ? Intent.GetIntExtra("tts_langid", 0)
                    : null;
                if (forcedLangId is not null) Append($"[tts] forced language id {forcedLangId}\n");

                // Which voice says the embedded English. Needed because every
                // speaker in the SA model recorded exactly one language, so an
                // English span has to come from a speaker who actually has English
                // — and which of the 130 those are is not published anywhere, so it
                // is auditioned rather than looked up.
                //   --es tts_lang zu --ei tts_speaker 129 --ei tts_engspk 18
                long? engSpeaker = Intent?.HasExtra("tts_engspk") == true
                    ? Intent.GetIntExtra("tts_engspk", 0)
                    : null;
                if (engSpeaker is not null) Append($"[tts] English spoken by speaker {engSpeaker}\n");

                Append($"[tts] catalogue proof: '{catalogueLang}' — select, download, speak\n");
                var crep = await CircleAI.Samples.It.Voice.ItTtsProbe.RunCataloguedAsync(
                    store, catalogueLang!, catPhrase, wavPath, s => Append("  " + s + "\n"),
                    default, speaker, forcedLangId, engSpeaker);
                var extOut = GetExternalFilesDir(null)?.AbsolutePath;
                if (!string.IsNullOrEmpty(extOut))
                {
                    try
                    {
                        await System.IO.File.WriteAllTextAsync(
                            System.IO.Path.Combine(extOut, "catalogue-result.txt"), crep);
                        if (System.IO.File.Exists(wavPath))
                            System.IO.File.Copy(wavPath,
                                System.IO.Path.Combine(extOut, "catalogue-result.wav"), true);
                    }
                    catch { /* mirroring is a convenience, never fail the run */ }
                }
                Append("\n" + crep);
                if (System.IO.File.Exists(wavPath)) await PlayWavAsync(wavPath);
                return;
            }

            var extRoot = GetExternalFilesDir(null)?.AbsolutePath;
            if (!string.IsNullOrEmpty(extRoot))
            {
                var toucanDir = System.IO.Path.Combine(extRoot, "toucan");
                var stageA = System.IO.Path.Combine(toucanDir, "toucan_stage_a.onnx");
                var langFile = System.IO.Path.Combine(toucanDir, "lang.txt");
                if (System.IO.File.Exists(stageA) && System.IO.File.Exists(langFile))
                {
                    var lang = (await System.IO.File.ReadAllTextAsync(langFile)).Trim();
                    var phraseF = System.IO.Path.Combine(toucanDir, "phrase.txt");
                    var toucanPhrase = System.IO.File.Exists(phraseF)
                        ? (await System.IO.File.ReadAllTextAsync(phraseF)).Trim()
                        : "Sawubona umhlaba.";
                    Append($"[tts] ToucanTTS assets found — proving {lang} on the phone\n");
                    var trep = await CircleAI.Samples.It.Voice.ItTtsProbe.RunToucanAsync(
                        toucanDir, System.IO.Path.Combine(toucanDir, "nchlt"), lang,
                        wavPath, toucanPhrase, s => Append("  " + s + "\n"));
                    try { await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(toucanDir, "result.txt"), trep); }
                    catch { }
                    Append("\n" + trep);
                    if (System.IO.File.Exists(wavPath)) await PlayWavAsync(wavPath);
                    return;
                }
            }

            var vut = System.IO.Path.Combine(FilesDir!.AbsolutePath, "vut", "model.onnx");

            // Also honour the app's EXTERNAL files dir. `run-as` only works on a
            // debuggable build, so on a Release APK the private FilesDir above
            // cannot be written over adb at all; /sdcard/Android/data/<pkg>/files
            // can, with no root and no debug build. Sideloading a voice must not
            // require shipping a debuggable APK.
            if (!System.IO.File.Exists(vut))
            {
                var ext = GetExternalFilesDir(null)?.AbsolutePath;
                if (!string.IsNullOrEmpty(ext))
                {
                    var extVut = System.IO.Path.Combine(ext, "vut", "model.onnx");
                    if (System.IO.File.Exists(extVut)) vut = extVut;
                }
            }

            // A whole language TOUR in one session, when files/vut/sweep.tsv is
            // present: each line is "code<TAB>langid<TAB>phrase".
            //
            // The alternative — a script that rewrites langid.txt and restarts the
            // app once per language — force-stops the process mid-sentence, throws
            // away a warm 122 MB session every time, and to anyone holding the
            // phone looks exactly like the app crash-looping. It also cut every
            // phrase off before it finished, because the report file is written
            // before playback begins. Walking the list in-process fixes all three.
            if (System.IO.File.Exists(vut))
            {
                var sweepFile = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(vut)!, "sweep.tsv");
                if (System.IO.File.Exists(sweepFile))
                {
                    await RunLanguageTourAsync(vut, wavPath, sweepFile);
                    return;
                }
            }

            if (System.IO.File.Exists(vut))
            {
                // Beside whichever model won above — private or external.
                var phraseFile = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(vut)!, "phrase.txt");
                var phrase = System.IO.File.Exists(phraseFile)
                    ? (await System.IO.File.ReadAllTextAsync(phraseFile)).Trim()
                    : "The quick brown fox jumps over the lazy dog.";
                Append("[tts] sideloaded voice-under-test found — proving it on the phone\n");

                // Three knobs for the dragged-first-syllable hunt, all optional and
                // all no-ops when absent:
                //   --ei tts_noisew 30   duration-predictor noise, as a percentage
                //   --ei tts_group  3    sentences synthesised per utterance
                //   --ei tts_leadpads 4  silent tokens before the first real sound
                float? tuneNoiseW = Intent?.HasExtra("tts_noisew") == true
                    ? Intent.GetIntExtra("tts_noisew", 80) / 100f
                    : null;
                var tuneGroup = Intent?.GetIntExtra("tts_group", 1) ?? 1;
                var tuneLead  = Intent?.GetIntExtra("tts_leadpads", 0) ?? 0;
                // Breathing room, in milliseconds, around the whole utterance.
                //   --ei tts_pre 250 --ei tts_post 400
                var tunePre   = Intent?.GetIntExtra("tts_pre", 0) ?? 0;
                var tunePost  = Intent?.GetIntExtra("tts_post", 0) ?? 0;
                // The language id embedded English switches TO, and optionally a
                // different speaker for it. Passing only the language keeps the
                // same voice — which is the test that matters for a blended
                // speaker: can one identity carry both languages, or does it
                // still need a second person for the English?
                //   --ei tts_englang 1 [--ei tts_engspk 13]
                long? vutEngLang = Intent?.HasExtra("tts_englang") == true
                    ? Intent.GetIntExtra("tts_englang", 1) : null;
                long? vutEngSpk  = Intent?.HasExtra("tts_engspk") == true
                    ? Intent.GetIntExtra("tts_engspk", 0) : null;
                // Cadence ratio as a percentage: how much slower the host
                // language runs than the borrowed one. 120 = the measured
                // isiZulu-to-English figure.  --ei tts_cadence 120
                var vutCadence = (Intent?.GetIntExtra("tts_cadence", 120) ?? 120) / 100f;
                // The HOST language, as a tag. The sideloaded path only knows a
                // numeric langid from langid.txt, and the loanword table is keyed
                // by language — without this the respelling silently never fires,
                // which looks exactly like the table being wrong.
                //   --es tts_host zu
                var vutHost = Intent?.GetStringExtra("tts_host") ?? "";
                // English pronunciation for words the loanword table has never
                // seen. Out-of-process espeak — it is GPL-3.0 and CircleAI never
                // links it. Absent (the separate app not installed), respelling
                // simply falls back to the language-switch path.
                CircleAI.Voice.IPhonemizer? engG2p = null;
                try { engG2p = CircleAI.Samples.It.Voice.ItSpeaker.MobilePhonemizerFactory?.Invoke("en-us"); }
                catch { /* no G2P app: the curated table still works */ }

                // Drive the learning from the command line, so adoption can be
                // proved on the phone without saying the same sentence five times
                // into a microphone.
                //
                //   --ez forget_learning true                     start from clean
                //   --es learn_heard "ngicela i-wayifayi ekhaya"  one transcript
                //   --ei learn_times 5                            how many hearings
                //
                // This calls the SAME method the voice loop calls with the
                // transcriber's output. The only thing not exercised is Whisper
                // producing the string — everything downstream of it is real:
                // the thresholds, the file in private storage, and the audio.
                // Collected into the report, not just the on-screen log. A Release
                // APK will not surrender its private FilesDir over adb, so a claim
                // that lives only in the UI can be checked by screenshot and
                // nothing else — which is how "it rendered" gets mistaken for "it
                // worked".
                var learnLog = new System.Text.StringBuilder();
                void Learn(string line) { Append(line + "\n"); learnLog.AppendLine(line); }

                if (Intent?.GetBooleanExtra("forget_learning", false) == true)
                {
                    if (System.IO.File.Exists(RespellingsPath)) System.IO.File.Delete(RespellingsPath);
                    _respellings = null;
                    Learn("[learn] forgot everything — starting clean");
                }

                var learnHeard = Intent?.GetStringExtra("learn_heard");
                if (!string.IsNullOrWhiteSpace(learnHeard))
                {
                    // Several transcripts separated by "|", so a SHARED PHONE can be
                    // put through in one run: three people saying the same borrowed
                    // word three different ways is the case that must teach nothing,
                    // and it cannot be shown with a single repeated sentence.
                    var heardList = learnHeard.Split('|', StringSplitOptions.RemoveEmptyEntries);
                    var times = Intent?.GetIntExtra("learn_times", 1) ?? 1;
                    var n = 0;
                    for (var round = 1; round <= times; round++)
                        foreach (var one in heardList)
                        {
                            var changed = LearnFromWhatTheySaid(one.Trim(), vutHost);
                            Learn($"[learn] hearing {++n}: \"{one.Trim()}\"" +
                                  (changed.Count == 0 ? "" : "  -> CHANGED " + string.Join(", ", changed)));
                        }
                }

                // What the table knows now, so the report shows the state machine
                // and not just its verdict — 3 of 5 hearings is a real state, and
                // a run that changed nothing must be distinguishable from one that
                // never counted anything.
                var learned = Respellings.All();
                Learn(learned.Count == 0
                    ? "[learn] table: empty"
                    : "[learn] table: " + string.Join("  ", learned.Select(w =>
                        $"{w.Word}={w.Spelling ?? "-"}({w.State}," +
                        $"{string.Join("/", w.Candidates.Select(c => $"{c.Key}:{c.Value}"))})")));

                var vrep = await CircleAI.Samples.It.Voice.ItTtsProbe.RunLocalAsync(
                    // "respelt X as Y" is the line that says which spelling the
                    // voice was actually handed, and it was going to the screen
                    // only. The summary underneath reports byte counts, which look
                    // identical whichever spelling won.
                    vut, wavPath, phrase,
                    s => { Append("  " + s + "\n"); learnLog.AppendLine("  " + s); },
                    ct: default,
                    langIdOverride: null, speakerOverride: null,
                    foreignLangId: vutEngLang, foreignSpeakerId: vutEngSpk,
                    noiseW: tuneNoiseW,
                    sentencesPerUtterance: tuneGroup,
                    leadInPads: tuneLead,
                    leadInSilenceMs: tunePre,
                    tailSilenceMs: tunePost,
                    cadenceRatio: vutCadence,
                    langTagForRespell: vutHost,
                    englishPhonemizer: engG2p,
                    // What this person has taught us by talking, which outranks
                    // both the shipped table and anything derived. Absent on a
                    // fresh install; it fills in as they use the phone.
                    personal: Respellings,
                    // How much to stretch a respelt word so every syllable is
                    // heard. 118 = 1.18x.   --ei tts_syllable 118
                    syllableFullness: (Intent?.GetIntExtra("tts_syllable", 118) ?? 118) / 100f);
                await System.IO.File.WriteAllTextAsync(
                    System.IO.Path.Combine(FilesDir!.AbsolutePath, "tts-result.txt"), vrep);

                // Mirror the report next to the sideloaded voice, in the EXTERNAL
                // dir. A Release APK cannot be read with `run-as`, so without this
                // a scripted sweep over many voices has no way to collect results
                // except screenshotting the phone once per language.
                try
                {
                    var extDir = System.IO.Path.GetDirectoryName(vut)!;
                    await System.IO.File.WriteAllTextAsync(
                        System.IO.Path.Combine(extDir, "result.txt"),
                        learnLog.Length == 0 ? vrep : learnLog.ToString() + "\n" + vrep);

                    // The learned table itself, so what the phone believes can be
                    // read rather than inferred from how the audio sounds. Removed
                    // when there is no table — a leftover copy beside a run that
                    // started clean is evidence of something that is not there.
                    var mirror = System.IO.Path.Combine(extDir, "respellings.json");
                    if (System.IO.File.Exists(RespellingsPath))
                        System.IO.File.Copy(RespellingsPath, mirror, true);
                    else if (System.IO.File.Exists(mirror))
                        System.IO.File.Delete(mirror);

                    // Mirror the AUDIO as well, not just the report. The report says
                    // how many bytes were written; only the waveform says whether the
                    // words are right. It was being written to the app's private
                    // FilesDir, which a Release APK will not surrender over adb — so
                    // every "it spoke" claim rested on a byte count nobody could hear.
                    if (System.IO.File.Exists(wavPath))
                        System.IO.File.Copy(wavPath, System.IO.Path.Combine(extDir, "tts-result.wav"), true);
                }
                catch { /* mirroring is a convenience, never fail the run */ }

                Append("\n" + vrep);

                // A WAV on disk proves synthesis; it does not prove the phone can
                // SPEAK. Play it out of the actual speaker — that is the only
                // result that settles "it talks on the device".
                if (System.IO.File.Exists(wavPath))
                    await PlayWavAsync(wavPath);
                Append("Saved on this phone.\n");
                return;
            }

            var report = await CircleAI.Samples.It.Voice.ItTtsProbe.RunAsync(
                store, wavPath, s => Append("  " + s + "\n"));

            var txtPath = System.IO.Path.Combine(FilesDir!.AbsolutePath, "tts-result.txt");
            await System.IO.File.WriteAllTextAsync(txtPath, report);

            Append("\n" + report);
            Append("Saved on this phone.\n");
        }
        catch (Exception ex)
        {
            Append($"[tts] FAILED: {ex}\n");
        }
        finally
        {
            _tts.Enabled = true;
        }
        Append("===== TTS DONE =====\n\n");
    }
#endif

#if IT_VOICE_ANDROID
    /// <summary>
    /// Play a 16-bit PCM WAV out of the phone's speaker via AudioTrack. Reads the
    /// sample rate from the header rather than assuming 16 kHz — Piper voices are
    /// 22050 and MMS are 16000, and playing one at the other's rate is the classic
    /// chipmunk/slow-motion bug.
    /// </summary>
    /// <summary>
    /// Speaks every language listed in <paramref name="sweepFile"/>, one after the
    /// other, in a single session — synthesise, play to the end, then the next.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A line is <c>code TAB langid TAB phrase</c>, or
    /// <c>code TAB langid TAB model TAB phrase</c> when each language has its OWN
    /// model rather than sharing one multi-lingual voice. The eleven South African
    /// languages come from a single 122 MB model selected by langid; the twenty
    /// continental ones are twenty separate models, so the path travels per line.
    /// An empty model column means "keep using the default".
    /// </para>
    /// <para>
    /// The engine is cached on model identity, so a shared model is loaded once and
    /// every language after the first follows in seconds. A language that fails is
    /// reported and the tour continues — one bad voice must not silence the rest.
    /// </para>
    /// </remarks>
    async Task RunLanguageTourAsync(string vut, string wavPath, string sweepFile)
    {
        var lines = await System.IO.File.ReadAllLinesAsync(sweepFile);
        var log = new System.Text.StringBuilder();
        var spoken = 0;
        var attempted = 0;

        Append($"\n===== SPEAKING {lines.Length} LANGUAGES =====\n");

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = line.Split('\t');
            if (f.Length < 3) continue;

            var code = f[0].Trim();
            long.TryParse(f[1].Trim(), out var langId);

            var model = vut;
            string phrase;
            if (f.Length >= 4)
            {
                var m = f[2].Trim();
                if (m.Length > 0)
                {
                    model = m.Contains('/')
                        ? m
                        : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(vut)!, m);
                }
                phrase = f[3].Trim();
            }
            else
            {
                phrase = f[2].Trim();
            }

            attempted++;
            if (!System.IO.File.Exists(model))
            {
                Append($"\n▶ {code} — SKIPPED, no model at {model}\n");
                log.Append($"--- {code} --- missing model {model}\n");
                continue;
            }

            // Refuse input too large to synthesise safely rather than discovering
            // the limit as a crash. Unbounded input is how a phone dies without a
            // catchable exception.
            if (CircleAI.Samples.It.DeviceDiagnostics.TooLargeToSynthesise(phrase, out var why))
            {
                Append($"\n▶ {code} — REFUSED: {why}\n");
                log.Append($"--- {code} --- refused: {why}\n");
                continue;
            }

            Append($"\n▶ {code} (langid {langId})\n");

            // A stack overflow, an OOM kill or a native SIGSEGV runs NO handler —
            // the process is simply gone. Writing down what we are about to attempt
            // is the only way the next launch can say what killed the last one.
            var stateDir = System.IO.Path.GetDirectoryName(vut)!;
            CircleAI.Samples.It.DeviceDiagnostics.BeginRisky(stateDir, $"{code} ({langId}) — {model}");
            try
            {
                var rep = await CircleAI.Samples.It.Voice.ItTtsProbe.RunLocalAsync(
                    model, wavPath, phrase, s => Append("  " + s + "\n"), default, langId);
                log.Append($"--- {code} ---\n{rep}\n");

                // Play to completion BEFORE moving on. Returning early here is what
                // made every phrase sound truncated.
                if (System.IO.File.Exists(wavPath))
                {
                    await PlayWavAsync(wavPath);
                    spoken++;
                }
            }
            catch (OutOfMemoryException ex)
            {
                // The likeliest real death on this phone: ~110 MB per voice against
                // ~1.5 GB free, loaded and released dozens of times. Drop what we
                // can and keep going — one language must not end the run.
                Append($"  {code} OUT OF MEMORY — releasing and continuing\n");
                log.Append($"--- {code} --- OOM\n{ex}\n");
                CircleAI.Samples.It.DeviceDiagnostics.WriteDetail(
                    System.IO.Path.GetDirectoryName(vut)!, $"OOM during {code}", ex);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            catch (Exception ex)
            {
                // Concise on screen, complete in a file. Printing the exception
                // verbatim fills a phone screen with runtime frames and reads like
                // a crash even when the failure was handled.
                Append($"  {code} FAILED\n  {CircleAI.Samples.It.DeviceDiagnostics.Summarise(ex)}");
                log.Append($"--- {code} --- FAILED\n{ex}\n");
                CircleAI.Samples.It.DeviceDiagnostics.WriteDetail(
                    System.IO.Path.GetDirectoryName(vut)!, $"failure during {code}", ex);
            }
            finally
            {
                CircleAI.Samples.It.DeviceDiagnostics.EndRisky(System.IO.Path.GetDirectoryName(vut)!);
            }
        }

        Append($"\n===== DONE — {spoken}/{lines.Length} languages spoke =====\n");
        try
        {
            await System.IO.File.WriteAllTextAsync(
                System.IO.Path.Combine(System.IO.Path.GetDirectoryName(vut)!, "sweep-result.txt"),
                log.ToString());
        }
        catch { }
    }

    async Task PlayWavAsync(string wavPath)
    {
        try
        {
            Append("[tts] playing through the speaker…\n");
            await PlayWavStaticAsync(wavPath);
            Append("[tts] PLAYED — the device spoke.\n");
        }
        catch (Exception ex)
        {
            Append($"[tts] playback FAILED: {ex.Message}\n");
        }
    }

    /// <summary>
    /// Plays a WAV out of the device speaker. Static because the language picker
    /// is a separate Activity and playback has nothing to do with this screen's
    /// transcript — it only ever needed the file and the speaker.
    /// </summary>
    public static async Task PlayWavStaticAsync(string wavPath)
    {
        var wav = await System.IO.File.ReadAllBytesAsync(wavPath);
        if (wav.Length <= 44) return;                       // header only: nothing to play

        // Read the format out of the header rather than assuming 22.05 kHz mono:
        // the voices in the catalogue do not all share a sample rate, and playing
        // one at another's rate is the chipmunk bug.
        var sampleRate = BitConverter.ToInt32(wav, 24);
        var channels = BitConverter.ToInt16(wav, 22);
        var pcm = new byte[wav.Length - 44];
        Buffer.BlockCopy(wav, 44, pcm, 0, pcm.Length);

        await using var player = new AndroidAudioPlayer();
        await player.PlayAsync(pcm, sampleRate, channels < 1 ? 1 : channels, 16);
    }
#endif

    /// <summary>Current battery charge 0-100, or null when it cannot be read.</summary>
    int? ReadBatteryPercent()
    {
        try
        {
            using var status = RegisterReceiver(null, new IntentFilter(Intent.ActionBatteryChanged));
            if (status is null) return null;

            var level = status.GetIntExtra(BatteryManager.ExtraLevel, -1);
            var scale = status.GetIntExtra(BatteryManager.ExtraScale, -1);
            if (level < 0 || scale <= 0) return null;

            return (int)Math.Round(level * 100.0 / scale);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The OS is asking apps to release memory. Fire the Neuron's brownout —
    /// evict the hot specialist first, keep the always-warm generalist serving —
    /// instead of letting the phone's low-memory killer take the whole process.
    /// This is the "RAM is never held hostage" guarantee wired to the REAL OS
    /// signal: the residency + evict-specialist-first logic already exists; this
    /// triggers it from onTrimMemory rather than only from a manual signal. A
    /// production app would also clear the pressure when memory recovers (e.g. on
    /// resume) — the specialist is rebuildable from the registry.
    /// </summary>
    public override void OnTrimMemory([Android.Runtime.GeneratedEnum] TrimMemory level)
    {
        base.OnTrimMemory(level);
        var session = _session;
        if (session is null) return;

        if (level is TrimMemory.RunningLow or TrimMemory.RunningCritical
                  or TrimMemory.Complete or TrimMemory.Background)
        {
            Append($"\n[mem] OS memory pressure ({level}) — evicting the specialist, keeping the generalist\n");
            _ = session.SignalCriticalMemoryAsync();
        }
    }

    void Append(string s)
    {
        RunOnUiThread(() =>
        {
            _transcript.Text += s;
            _scroll.Post(() => _scroll.FullScroll(FocusSearchDirection.Down));
        });
    }

    /// <summary>
    /// Renders the model's reasoning trace in muted grey so it is visibly the
    /// scratchpad, not the answer. Uses a SpannableString because the whole
    /// transcript is one TextView — colouring only the appended run.
    /// </summary>
    void AppendThinking(string s)
    {
        RunOnUiThread(() =>
        {
            var start = _transcript.Text?.Length ?? 0;
            var sb = new Android.Text.SpannableStringBuilder(_transcript.TextFormatted);
            sb.Append(s);
            sb.SetSpan(new Android.Text.Style.ForegroundColorSpan(Muted),
                       start, start + s.Length,
                       Android.Text.SpanTypes.ExclusiveExclusive);
            _transcript.TextFormatted = sb;
            _scroll.Post(() => _scroll.FullScroll(FocusSearchDirection.Down));
        });
    }
}
