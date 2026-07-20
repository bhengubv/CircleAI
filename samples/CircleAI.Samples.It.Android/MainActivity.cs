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

    static readonly Color Bg    = Color.ParseColor("#080d14");
    static readonly Color Panel = Color.ParseColor("#0f1927");
    static readonly Color Ink   = Color.ParseColor("#eef6ff");
    static readonly Color Muted = Color.ParseColor("#5f7a95");
    static readonly Color Blue  = Color.ParseColor("#2196F3");

    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        BuildUi();

        Append("IT! - CircleAI Neuron, on-device (C#)\n\n");
        Append("Type a message and hit Send. Try:\n");
        Append("  \"my name is ...\"   then   \"what's my name?\"\n");
        Append("  \"solve ... step by step\"   -> routes to a specialist\n\n");

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
            await Task.Run(() => _session.RunTurnStreamingAsync(
                text,
                line => Append(line + "\n"),
                chunk => Append(chunk)));
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

    void Append(string s)
    {
        RunOnUiThread(() =>
        {
            _transcript.Text += s;
            _scroll.Post(() => _scroll.FullScroll(FocusSearchDirection.Down));
        });
    }
}
