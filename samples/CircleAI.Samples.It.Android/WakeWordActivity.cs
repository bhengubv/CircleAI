#if IT_VOICE_ANDROID
#nullable enable

// WakeWordActivity.cs
//
// "Hey B", listened for on the phone, with nothing leaving it.
//
// THE SCREEN SAYS ONE SENTENCE AND THEN GETS OUT OF THE WAY. A wake word is not
// a feature you operate, it is one you forget about — so there is no threshold
// slider, no confidence readout, no model name. Someone who has never heard the
// word "model" should be able to open this, say two syllables, and watch it
// answer. Everything a developer would want instead goes to logcat under the tag
// below, where it belongs.
//
// WHY THE MICROPHONE RUNS ON ITS OWN THREAD. Decoding is roughly 20x realtime on
// a desktop and a good deal less on a cheap phone; doing it on the UI thread
// would drop frames on the very animation that tells the user it is listening.
// The capture loop owns the spotter, and only the detection crosses back.
//
// SELF-CHECK is a hidden affordance, present only when someone has pushed wavs
// to the app's external files directory. It runs the same spotter over recorded
// audio and prints the phrase, the frame and the probability — which is what
// makes an on-device claim checkable instead of a screenshot of a nice circle.
// See docs: adb push <wavs> /sdcard/Android/data/com.bhengubv.circleai/files/kws-selfcheck/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Views.Animations;
using Android.Widget;
using CircleAI.Voice;

// The Android SDK ships its own Path (a drawing primitive), Environment (device
// storage) and OperationCanceledException, and all three collide with the BCL
// types this file actually wants. Aliased rather than fully-qualified at ~15 call
// sites, which is how the rest of this head handles the same clash.
using IOPath = System.IO.Path;
using SysEnv = System.Environment;
using Cancelled = System.OperationCanceledException;

namespace CircleAI.Samples.It.Mobile;

[Activity(Label = "Hey B", Exported = false)]
public class WakeWordActivity : Activity
{
    const string Tag = "CircleAI.Kws";

    /// <summary>Registry name of the wake-word bundle.</summary>
    const string ModelName = "KWS-Zipformer-HeyB";

    TextView _status = null!;
    TextView _hint = null!;
    EarView  _ear = null!;

