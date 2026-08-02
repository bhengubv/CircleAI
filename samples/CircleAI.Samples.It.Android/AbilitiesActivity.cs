// AbilitiesActivity.cs
//
// What it can do — not what models are installed.
//
// This screen was a list of 78 models. Two things were wrong with that, and the
// second one is the real one:
//
//   IT WAS SLOW. Seventy-eight cards built on the UI thread took about two
//   minutes to paint on a P30. A screen that looks broken is broken.
//
//   IT WAS IN OUR LANGUAGE. "Qwen3 0.6B" is not a thing a person knows. A nine
//   year old and a seventy year old both understand "can it talk?" and neither
//   has any idea what a model is, or why there are seventy-eight of them, or
//   which one they are supposed to want. Asking them to choose is asking them to
//   learn our filing system before they can use their phone.
//
// So it lists ABILITIES. Talking, listening, seeing, answering. Each one is a
// sentence about something the phone can do for you, and underneath it CircleAI
// picks the best model that actually fits this device — which is a decision we
// are equipped to make and the person is not.
//
// The slowness fixed itself: five cards instead of seventy-eight.
//
// The model names are still reachable — a developer evaluating this needs them
// and will look. They are one tap down, not on the front page, which is the
// right order of priority for a product that wants both audiences.

using System.Globalization;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Device;
using CircleAI.Inference;

namespace CircleAI.Samples.It.Mobile;

[Activity(Label = "What it can do", Exported = false)]
public class AbilitiesActivity : Activity
{
    /// <summary>One thing the phone can do, in the words a person would use.</summary>
    /// <param name="Title">What it is. A verb, not a noun — "Talking", not "TTS".</param>
    /// <param name="Blurb">What it means for you, in one sentence.</param>
    /// <param name="Modality">Which models serve it.</param>
    sealed record Ability(string Title, string Blurb, ModelModality Modality);

    static readonly Ability[] Abilities =
    {
        new("Talking",   "Reads things out loud, in 71 languages",        ModelModality.Tts),
        new("Listening", "Understands you when you speak",                ModelModality.Asr),
        new("Answering", "Answers questions and helps you write",         ModelModality.Chat),
        new("Seeing",    "Looks at a photo and tells you what is in it",  ModelModality.Vision),
        new("Waking",    "Hears you say \"Hey B\" without being touched", ModelModality.WakeWord),
    };

    ModelRegistryService? _registry;
    BundleModelLoader?    _loader;
    LinearLayout?         _list;
    LinearLayout?         _tabs;
    string                _modelDir = "";
    bool                  _showTechnical;

    /// <summary>Which tab is showing.</summary>
    /// <remarks>
    /// Two, not five. A settings screen that scrolls forever makes a person hunt,
    /// and hunting is what makes someone decide an app is "complicated" — they
    /// never say the layout was wrong, they say they could not find it. Two tabs
    /// that each fit on one screen means nothing is ever below the fold.
    ///
    /// The split is what-it-does versus what-this-phone-is, because those are two
    /// different questions asked by two different people: the owner wants to turn
    /// something on, and the developer wants to know what it costs.
    /// </remarks>
    int _tab;

    static readonly string[] TabNames = { "What it does", "This phone" };

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AndroidDeviceMemory.Install(this);

