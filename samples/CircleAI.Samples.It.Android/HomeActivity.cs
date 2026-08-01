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
// Everything else — the chat, the capability probe, the vision demo — is one tap
// away and none of it competes for this screen.

using System;
using System.Collections.Generic;
using System.Threading;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Views.Animations;
using Android.Widget;

namespace CircleAI.Samples.It.Mobile;

[Activity(Label = "CircleAI",
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
    int _next;
    CancellationTokenSource? _speaking;

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

        // Wordmark, small. The product name is not the pitch.
        var name = Ui.Label(this, "CircleAI", 18f, Ui.InkSoft, bold: true);
        name.SetPadding(pad, Ui.Dp(this, 28), pad, 0);
        name.Gravity = GravityFlags.Center;
        root.AddView(name, Ui.Fill());

        // ── the thing you press ──────────────────────────────────────────
        _mark = new MarkView(this);
        var markSize = Ui.Dp(this, 200);
        var markLp = new LinearLayout.LayoutParams(markSize, markSize);
        markLp.TopMargin = Ui.Dp(this, 40);
        markLp.Gravity = GravityFlags.CenterHorizontal;
        _mark.Clickable = true;
        _mark.Click += (s, e) => SpeakNext();
        root.AddView(_mark, markLp);

        _prompt = Ui.Label(this, "Tap to hear it speak", 20f, Ui.Ink, bold: true);
        _prompt.Gravity = GravityFlags.Center;
        _prompt.SetPadding(pad, Ui.Dp(this, 28), pad, 0);
        root.AddView(_prompt, Ui.Fill());

        _caption = Ui.Label(this, "isiZulu — one of 71", 15f, Ui.InkSoft);
        _caption.Gravity = GravityFlags.Center;
        _caption.SetPadding(pad, Ui.Dp(this, 8), pad, 0);
        root.AddView(_caption, Ui.Fill());

        // Spacer, so the claims sit low and the circle owns the upper half.
        var spacer = new View(this);
        root.AddView(spacer, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        // ── three claims, three lines ────────────────────────────────────
        var claims = new LinearLayout(this) { Orientation = Orientation.Vertical };
        claims.SetPadding(pad, 0, pad, Ui.Dp(this, 20));
        foreach (var line in new[]
                 {
                     "71 languages, spoken out loud",
                     "Runs on the phone — works with no signal",
                     "Free, no account, nothing sent anywhere",
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

        var langs = Ui.Action(this, "All 71 languages", primary: true);
        langs.Click += (s, e) => StartActivity(new Intent(this, typeof(LanguagePickerActivity)));
        var lp1 = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        lp1.RightMargin = Ui.Dp(this, 8);
        nav.AddView(langs, lp1);

        var chat = Ui.Action(this, "Ask it something", primary: false);
        chat.Click += (s, e) => StartActivity(new Intent(this, typeof(MainActivity)));
        nav.AddView(chat, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

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

        _caption.Text = $"{label} — one of 71";
        _prompt.Text = "…";
        _mark.SetBusy(true);

        try
        {
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

    protected override void OnDestroy()
    {
        _speaking?.Cancel();
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

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            float w = Width, h = Height;
            float cx = w / 2f, cy = h / 2f;
            float r = Math.Min(w, h) / 2f;

            _ring.StrokeWidth = r * 0.11f;
            _arc.StrokeWidth  = r * 0.085f;

            canvas.DrawCircle(cx, cy, r * 0.98f, _halo);

            // The ring, open on the right where the sound leaves.
            var ringR = r * 0.46f;
            var ringBox = new RectF(cx - ringR, cy - ringR, cx + ringR, cy + ringR);
            canvas.DrawArc(ringBox, -50f, 280f, false, _ring);

            // Three widening arcs. The outer two fade when idle and come up to
            // full strength while speaking, so the mark reads as "it is talking".
            for (var i = 0; i < 3; i++)
            {
                var ar = ringR + r * (0.16f + 0.16f * i);
                var box = new RectF(cx - ar, cy - ar, cx + ar, cy + ar);
                _arc.Alpha = _busy ? 255 : i switch { 0 => 235, 1 => 150, _ => 80 };
                canvas.DrawArc(box, -34f, 68f, false, _arc);
            }
        }
    }
}
