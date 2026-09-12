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

namespace CircleAI.Assistant.Device;

[Activity(Label = "What it can do", Exported = false)]
public class AbilitiesActivity : Activity
{
    /// <summary>The abilities this head has a screen for.</summary>
    /// <remarks>
    /// THE ONLY THING THIS SCREEN STILL DECIDES. The list of abilities, which
    /// model serves each one, whether it is on this phone and whether it is
    /// actually running are all DeviceFacts' answers now - the same ones
    /// Settings gives in the Blazor head, which is the point.
    /// <para>
    /// What genuinely differs between the two heads is which screens exist. This
    /// one has an activity for every ability with a route; the Blazor head has a
    /// page only for waking. A row that looks tappable and does nothing is worse
    /// than a plain one, so the head that knows says so.
    /// </para>
    /// </remarks>
    static readonly string[] Screens =
    {
        "Listening", "Seeing", "Music", "Translating", "Finding",
#if IT_VOICE_ANDROID
        // WAKING IS LISTED ONLY WHEN THE BUILD CAN ACTUALLY WAKE. The chat-only
        // APK ships without the speech stack, so HandsFree and the wake screen
        // are compiled out - but the wake MODEL can still be sitting on disk
        // from an earlier install, and a list driven by what is on disk
        // cheerfully advertised "Waking ✓ On" on a phone that cannot wake at
        // all. Seen on the P30: Waking read On while the home screen correctly
        // said "Tap and talk".
        //
        // Files on disk are not an ability. An ability is code that runs.
        "Waking",
#endif
    };

    DeviceFacts?               _facts;
    IReadOnlyList<AbilityRow>? _rows;
    PhoneFacts?                _phone;

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
        ActionBar?.Hide();      // it repeated the title, and it is system-themed
        AndroidDeviceMemory.Install(this);

        // MODELSTORE, NOT SpecialFolder.ApplicationData, AND THEY ARE NOT THE
        // SAME DIRECTORY ON A PHONE. ModelPaths found this by looking at a disk:
        //
        //     files directory                 ->  /data/user/0/<pkg>/files
        //     SpecialFolder.ApplicationData   ->  /data/user/0/<pkg>/files/.config
        //
        // The second is a subdirectory of the first, so both exist, both are
        // writable and both look right in a log - which is how a 523 MB model
        // was downloaded twice onto a phone with 890 MB of app data. The session
        // reads ModelPaths.Default and says in its own comment that "the library
        // loaders were fixed and this sample was not": this screen was reporting
        // on, and downloading into, a directory the assistant never opens.
        _modelDir = ModelStore.Path;
        _registry = new ModelRegistryService();
        _loader   = new BundleModelLoader(_modelDir, _registry);
        _facts    = new DeviceFacts(Screens);

