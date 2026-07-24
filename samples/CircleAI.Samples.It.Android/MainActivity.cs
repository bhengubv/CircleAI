// MainActivity.cs
//
// IT! on Android — the sample a developer actually drives. Type a message, watch
// the concierge pick which organ answers, watch the reply stream in word by word.
// The whole brain is the shared ItSession (same C# the desktop console runs).

using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using CircleAI.Samples.It;

namespace CircleAI.Samples.It.Mobile;

[Activity(Label = "IT!", MainLauncher = true, WindowSoftInputMode = SoftInput.AdjustResize)]
public class MainActivity : Activity
{
    ItSession? _session;
    TextView _transcript = null!;
    ScrollView _scroll = null!;
    EditText _input = null!;
    Button _send = null!;
    Button _tools = null!;
    Button _cv = null!;
#if IT_VOICE_ANDROID
    Button _talk = null!;
#endif

    static readonly Color Bg    = Color.ParseColor("#080d14");
    static readonly Color Panel = Color.ParseColor("#0f1927");
    static readonly Color Ink   = Color.ParseColor("#eef6ff");
    static readonly Color Muted = Color.ParseColor("#5f7a95");
    static readonly Color Blue  = Color.ParseColor("#2196F3");

    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Teach the platform-neutral device probe how to read THIS phone's real
        // memory + storage. Two DISTINCT numbers matter and were conflated before:
        //   • AvailMem (FREE RAM) gates model FIT — a model needs its weight in free
        //     RAM to load; picking against total RAM OOM-killed the app on a 3.6 GB
        //     phone with only ~1.5 GB free (it selected a 4B model and died).
        //   • TotalMem (device-class RAM) gates TIER — a 3.6 GB phone is a Phone.
        // Without the hook the Core heuristic reads the GC heap limit (~100 MB) and
        // the phone looks like a Wearable with everything NothingFits.
        CircleAI.Core.DeviceProbe.PlatformMemoryProbe = () =>
        {
            long? avail = null, total = null, storage = null;
            try
            {
                if (GetSystemService(Android.Content.Context.ActivityService) is Android.App.ActivityManager am)
                {
                    var mi = new Android.App.ActivityManager.MemoryInfo();
                    am.GetMemoryInfo(mi);
                    avail = mi.AvailMem;   // free RAM → model fit
                    total = mi.TotalMem;   // device class → tier
                }
            }
            catch { /* fall back to the Core heuristic */ }
            try
            {
                var stat = new Android.OS.StatFs(FilesDir!.AbsolutePath);
                storage = stat.AvailableBytes;
            }
            catch { /* fall back to the Core heuristic */ }
            return new CircleAI.Core.DeviceProbe.PlatformMemory(avail, storage, total);
        };

        BuildUi();

        Append("IT! - CircleAI Neuron, on-device (C#)\n\n");
        Append("Type a message and hit Send. Try:\n");
        Append("  \"my name is ...\"   then   \"what's my name?\"\n");
        Append("  \"solve ... step by step\"   -> routes to a specialist\n\n");

        Append("Tap Caps now for the offline sweep — per-modality model verdicts\n");
        Append("plus CV / cover letter / invoice / report PDFs. No model, no wait.\n\n");

        Append("Starting the Neuron. On first run it picks a model that fits\n");
        Append("this phone and downloads it (~433 MB), so this takes a while.\n");
        Append("Later runs load straight from cache.\n\n");

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
            // Full detail, not just Message — the first real-model run is the
            // one where native/model failures actually need diagnosing.
            Append("ERROR starting the Neuron:\n" + ex + "\n");
        }
    }

    void BuildUi()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Bg);

        // header
        var header = new TextView(this) { Text = "IT!", TextSize = 22f };
        header.SetTypeface(null, TypefaceStyle.Bold);
        header.SetTextColor(Ink);
        header.SetPadding(36, 40, 36, 26);
        header.SetBackgroundColor(Panel);
        root.AddView(header, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // transcript (fills the middle)
        _scroll = new ScrollView(this);
        _transcript = new TextView(this) { TextSize = 13f };
        _transcript.SetTextColor(Ink);
        _transcript.SetPadding(30, 28, 30, 28);
        _transcript.SetTextIsSelectable(true);
        _scroll.AddView(_transcript);
        root.AddView(_scroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        // input row
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetBackgroundColor(Panel);
        row.SetPadding(20, 16, 20, 22);

        _input = new EditText(this) { Hint = "Say something to IT!", Enabled = false };
        _input.SetTextColor(Ink);
        _input.SetHintTextColor(Muted);
        _input.SetSingleLine(true);
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
        row.AddView(_input, new LinearLayout.LayoutParams(
            0, ViewGroup.LayoutParams.WrapContent, 1f));

        // One tap runs the tool probes — questions whose answers the model
        // cannot know unless it actually calls a tool.
        _tools = new Button(this) { Text = "Sweep", Enabled = false };
        _tools.SetTextColor(Ink);
        _tools.SetBackgroundColor(Panel);
        _tools.Click += (s, e) => RunFullSweep();
        row.AddView(_tools, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        // Offline capability sweep. Enabled immediately: it needs no model, so it
        // proves the catalogued-model verdicts AND the whole document engine
        // on-device before the brain finishes loading.
        _cv = new Button(this) { Text = "Caps", Enabled = true };
        _cv.SetTextColor(Ink);
        _cv.SetBackgroundColor(Panel);
        _cv.Click += (s, e) => RunCapabilities();
        row.AddView(_cv, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

#if IT_VOICE_ANDROID
        // Hands-free. Only present when the APK was built with voice, because
        // without the ONNX/whisper natives the button could only ever fail.
        _talk = new Button(this) { Text = "Talk", Enabled = false };
        _talk.SetTextColor(Ink);
        _talk.SetBackgroundColor(Panel);
        _talk.Click += (s, e) => ToggleVoiceLoop();
        row.AddView(_talk, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
#endif

        _send = new Button(this) { Text = "Send", Enabled = false };
        _send.SetTextColor(Color.White);
        _send.SetBackgroundColor(Blue);
        _send.Click += (s, e) => Send();
        row.AddView(_send, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        root.AddView(row, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        SetContentView(root);
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

            Append("\n[caps] OK. Pull the whole result off the phone with:\n");
            Append("  adb exec-out run-as com.bhengubv.itsample cat files/capability-report.txt\n");
            Append("  adb exec-out run-as com.bhengubv.itsample cat files/<name>.pdf > out.pdf\n");
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

            RunOnUiThread(() => _talk.Text = "Talk");
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
                speaker.Engine,
                new AndroidAudioPlayer());

            _voiceLoop.Faulted += (s, ex) => Append($"[voice] turn failed: {ex.Message}\n");
            await _voiceLoop.StartAsync();
            RunOnUiThread(() => _talk.Text = "Stop");
            Append("[voice] listening — say \"hey b\"\n");
        }
        catch (Exception ex)
        {
            Append($"[voice] failed: {ex.Message}\n");
        }
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