        _modelDir = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "CircleAI", "Models");
        _registry = new ModelRegistryService();
        _loader   = new BundleModelLoader(_modelDir, _registry);

        BuildUi();
        Refresh();
    }

    void BuildUi()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Ui.Bg);

        var header = new LinearLayout(this) { Orientation = Orientation.Vertical };
        header.SetBackgroundColor(Ui.Surface);
        header.SetPadding(Ui.Dp(this, 20), Ui.Dp(this, 16), Ui.Dp(this, 20), 0);
        header.AddView(Ui.Label(this, "Circle AI", 24f, Ui.Ink, bold: true));

        // The tabs live in the header, under the title, where every phone puts
        // them. A segmented row rather than Android's TabHost: two items do not
        // need a widget with its own lifecycle, and this matches the rest of the
        // hand-built UI instead of importing a different visual language.
        _tabs = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        _tabs.SetPadding(0, Ui.Dp(this, 12), 0, 0);
        header.AddView(_tabs, Ui.Fill());
        root.AddView(header, Ui.Fill());

        var scroll = new ScrollView(this) { VerticalScrollBarEnabled = false };
        scroll.OverScrollMode = OverScrollMode.Never;
        _list = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _list.SetPadding(Ui.Dp(this, 16), Ui.Dp(this, 16), Ui.Dp(this, 16), Ui.Dp(this, 24));
        scroll.AddView(_list);
        root.AddView(scroll, Ui.Fill(1f));

        SetContentView(root);
    }

    void Refresh()
    {
        if (_list is null || _registry is null || _tabs is null) return;
        _list.RemoveAllViews();
        _tabs.RemoveAllViews();

        for (var i = 0; i < TabNames.Length; i++)
        {
            var index    = i;
            var selected = i == _tab;
            var tab = Ui.Label(this, TabNames[i], 15f, selected ? Ui.Ink : Ui.InkSoft, bold: selected);
            tab.Gravity = GravityFlags.Center;
            tab.SetPadding(0, Ui.Dp(this, 12), 0, Ui.Dp(this, 12));   // 48dp target
            tab.Clickable = true;
            // The selected tab is underlined in blue. Colour alone is not enough —
            // it fails for anyone colour-blind and washes out in sunlight, which is
            // the condition half the people this is for will be holding the phone in.
            if (selected) tab.Background = Underline();
            tab.Click += (_, _) => { _tab = index; Refresh(); };
            _tabs.AddView(tab, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        }

        var probe = DeviceProbe.Snapshot();
        if (_tab == 0) ShowAbilities(probe);
        else           ShowPhone(probe);
    }

    void ShowAbilities(DeviceProbe probe)
    {
        // ONE bordered panel with hairline-separated rows, not five fat cards.
        //
        // Five cards each with their own padding, margin and shadow spent most of
        // the screen on the gaps BETWEEN things, and pushed the fifth ability below
        // the fold — so a screen with only five items still made you scroll, which
        // is the thing tabs were supposed to fix. A divider does the same job as a
        // card edge for a fraction of the height.
        //
        // The border is blue so the panel has a visible edge. On a dark theme a
        // surface-on-surface card is nearly invisible in daylight, and "where does
        // this box end" is not a question a person should have to work at.
        var panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        panel.Background = Ui.Outlined(this, Ui.Blue, 14f);
        panel.SetPadding(0, Ui.Dp(this, 2), 0, Ui.Dp(this, 2));

        for (var i = 0; i < Abilities.Length; i++)
        {
            if (i > 0) panel.AddView(Divider(), Ui.Fill());
            panel.AddView(Row(Abilities[i], probe), Ui.Fill());
        }

        _list!.AddView(panel, Ui.Fill());
    }

    /// <summary>The hairline between rows — an &lt;hr&gt;, in effect.</summary>
    View Divider()
    {
        var v = new View(this);
        // Blue at low alpha, not Ui.Hairline. Hairline is the slate darkened —
        // on a dark panel that is slate-on-slate and vanishes completely in
        // sunlight, which is where these phones actually get used.
        v.SetBackgroundColor(Android.Graphics.Color.Argb(70, Ui.Blue.R, Ui.Blue.G, Ui.Blue.B));
        v.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Math.Max(1, Ui.Dp(this, 1)));
        return v;
    }

    /// <summary>The device tab: what this phone is and what CircleAI does about it.</summary>
    void ShowPhone(DeviceProbe probe)
    {
        var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
        card.Background = Ui.Rounded(this, Ui.Surface, 14f);
        card.SetPadding(Ui.Dp(this, 18), Ui.Dp(this, 16), Ui.Dp(this, 18), Ui.Dp(this, 16));

        void Line(string title, string value)
        {
            card.AddView(Ui.Label(this, title, 13f, Ui.InkSoft));
            var v = Ui.Label(this, value, 16f, Ui.Ink);
            v.SetPadding(0, Ui.Dp(this, 2), 0, Ui.Dp(this, 14));
            card.AddView(v);
        }

        Line("Space free", $"{probe.StorageFreeGb:0.#} GB");
        Line("Memory", probe.MeasurementWarning is null
            ? $"{probe.RamAvailableBytes / 1_000_000_000.0:0.#} GB free of " +
              $"{probe.RamTotalBytes / 1_000_000_000.0:0.#} GB"
            : "Can't be read on this phone");
        Line("Frees memory after",
            $"{AndroidMemoryPressure.IdleWindowFor(probe.Classify()).TotalMinutes:0} minutes unused, " +
            "or straight away if the phone needs it");
        Line("Where it runs", "On this phone. Nothing is sent anywhere.");

        _list!.AddView(card, Ui.Fill());

        var toggle = Ui.Action(this, _showTechnical ? "Hide technical details" : "Show technical details", primary: false);
        toggle.Click += (_, _) => { _showTechnical = !_showTechnical; Refresh(); };
        var tlp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        tlp.TopMargin = Ui.Dp(this, 16);
        _list.AddView(toggle, tlp);

        if (!_showTechnical) return;

        var tech = new LinearLayout(this) { Orientation = Orientation.Vertical };
        tech.Background = Ui.Rounded(this, Ui.Surface, 14f);
        tech.SetPadding(Ui.Dp(this, 18), Ui.Dp(this, 14), Ui.Dp(this, 18), Ui.Dp(this, 14));
        var tlp2 = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        tlp2.TopMargin = Ui.Dp(this, 10);

        foreach (var ability in Abilities)
        {
            var m = _registry!.AllModels
                .Where(x => x.Modality == ability.Modality)
                .OrderByDescending(x => Installed(x.Name))
                .ThenByDescending(x => x.QualityRank)
                .FirstOrDefault();
            if (m is null) continue;

            tech.AddView(Ui.Label(this,
                $"{ability.Title}: {m.Name}\n{Size(m.TotalBytes)} · needs {m.MinRamGb:0.#} GB · {m.Repo}",
                11.5f, Ui.InkSoft));
            tech.AddView(new View(this), new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, Ui.Dp(this, 10)));
        }
        _list.AddView(tech, tlp2);
    }

    Android.Graphics.Drawables.Drawable Underline()
    {
        var d = new Android.Graphics.Drawables.GradientDrawable();
        d.SetShape(Android.Graphics.Drawables.ShapeType.Rectangle);
        d.SetColor(Android.Graphics.Color.Transparent.ToArgb());
        d.SetStroke(0, Android.Graphics.Color.Transparent);
        var layers = new Android.Graphics.Drawables.LayerDrawable(new Android.Graphics.Drawables.Drawable[]
        {
            d,
            Ui.Rounded(this, Ui.Blue, 2f),
        });
        // Only the bottom 3dp of the second layer shows — an underline, not a fill.
        layers.SetLayerInset(1, 0, Ui.Dp(this, 44), 0, 0);
        return layers;
    }

    /// <summary>One compact row: what it does on the left, its state on the right.</summary>
    View Row(Ability ability, DeviceProbe probe)
    {
        var candidates = _registry!.AllModels.Where(m => m.Modality == ability.Modality).ToList();
        var installed  = candidates.FirstOrDefault(m => Installed(m.Name));
        var best       = installed
                      ?? candidates.Where(m => Fits(m, probe))
                                   .OrderByDescending(m => m.QualityRank)
                                   .ThenBy(m => m.MinRamGb)
                                   .FirstOrDefault();

        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Ui.Dp(this, 16), Ui.Dp(this, 12), Ui.Dp(this, 14), Ui.Dp(this, 12));

        var text = new LinearLayout(this) { Orientation = Orientation.Vertical };
        text.AddView(Ui.Label(this, ability.Title, 16f, Ui.Ink, bold: true));
        var sub = installed is not null
            ? ability.Blurb
            : best is not null
                ? $"{ability.Blurb}  ·  {Size(best.TotalBytes)}"
                : ability.Blurb;
        var blurb = Ui.Label(this, sub, 12.5f, Ui.InkSoft);
        blurb.SetPadding(0, Ui.Dp(this, 2), 0, 0);
        text.AddView(blurb);
        row.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        if (installed is not null)
        {
            var on = Ui.Label(this, "✓ On", 14f, Ui.Blue, bold: true);
            row.AddView(on);
        }
        else if (best is not null)
        {
            var get = Compact("Turn on");
            var bar = new ProgressBar(this, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal)
            { Max = 1000, Visibility = ViewStates.Gone };
            var pct = Ui.Label(this, "", 11.5f, Ui.Ink);
            pct.Visibility = ViewStates.Gone;
            row.AddView(get);

            // Progress lives UNDER the row so starting a download does not reflow
            // the list and shove the next ability off the screen mid-tap.
            var wrap = new LinearLayout(this) { Orientation = Orientation.Vertical };
            wrap.AddView(row, Ui.Fill());
            var meta = new LinearLayout(this)
            {
                Orientation = Orientation.Vertical,
                // Gone, not Invisible: a hidden-but-laid-out box still spends its
                // padding, which made every row with a button taller than the rows
                // without one and left the list looking carelessly spaced.
                Visibility = ViewStates.Gone,
            };
            meta.SetPadding(Ui.Dp(this, 16), 0, Ui.Dp(this, 16), Ui.Dp(this, 10));
            meta.AddView(bar, Ui.Fill());
            meta.AddView(pct, Ui.Fill());
            wrap.AddView(meta, Ui.Fill());

            // Wired here, once meta exists: tapping reveals the progress area and
            // starts the download.
            get.Click += (_, _) => { meta.Visibility = ViewStates.Visible; Turn(best, get, bar, pct); };
            return wrap;
        }
        else
        {
            row.AddView(Ui.Label(this, "Needs more memory", 12f, Ui.InkSoft));
        }

        return row;
    }

    /// <summary>A small button. Full-size Ui.Action is a third of a row on its own.</summary>
    Button Compact(string text)
    {
        var b = new Button(this) { Text = text, TextSize = 13f };
        b.SetAllCaps(false);
        b.SetSingleLine(true);
        b.SetTextColor(Ui.White);
        b.Background = Ui.Rounded(this, Ui.Blue, 10f);
        b.SetPadding(Ui.Dp(this, 16), Ui.Dp(this, 8), Ui.Dp(this, 16), Ui.Dp(this, 8));
        b.SetMinimumHeight(Ui.Dp(this, 40));
        b.SetMinimumWidth(0);
        b.StateListAnimator = null;
        return b;
    }

    async void Turn(ModelEntry model, Button button, ProgressBar bar, TextView pct)
    {
        var cts = new CancellationTokenSource();
        button.Text = "Stop";
        bar.Visibility = ViewStates.Visible;
        pct.Visibility = ViewStates.Visible;
        pct.Text = "Setting up…";

        void OnCancel(object? s, EventArgs e) => cts.Cancel();
        button.Click += OnCancel;

        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var progress = new Progress<float>(f =>
            {
                var done    = (long)(model.TotalBytes * Math.Clamp(f, 0f, 1f));
                var seconds = Math.Max(0.001, started.Elapsed.TotalSeconds);
                var rate    = done / seconds / (1024.0 * 1024.0);
                RunOnUiThread(() =>
                {
                    bar.Progress = (int)(Math.Clamp(f, 0f, 1f) * 1000);
                    // Time left, not just a percentage. "60%" does not answer the
                    // question a person is actually asking, which is "can I put
                    // the phone down?"
                    var left = rate > 0.05
                        ? TimeSpan.FromSeconds((model.TotalBytes - done) / (rate * 1024 * 1024))
                        : (TimeSpan?)null;
                    pct.Text = $"{f * 100:0}%" +
                               (left is { TotalSeconds: > 1 and < 3600 }
                                   ? $"  ·  about {Human(left.Value)} left"
                                   : "");
                });
            });

            await Task.Run(() => _loader!.DownloadModelAsync(model.Name, progress), cts.Token);
            RunOnUiThread(Refresh);
        }
        catch (System.OperationCanceledException)
        {
            RunOnUiThread(Refresh);
        }
        catch (Exception ex)
        {
            RunOnUiThread(() =>
            {
                bar.Visibility = ViewStates.Gone;
                pct.Text = Explain(ex);
                button.Text = "Try again";
                button.Click -= OnCancel;
            });
        }
        finally { button.Click -= OnCancel; }
    }

    void Remove(ModelEntry model) =>
        new AlertDialog.Builder(this)
            .SetTitle("Turn this off?")
            .SetMessage($"Frees {Size(model.TotalBytes)}. Turning it back on downloads it again.")
            .SetNegativeButton("Keep it", (s, e) => { })
            .SetPositiveButton("Turn off", (s, e) =>
            {
                try
                {
                    var dir = System.IO.Path.Combine(_modelDir, model.Name);
                    if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
                }
                catch (Exception ex)
                {
                    Toast.MakeText(this, ex.Message, ToastLength.Long)?.Show();
                }
                Refresh();
            })
            .Show();

    // ── plain language ───────────────────────────────────────────────────────

    bool Installed(string name) => _loader?.ModelExists(name) == true;

    static bool Fits(ModelEntry m, DeviceProbe probe) =>
        m.MinRamGb <= probe.UsableRamGb + 0.0001 &&
        (probe.StorageFreeGb <= 0 || m.MinStorageGb <= probe.StorageFreeGb + 0.0001);


    static string Size(long bytes) =>
        bytes >= 1_000_000_000 ? $"{bytes / 1_000_000_000.0:0.#} GB" : $"{bytes / 1_000_000.0:0} MB";

    static string Human(TimeSpan t) =>
        t.TotalSeconds < 90 ? $"{t.TotalSeconds:0} seconds" : $"{t.TotalMinutes:0} minutes";

    static string Explain(Exception ex) => ex switch
    {
        HttpRequestException        => "No internet. Check your connection and try again.",
        System.IO.IOException       => "Ran out of space. Free some up and try again.",
        UnauthorizedAccessException => "This phone blocked the download.",
        _ when ex.Message.Contains("SHA", StringComparison.OrdinalIgnoreCase)
                                    => "The download got damaged on the way. Try again.",
        _                           => "Something went wrong. Try again.",
    };
}