        BuildUi();
        Refresh();
    }

    void BuildUi()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Ui.Bg);

        // The wordmark was here already but did nothing — a title where the way home
        // belonged. It is now the way home.
        root.AddView(Ui.HomeBar(this, "What it can do"), Ui.Fill());

        var header = new LinearLayout(this) { Orientation = Orientation.Vertical };
        header.SetBackgroundColor(Ui.Surface);
        header.SetPadding(Ui.Dp(this, 20), 0, Ui.Dp(this, 20), 0);

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

    /// <summary>Redraw, fetching the phone's answers off the UI thread.</summary>
    /// <remarks>
    /// ASYNC BECAUSE THE ANSWER COSTS DISK. It used to be synchronous and cheap
    /// only by accident - it asked the registry directly and called ModelExists,
    /// which hashes a 470 MB anchor file at 9.4 s a go, ON THE UI THREAD, once
    /// per ability. DeviceFacts does the work on a worker and the tabs paint
    /// immediately, so the screen is responsive while the answer arrives.
    /// </remarks>
    /// <summary>The shared answer, corrected for what THIS build and phone have.</summary>
    /// <remarks>
    /// TWO THINGS THE SHARED ANSWER CANNOT KNOW, and both are about this head
    /// rather than about the assistant.
    /// <para>
    /// A WAKE BUNDLE THE OWNER COPIED ONTO THE PHONE counts as installed.
    /// DeviceFacts asks the model loader, which only knows about downloads -
    /// so without this the list offers to fetch something already sitting on
    /// the device, which is precisely the wrong answer for somebody who
    /// side-loaded it because they have no data to spend.
    /// </para>
    /// <para>
    /// AND THE CHAT-ONLY APK HAS NO SPEECH STACK AT ALL. Waking is compiled out
    /// of it, so the row must not be there - a list driven by what is on disk
    /// advertised "Waking &#x2713; On" on a phone that could not wake, which is
    /// the oldest lie on this screen.
    /// </para>
    /// </remarks>
    IReadOnlyList<AbilityRow> ThisBuild(IReadOnlyList<AbilityRow> rows)
    {
#if IT_VOICE_ANDROID
        var sideloaded = false;
        try { sideloaded = WakeWordActivity.SideloadedBundle(this) is not null; }
        catch { /* an absent bundle is the normal case, not an error */ }
        if (!sideloaded) return rows;

        return [.. rows.Select(r => r.Title == "Waking" && r.State is
                    AbilityState.Available or AbilityState.TooBig or AbilityState.NotCatalogued
            ? r with { State = AbilityState.Ready, Bytes = null }
            : r)];
#else
        return [.. rows.Where(r => r.Title != "Waking")];
#endif
    }

    /// <summary>Throw away the cached answers and redraw.</summary>
    /// <remarks>
    /// A DOWNLOAD CHANGES THE ANSWER AND NOTHING ELSE DOES. Refresh holds the
    /// rows between redraws so flipping a tab does not re-walk the disk; this is
    /// the one event that invalidates them.
    /// </remarks>
    void Reread()
    {
        _rows  = null;
        _phone = null;
        Refresh();
    }

    async void Refresh()
    {
        if (_list is null || _registry is null || _tabs is null) return;
        _list.RemoveAllViews();
        _tabs.RemoveAllViews();

        for (var i = 0; i < TabNames.Length; i++)
        {
            var index    = i;
            var selected = i == _tab;
            var tab = Ui.Label(this, TabNames[i], 15f, selected ? Ui.Blue : Ui.InkSoft, bold: selected);
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

        // Held between redraws so flipping tabs or toggling the technical list
        // does not re-walk the disk - the answer has not changed in the time it
        // takes to tap.
        try
        {
            if (_tab == 0)
            {
                _rows ??= ThisBuild(await _facts!.AbilitiesAsync());
                if (_list is null || _tab != 0) return;
                ShowAbilities(_rows);
            }
            else
            {
                _phone ??= await _facts!.PhoneAsync();
                if (_list is null || _tab != 1) return;
                ShowPhone(_phone);
            }
        }
        catch (Exception ex)
        {
            // A screen that says nothing is indistinguishable from a crash.
            _list?.AddView(Ui.Label(this, ex.Message, 13f, Ui.InkSoft), Ui.Fill());
        }
    }

    void ShowAbilities(IReadOnlyList<AbilityRow> rows)
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

        for (var i = 0; i < rows.Count; i++)
        {
            if (i > 0) panel.AddView(Divider(), Ui.Fill());
            panel.AddView(Row(rows[i]), Ui.Fill());
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
    void ShowPhone(PhoneFacts facts)
    {
        var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
        card.Background = Ui.Rounded(this, Ui.Surface, 14f);
        card.SetPadding(Ui.Dp(this, 18), Ui.Dp(this, 16), Ui.Dp(this, 18), Ui.Dp(this, 16));

        void Line(string title, string value)
        {
            card.AddView(Ui.Label(this, title, 13f, Ui.InkSoft));
            var v = Ui.Label(this, value, 16f, Ui.Blue);
            v.SetPadding(0, Ui.Dp(this, 2), 0, Ui.Dp(this, 14));
            card.AddView(v);
        }

        // THE SAME LINES SETTINGS SHOWS IN THE OTHER HEAD, from the same place.
        // Both screens built this list themselves, so this one carried "Frees
        // memory after", the other carried the build number, and neither had the
        // other's. Both have both now.
        foreach (var f in facts.Facts) Line(f.Title, f.Value);

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

        // A READOUT THAT NAMED A DIFFERENT MODEL FROM THE ROW ABOVE IT. This
        // ordered by "installed, then best quality" while the row itself used
        // ModelChoice - so a diagnostics screen actively misled whoever came to
        // it to diagnose something. One rule, and it is not this file's.
        foreach (var line in facts.Technical)
        {
            tech.AddView(Ui.Label(this, line, 11.5f, Ui.InkSoft));
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

    /// <summary>The screen that demonstrates an ability, where one exists.</summary>
    /// <remarks>
    /// SEEING WAS THE ROW THAT OFFERED A DOWNLOAD TO NOWHERE. Every other part
    /// of vision was built - two models catalogued with their hashes, the bridge
    /// able to encode an image, RunImageTurnAsync complete - and the abilities
    /// list would cheerfully fetch 311 MB for an ability with no screen behind
    /// it. The tick then said On for something a person could not do.
    /// </remarks>
    /// <remarks>
    /// KEYED ON THE ABILITY, NOT THE MODALITY, and it had to change the moment a
    /// second ability shared one. Translating rides the CHAT model - there is no
    /// translation model to download - so it and Answering are both
    /// <see cref="ModelModality.Chat"/>, and a modality-keyed lookup would have
    /// sent "Answering" to the translate screen as well.
    /// <para>
    /// A row's identity is what it DOES. The modality is only which models
    /// serve it, and two things can be served by one model - which is the same
    /// realisation as NeedsNoModel above: the list kept deriving a row's
    /// behaviour from its model when the row is the thing a person taps.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// KEYED ON THE ROUTE THE SHARED CATALOGUE GAVE, not on a title spelled here
    /// a second time. The route only reaches a row at all if Screens above
    /// declared this head has the screen, so the two lists cannot disagree
    /// without one of them being edited.
    /// </remarks>
    static Type? ScreenFor(string? route) => route switch
    {
#if IT_VOICE_ANDROID
        "wake" => typeof(WakeWordActivity),
#endif
        "seeing" => typeof(SeeingActivity),

        // LISTENING WAS THE OTHER DOWNLOAD TO NOWHERE. Whisper has been
        // catalogued and fetchable for as long as this row has existed, and
        // there was nothing behind it - this head could not transcribe a file
        // OR a microphone. TranscribeActivity is what the row now leads to.
        "transcribe" => typeof(TranscribeActivity),
        "music" => typeof(MusicActivity),
        "translate" => typeof(TranslateActivity),
        "find" => typeof(SearchActivity),

        _ => null,
    };

    /// <summary>One compact row: what it does on the left, its state on the right.</summary>
    /// <remarks>
    /// IT DECIDES NOTHING NOW, AND THAT IS THE CHANGE. This method used to scan
    /// the registry by modality, call ModelExists on every candidate and apply
    /// its own idea of what fits - a rule that had drifted from the one Settings
    /// uses in the other head and from the one the chat screen uses in this one.
    /// It is handed a row and draws it.
    /// </remarks>
    View Row(AbilityRow ability)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Ui.Dp(this, 16), Ui.Dp(this, 12), Ui.Dp(this, 14), Ui.Dp(this, 12));

        var text = new LinearLayout(this) { Orientation = Orientation.Vertical };
        text.AddView(Ui.Label(this, ability.Title, 16f, Ui.Blue, bold: true));

        // The size belongs to a row that is offering a download and to no other.
        var sub = ability.Bytes is { } bytes
            ? ability.Blurb + "  " + Dot + "  " + ModelChoice.Size(bytes)
            : ability.Blurb;
        var blurb = Ui.Label(this, sub, 12.5f, Ui.InkSoft);
        blurb.SetPadding(0, Ui.Dp(this, 2), 0, 0);
        text.AddView(blurb);
        row.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        var screen = ScreenFor(ability.TryRoute);

        switch (ability.State)
        {
            case AbilityState.On:
                // An ability that is ON should be somewhere you can GO, not just
                // a tick - but a row that looks tappable and does nothing is
                // worse than a plain one.
                if (screen is not null) Go(row, "Try it  " + Chevron, screen);
                else row.AddView(Ui.Label(this, "\u2713 On", 14f, Ui.Blue, bold: true));
                break;

            case AbilityState.Ready:
                // READY IS NOT ON, AND THIS ROW USED TO PRINT A TICK FOR IT.
                // Everything is downloaded and nothing is running: what that
                // calls for is a switch, not a size and not a tick. The screen
                // behind the route is the one that starts it.
                if (screen is not null) Go(row, "Turn on  " + Chevron, screen);
                else row.AddView(Ui.Label(this, "Ready", 12f, Ui.InkSoft));
                break;

            case AbilityState.Available:
                // Returns the WRAPPER, not the row: the progress area has to sit
                // under it and the caller adds one view to the panel.
                return Download(row, ability);

            case AbilityState.TooBig:
                row.AddView(Ui.Label(this, "Needs more memory", 12f, Ui.InkSoft));
                break;

            default:
                // NOTHING CATALOGUED. Different from "will not fit", and the
                // screen must not confuse them: "needs more memory" tells a
                // person their phone is the problem and invites them to go buy a
                // better one, for a model that does not exist on any phone yet.
                // That is our gap and it should read like our gap.
                row.AddView(Ui.Label(this, "Not ready yet", 12f, Ui.InkSoft));
                break;
        }

        return row;
    }

    const string Dot     = "\u00b7";
    const string Chevron = "\u203a";

    /// <summary>Make the whole row open a screen.</summary>
    void Go(LinearLayout row, string label, Type screen)
    {
        row.AddView(Ui.Label(this, label, 14f, Ui.Blue, bold: true));
        row.Clickable = true;
        row.Click += (_, _) => StartActivity(new Intent(this, screen));
    }

    /// <summary>The row, its Turn on button, and the progress area beneath.</summary>
    View Download(LinearLayout row, AbilityRow ability)
    {
        var get = Compact("Turn on");
        var bar = new ProgressBar(this, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal)
        { Max = 1000, Visibility = ViewStates.Gone };
        var pct = Ui.Label(this, "", 11.5f, Ui.Blue);
        pct.Visibility = ViewStates.Gone;

        // Progress lives UNDER the row so starting a download does not reflow the
        // list and shove the next ability off the screen mid-tap.
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
        row.AddView(get);

        get.Click += (_, _) =>
        {
            // WHICH MODEL THIS ROW MEANS, ASKED OF THE SHARED CATALOGUE. Asked
            // here rather than carried on the row because it is only needed the
            // moment somebody taps, and because a size on a row and a different
            // model in the download is the one mistake in this screen that
            // actually spends a person's data.
            var model = DeviceFacts.ChoiceFor(
                ability.Title, _registry!, _loader!, DeviceProbe.Snapshot());
            if (model is null)
            {
                pct.Visibility = ViewStates.Visible;
                pct.Text = "Nothing that fits this phone.";
                return;
            }
            meta.Visibility = ViewStates.Visible;
            Turn(model, get, bar, pct);
        };

        return wrap;
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
        // Asking for it again is the clearest possible withdrawal of a refusal.
        SetupPrefs.Allow(this, model.Name);

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
            RunOnUiThread(Reread);
        }
        catch (System.OperationCanceledException)
        {
            RunOnUiThread(Reread);
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
            .SetMessage($"Frees {ModelChoice.Size(model.TotalBytes)}. Turning it back on downloads it again.")
            .SetNegativeButton("Keep it", (s, e) => { })
            .SetPositiveButton("Turn off", (s, e) =>
            {
                try
                {
                    var dir = System.IO.Path.Combine(_modelDir, model.Name);
                    if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);

                    // Off has to STAY off. The home screen finishes an unfinished
                    // setup by itself on resume, so without this the model they
                    // just removed comes straight back and the switch reads broken.
                    SetupPrefs.Decline(this, model.Name);
                }
                catch (Exception ex)
                {
                    Toast.MakeText(this, ex.Message, ToastLength.Long)?.Show();
                }
                Refresh();
            })
            .Show();

    // ── plain language ───────────────────────────────────────────────────────

    // Installed, Fits and Size were here. All three are ModelChoice's now, and
    // all three had drifted: Installed hashed a 470 MB file to answer "is it
    // there" (9.4 s a candidate, on the UI thread), Fits was a second copy of a
    // rule that also lives in ModelChoice, and Size was a third copy of a
    // formatter. The screen that shows an answer should not be a place the
    // answer is decided.

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