    CancellationTokenSource? _listening;
    string? _bundleDir;
    int _heard;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActionBar?.Hide();
        BuildUi();
    }

    void BuildUi()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Ui.Bg);
        root.SetGravity(GravityFlags.CenterHorizontal);
        var pad = Ui.Dp(this, 24);

        root.AddView(Ui.HomeBar(this, "Hey B"), Ui.Fill());

        _ear = new EarView(this);
        var size = Ui.Dp(this, 200);
        var lp = new LinearLayout.LayoutParams(size, size) { TopMargin = Ui.Dp(this, 44) };
        lp.Gravity = GravityFlags.CenterHorizontal;
        root.AddView(_ear, lp);

        // The instruction IS the interface. Quotation marks around the phrase so
        // it reads as something to say out loud rather than a label.
        _status = Ui.Label(this, "Say “Hey B”", 22f, Ui.Ink, bold: true);
        _status.Gravity = GravityFlags.Center;
        _status.SetPadding(pad, Ui.Dp(this, 30), pad, 0);
        root.AddView(_status, Ui.Fill());

        _hint = Ui.Label(this, "Getting ready…", 15f, Ui.InkSoft);
        _hint.Gravity = GravityFlags.Center;
        _hint.SetPadding(pad, Ui.Dp(this, 8), pad, 0);
        root.AddView(_hint, Ui.Fill());

        root.AddView(new View(this), new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        var claim = Ui.Label(this,
            "·   Listening happens on this phone\n" +
            "·   Nothing is kept and nothing is sent",
            14f, Ui.InkSoft);
        claim.SetPadding(pad, 0, pad, Ui.Dp(this, 22));
        root.AddView(claim, Ui.Fill());

        if (SelfCheckFolder() is not null)
        {
            var link = Ui.Label(this, "Check it against a recording", 15f, Ui.Blue, bold: true);
            link.Gravity = GravityFlags.Center;
            link.SetPadding(0, Ui.Dp(this, 12), 0, Ui.Dp(this, 24));
            link.Clickable = true;
            link.Click += (_, _) => RunSelfCheck();
            root.AddView(link, Ui.Fill());
        }

        SetContentView(root);
    }

    protected override void OnResume()
    {
        base.OnResume();
        StartListening();
    }

    public override void OnRequestPermissionsResult(
        int requestCode, string[] permissions, Android.Content.PM.Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == 1002 &&
            grantResults.Length > 0 && grantResults[0] == Android.Content.PM.Permission.Granted)
            StartListening();
    }

    protected override void OnPause()
    {
        // The microphone stops with the screen. A wake word that keeps the mic
        // open behind your back is the thing people are afraid of, and being in
        // the background is not a reason to hold it — the resident service is,
        // and that is a deliberate, separate opt-in.
        _listening?.Cancel();
        _listening = null;
        _ear.SetListening(false);
        base.OnPause();
    }

    async void StartListening()
    {
        if (_listening is not null) return;

        // RECORD_AUDIO is a runtime permission. Without it AudioRecord does not
        // fail — it hands back silence, which looks exactly like a wake word that
        // does not work.
        if (CheckSelfPermission(Android.Manifest.Permission.RecordAudio)
            != Android.Content.PM.Permission.Granted)
        {
            RequestPermissions([Android.Manifest.Permission.RecordAudio], 1002);
            _hint.Text = "Needs permission to hear you";
            return;
        }

        _bundleDir ??= FindBundle();
        if (_bundleDir is null)
        {
            _status.Text = "Not turned on yet";
            _hint.Text = "Turn on Waking under “What it can do”";
            return;
        }

        var cts = new CancellationTokenSource();
        _listening = cts;
        _hint.Text = "Listening";
        _ear.SetListening(true);

        try
        {
            await Task.Run(() => ListenLoop(_bundleDir, cts.Token), cts.Token).ConfigureAwait(false);
        }
        catch (Cancelled) { }
        catch (Exception ex)
        {
            Log.Error(Tag, "listen loop failed: " + ex);
            RunOnUiThread(() =>
            {
                _hint.Text = "Could not start listening";
                _ear.SetListening(false);
            });
        }
    }

    /// <summary>Microphone in, keyword out. Runs off the UI thread.</summary>
    async Task ListenLoop(string bundleDir, CancellationToken ct)
    {
        // TWO STAGES. Stage one is generous so the wake never misses; stage two
        // throws out the ones that were the word rather than the wake — "let us
        // circle back" — by checking the phrase STARTED what was being said.
        using var kws = new ConfirmedKeywordSpotter(new ZipformerKwsSpotter(bundleDir));
        Log.Info(Tag, $"listening for: {string.Join(" | ", kws.Keywords)}  (bundle {bundleDir})");
        foreach (var (phrase, by) in kws.ShadowedKeywords)
            Log.Warn(Tag, $"\"{phrase}\" can never fire — \"{by}\" finishes inside it");

        kws.Woke += (_, d) =>
        {
            Log.Info(Tag, $"HEARD \"{d.Phrase}\" at frame {d.AtFrame} p={d.Probability:F4}");
            RunOnUiThread(() => Woke(d.Phrase));
        };
        kws.Rejected += (_, r) =>
            Log.Info(Tag, $"VETOED \"{r.Detection.Phrase}\" p={r.Detection.Probability:F4} — {r.Reason}");

        await using var mic = new AndroidAudioCapture();
        var pcm = new float[1600];

        await foreach (var chunk in mic.CaptureAsync(ct).ConfigureAwait(false))
        {
            // PCM16 little-endian to float in [-1, 1]. NOT scaled to int16 range:
            // KaldiFbank takes normalised samples, and multiplying here is exactly
            // the bug that made this deaf for a day.
            var samples = chunk.Length / 2;
            if (samples > pcm.Length) pcm = new float[samples];
            var span = chunk.Span;
            for (var i = 0; i < samples; i++)
                pcm[i] = (short)(span[i * 2] | (span[i * 2 + 1] << 8)) / 32768f;

            kws.AcceptWaveform(pcm.AsSpan(0, samples));
        }
    }

    void Woke(string phrase)
    {
        _heard++;
        _status.Text = "Heard you";
        _hint.Text = _heard == 1 ? "Say it again to try once more" : $"{_heard} times";
        _ear.Flash();

        // Back to the invitation, so the screen never sits on a stale success.
        _status.PostDelayed(() =>
        {
            if (_listening is null) return;
            _status.Text = "Say “Hey B”";
            _hint.Text = "Listening";
        }, 2200);
    }

    // ── proof, not decoration ────────────────────────────────────────────────

    string? SelfCheckFolder()
    {
        var ext = GetExternalFilesDir(null)?.AbsolutePath;
        if (ext is null) return null;
        var dir = IOPath.Combine(ext, "kws-selfcheck");
        return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.wav").Any() ? dir : null;
    }

    /// <summary>
    /// Runs the spotter over pushed recordings and prints what it found.
    /// </summary>
    /// <remarks>
    /// The numbers are the point: a phrase, the frame it landed on and the mean
    /// acoustic probability. Run against sherpa's own shipped audio these must
    /// match what the same code produces on a desktop, which is the difference
    /// between "it ran on the phone" and "it worked on the phone".
    /// </remarks>
    async void RunSelfCheck()
    {
        var dir = SelfCheckFolder();
        if (dir is null || _bundleDir is null) return;

        _listening?.Cancel();
        _listening = null;
        _ear.SetListening(false);
        _status.Text = "Checking…";
        _hint.Text = "";

        var summary = await Task.Run(() =>
        {
            var lines = new List<string>();
            var hits = 0;
            var keywords = IOPath.Combine(dir, "keywords.txt");
            foreach (var wav in Directory.EnumerateFiles(dir, "*.wav").OrderBy(f => f))
            {
                try
                {
                    using var kws = new ConfirmedKeywordSpotter(new ZipformerKwsSpotter(
                        _bundleDir, File.Exists(keywords) ? keywords : null));
                    var found = new List<string>();
                    kws.Woke += (_, d) =>
                    {
                        found.Add($"{d.Phrase} p={d.Probability:F4} @{d.AtFrame}");
                        hits++;
                    };
                    kws.Rejected += (_, r) => found.Add($"[vetoed {r.Detection.Phrase} — {r.Reason}]");

                    var audio = ReadWav(wav);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    for (var i = 0; i < audio.Length; i += 1600)
                        kws.AcceptWaveform(audio.AsSpan(i, Math.Min(1600, audio.Length - i)));
                    kws.Flush();
                    sw.Stop();

                    var rt = audio.Length / 16000.0 / Math.Max(sw.Elapsed.TotalSeconds, 1e-6);
                    var line = $"{IOPath.GetFileName(wav)}: " +
                               (found.Count > 0 ? string.Join(", ", found) : "nothing") +
                               $"  [{audio.Length / 16000.0:F2}s in {sw.Elapsed.TotalSeconds:F2}s, {rt:F1}x]";
                    Log.Info(Tag, "selfcheck " + line);
                    lines.Add(line);
                }
                catch (Exception ex)
                {
                    Log.Error(Tag, $"selfcheck {IOPath.GetFileName(wav)} failed: {ex}");
                    lines.Add($"{IOPath.GetFileName(wav)}: FAILED — {ex.Message}");
                }
            }
            return (Hits: hits, Lines: lines);
        }).ConfigureAwait(true);

        _status.Text = summary.Hits > 0 ? $"Found {summary.Hits}" : "Found nothing";
        _hint.Text = "See the log for detail";
        StartListening();
    }

    /// <summary>16-bit mono PCM WAV to float [-1, 1], chunk-walked rather than assumed.</summary>
    static float[] ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var at = 12;                                    // past RIFF....WAVE
        while (at + 8 < bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
            var size = BitConverter.ToInt32(bytes, at + 4);
            if (id == "data") { at += 8; break; }
            at += 8 + size + (size & 1);                // chunks are word-aligned
        }
        var n = (bytes.Length - at) / 2;
        var f = new float[n];
        for (var i = 0; i < n; i++) f[i] = BitConverter.ToInt16(bytes, at + i * 2) / 32768f;
        return f;
    }

    /// <summary>Where the bundle's three graphs live — downloaded, or side-loaded.</summary>
    /// <remarks>
    /// TWO PLACES, IN THIS ORDER: the catalogue's model store, then a bundle
    /// dropped into the app's own external files directory.
    /// <para>
    /// The loader resolves a bundle to ONE file, which is right for a single-graph
    /// model and not for this one — encoder, decoder and joiner have to be found
    /// together, so what the spotter wants is the directory around them.
    /// </para>
    /// <para>
    /// The side-load path is not a test hook. Getting a model onto a phone with no
    /// data budget — passed over Bluetooth, copied from a laptop, handed on a
    /// memory card — is how this actually reaches people who most need it to work
    /// offline, and it is the same directory a file manager can write to. It is
    /// also, incidentally, the only way to put a bundle on a release build without
    /// root, which is what makes an on-device claim checkable.
    /// </para>
    /// </remarks>
    string? FindBundle() => FindBundle(this);

    /// <summary>
    /// A bundle the owner put on the phone themselves, if there is one.
    /// </summary>
    /// <remarks>
    /// Public because the abilities list has to agree with this screen about what
    /// counts as present. It did not, at first: a side-loaded bundle worked
    /// perfectly here while the list still offered to DOWNLOAD it, which is the
    /// exact insult you do not want to hand someone who has just gone to the
    /// trouble of copying a file across because they have no data.
    /// </remarks>
    public static string? SideloadedBundle(Context c) =>
        c.GetExternalFilesDir(null)?.AbsolutePath is { } ext &&
        Directory.Exists(IOPath.Combine(ext, "kws-hey-b"))
            ? IOPath.Combine(ext, "kws-hey-b")
            : null;

    /// <summary>
    /// The wake bundle this phone should use — downloaded, or side-loaded.
    /// </summary>
    /// <remarks>
    /// Public for the same reason <see cref="SideloadedBundle"/> is: the landing
    /// screen now listens too, and two screens disagreeing about whether the wake
    /// word is present is precisely the failure the side-load note below describes.
    /// One lookup, one answer.
    /// </remarks>
    public static string? FindBundle(Context c)
    {
        try
        {
            var stored = IOPath.Combine(
                SysEnv.GetFolderPath(SysEnv.SpecialFolder.ApplicationData),
                "CircleAI", "Models", ModelName);

            foreach (var dir in new[] { stored, SideloadedBundle(c) })
            {
                if (dir is null || !Directory.Exists(dir)) continue;
                var encoder = Directory
                    .EnumerateFiles(dir, "*encoder*.onnx", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (encoder is not null)
                {
                    Log.Info(Tag, "bundle: " + IOPath.GetDirectoryName(encoder));
                    return IOPath.GetDirectoryName(encoder);
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(Tag, "bundle lookup failed: " + ex);
            return null;
        }
    }

    /// <summary>
    /// The listening indicator: a ring with arcs that breathe while the mic is
    /// open and snap wide for a moment when the phrase lands.
    /// </summary>
    sealed class EarView : View
    {
        readonly Paint _ring = new(PaintFlags.AntiAlias) { Color = Ui.Blue };
        readonly Paint _arc  = new(PaintFlags.AntiAlias) { Color = Ui.Blue };
        readonly Paint _halo = new(PaintFlags.AntiAlias) { Color = Ui.Blue };
        bool _listening;
        bool _flash;

        public EarView(Context c) : base(c)
        {
            _ring.SetStyle(Paint.Style.Stroke);
            _arc.SetStyle(Paint.Style.Stroke);
            _arc.StrokeCap = Paint.Cap.Round;
            _halo.SetStyle(Paint.Style.Fill);
            _halo.Alpha = 28;
        }

        public void SetListening(bool on)
        {
            _listening = on;
            if (on)
                StartAnimation(new AlphaAnimation(1f, 0.5f)
                {
                    Duration = 1100,
                    RepeatCount = Animation.Infinite,
                    RepeatMode = RepeatMode.Reverse,
                });
            else ClearAnimation();
            Invalidate();
        }

        public void Flash()
        {
            _flash = true;
            ClearAnimation();
            Invalidate();
            PostDelayed(() =>
            {
                _flash = false;
                SetListening(_listening);
            }, 900);
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            float w = Width, h = Height, cx = w / 2f, cy = h / 2f;
            var r = Math.Min(w, h) / 2f;

            _ring.StrokeWidth = r * 0.11f;
            _arc.StrokeWidth  = r * 0.085f;
            _halo.Alpha = _flash ? 70 : 28;
            canvas.DrawCircle(cx, cy, r * 0.98f, _halo);

            var ringR = r * 0.46f;
            canvas.DrawArc(new RectF(cx - ringR, cy - ringR, cx + ringR, cy + ringR),
                           -50f, 280f, false, _ring);

            for (var i = 0; i < 3; i++)
            {
                var ar = ringR + r * (0.16f + 0.16f * i);
                _arc.Alpha = _flash ? 255 : i switch { 0 => 235, 1 => 150, _ => 80 };
                canvas.DrawArc(new RectF(cx - ar, cy - ar, cx + ar, cy + ar),
                               -34f, 68f, false, _arc);
            }
        }
    }
}
#endif
