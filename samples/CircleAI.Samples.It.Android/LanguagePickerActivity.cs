// LanguagePickerActivity.cs
//
// The front door. CircleAI speaks 74 languages entirely on the phone, and until
// this screen existed there was no way for a person holding the device to hear
// any of them — the language was an adb intent extra, so the single most
// remarkable thing about the project was reachable only by its authors.
//
// One tap: pick a language, the voice downloads if it is not already there, and
// the phone says a greeting out loud. No account, no network round-trip for the
// speech itself, and nothing leaves the device.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using CircleAI.Inference;
using CircleAI.Core.Models;

namespace CircleAI.Samples.It.Mobile;

[Activity(Label = "Languages", ParentActivity = typeof(HomeActivity))]
public class LanguagePickerActivity : Activity
{
    /// <summary>
    /// What each language is called, and a greeting in it.
    /// </summary>
    /// <remarks>
    /// Both columns are shown to the user, so both have to be right. Where a
    /// greeting could not be confirmed the entry is left null and the screen falls
    /// back to naming the language in its own words rather than inventing a
    /// sentence — a demo that mispronounces a made-up phrase at a native speaker
    /// is worse than one that says less.
    /// </remarks>
    static readonly Dictionary<string, (string Name, string Native, string? Greeting)> Languages = new()
    {
        ["af"]   = ("Afrikaans",       "Afrikaans",   "Hallo wêreld. Hoe gaan dit met jou?"),
        ["ak"]   = ("Akan",            "Twi",          "Akwaaba. Wo ho te sɛn?"),
        ["am"]   = ("Amharic",         "አማርኛ",         "ሰላም ለዓለም። እንዴት ናችሁ?"),
        ["ar"]   = ("Arabic",          "العربية",       "مرحبا بالعالم. كيف حالك؟"),
        ["bem"]  = ("Bemba",           "Ichibemba",    "Muli shani? Ndi bwino."),
        ["bm"]   = ("Bambara",         "Bamanankan",   "I ni ce. I ka kɛnɛ wa?"),
        ["bn"]   = ("Bengali",         "বাংলা",         "হ্যালো বিশ্ব। আপনি কেমন আছেন?"),
        ["ee"]   = ("Ewe",             "Eʋegbe",       "Ŋdi na wò. Èfɔa nyuie a?"),
        ["en"]   = ("English",         "English",      "Hello world. How are you today?"),
        // es-ES and es-MX are separate rows because they are separate to the
        // people who speak them. Collapsing them under "Spanish" served one and
        // silently told the other they were the same, which they are not.
        ["es-ES"] = ("Spanish (Spain)",  "Español (España)", "Hola mundo. ¿Cómo estás hoy?"),
        ["es-MX"] = ("Spanish (Mexico)", "Español (México)", "Hola mundo. ¿Cómo estás hoy?"),
        ["fa"]   = ("Persian",         "فارسی",         "سلام دنیا. حال شما چطور است؟"),
        ["ff"]   = ("Fula",            "Fulfulde",     "Jam waali. No mbaɗɗaa?"),
        ["fon"]  = ("Fon",             "Fɔngbè",       "Kúdó. A fɔ́n gánjí à?"),
        ["fr"]   = ("French",          "Français",     "Bonjour le monde. Comment allez-vous ?"),
        ["gn"]   = ("Guarani",         "Avañe'ẽ",      "Mba'éichapa. Iporãnte."),
        ["gu"]   = ("Gujarati",        "ગુજરાતી",       "નમસ્તે વિશ્વ. તમે કેમ છો?"),
        ["ha"]   = ("Hausa",           "Harshen Hausa", "Sannu duniya. Yaya kake?"),
        ["hi"]   = ("Hindi",           "हिन्दी",         "नमस्ते दुनिया। आप कैसे हैं?"),
        ["ht"]   = ("Haitian Creole",  "Kreyòl",       "Bonjou monn. Kijan ou ye?"),
        ["id"]   = ("Indonesian",      "Bahasa Indonesia", "Halo dunia. Apa kabar?"),
        ["ig"]   = ("Igbo",            "Asụsụ Igbo",   "Ndewo ụwa. Kedu ka ị mere?"),
        ["ja"]   = ("Japanese",        "日本語",        "こんにちは世界。お元気ですか。"),
        ["jv"]   = ("Javanese",        "Basa Jawa",    "Sugeng enjing. Piye kabare?"),
        ["ki"]   = ("Kikuyu",          "Gĩkũyũ",       "Ũhoro waku? Nĩ mwega."),
        ["kn"]   = ("Kannada",         "ಕನ್ನಡ",         "ನಮಸ್ಕಾರ ಜಗತ್ತು. ಹೇಗಿದ್ದೀರಿ?"),
        ["kr"]   = ("Kanuri",          "Kanuri",       "Ndaram. Awo cira?"),
        ["lg"]   = ("Luganda",         "Luganda",      "Oli otya? Ndi bulungi."),
        ["lgg"]  = ("Lugbara",         "Lugbarati",    "Mi ngoni? Ma muke."),
        ["ln"]   = ("Lingala",         "Lingála",      "Mbote na yo. Ozali malamu?"),
        ["mg"]   = ("Malagasy",        "Malagasy",     "Manao ahoana. Salama tsara."),
        ["ml"]   = ("Malayalam",       "മലയാളം",       "നമസ്കാരം ലോകം. സുഖമാണോ?"),
        ["mos"]  = ("Mossi",           "Mooré",        "Ne y windga. Y kibare?"),
        ["mr"]   = ("Marathi",         "मराठी",         "नमस्कार जग. तुम्ही कसे आहात?"),
        ["my"]   = ("Burmese",         "မြန်မာ",        "မင်္ဂလာပါ ကမ္ဘာ။ နေကောင်းလား။"),
        ["ne"]   = ("Nepali",          "नेपाली",         "नमस्ते संसार। तपाईं कस्तो हुनुहुन्छ?"),
        ["nl-NL"] = ("Dutch",           "Nederlands",         "Hallo wereld. Hoe gaat het met je?"),
        ["nl-BE"] = ("Flemish",         "Vlaams",             "Hallo wereld. Hoe gaat het met je?"),
        ["nr"]   = ("isiNdebele",      "isiNdebele",   "Lotjhani phasi. Unjani namhlanje?"),
        ["nso"]  = ("Sepedi",          "Sepedi",       "Dumela lefase. O kae lehono?"),
        ["ny"]   = ("Chichewa",        "Chichewa",     "Moni. Muli bwanji?"),
        ["nyn"]  = ("Nyankole",        "Runyankole",   "Agandi? Nimarungi."),
        ["om"]   = ("Oromo",           "Afaan Oromoo", "Akkam jirta? Nagaa dha."),
        ["pa"]   = ("Punjabi",         "ਪੰਜਾਬੀ",         "ਸਤ ਸ੍ਰੀ ਅਕਾਲ ਦੁਨੀਆ। ਤੁਸੀਂ ਕਿਵੇਂ ਹੋ?"),
        ["pt-BR"] = ("Portuguese (Brazil)",   "Português (Brasil)",   "Olá mundo. Como você está hoje?"),
        ["pt-PT"] = ("Portuguese (Portugal)", "Português (Portugal)", "Olá mundo. Como está hoje?"),
        ["qu"]   = ("Quechua",         "Runa Simi",    "Allillanchu? Allinmi kani."),
        ["rn"]   = ("Kirundi",         "Ikirundi",     "Amakuru? Ni meza."),
        ["ru"]   = ("Russian",         "Русский",      "Привет мир. Как дела сегодня?"),
        ["rw"]   = ("Kinyarwanda",     "Ikinyarwanda", "Amakuru? Ni meza."),
        ["sg"]   = ("Sango",           "Sängö",        "Bara ala. Tongana nyen?"),
        ["si"]   = ("Sinhala",         "සිංහල",        "ආයුබෝවන් ලෝකය. කොහොමද?"),
        ["sn"]   = ("Shona",           "chiShona",     "Mhoro nyika. Wakadii nhasi?"),
        ["so"]   = ("Somali",          "Soomaali",     "Salaan aduunka. Sidee tahay?"),
        ["ss"]   = ("siSwati",         "siSwati",      "Sawubona live. Unjani lamuhla?"),
        ["st"]   = ("Sesotho",         "Sesotho",      "Dumela lefatshe. O phela joang?"),
        ["su"]   = ("Sundanese",       "Basa Sunda",   "Wilujeng énjing. Kumaha damang?"),
        ["sw"]   = ("Swahili",         "Kiswahili",    "Habari dunia. Hujambo leo?"),
        ["ta"]   = ("Tamil",           "தமிழ்",         "வணக்கம் உலகம். எப்படி இருக்கிறீர்கள்?"),
        ["te"]   = ("Telugu",          "తెలుగు",        "నమస్కారం ప్రపంచం. ఎలా ఉన్నారు?"),
        ["th"]   = ("Thai",            "ไทย",           "สวัสดีชาวโลก สบายดีไหม"),
        ["ti"]   = ("Tigrinya",        "ትግርኛ",         "ሰላም ዓለም። ከመይ ኣለኻ?"),
        ["tl"]   = ("Filipino",        "Tagalog",      "Kumusta mundo. Kumusta ka ngayon?"),
        ["tn"]   = ("Setswana",        "Setswana",     "Dumela lefatshe. O tsogile jang?"),
        ["tpi"]  = ("Tok Pisin",       "Tok Pisin",    "Gude world. Yu orait?"),
        ["ts"]   = ("Xitsonga",        "Xitsonga",     "Avuxeni misava. U njhani namuntlha?"),
        ["ur"]   = ("Urdu",            "اردو",          "ہیلو دنیا۔ آپ کیسے ہیں؟"),
        ["ve"]   = ("Tshivenda",       "Tshivenḓa",    "Ndaa shango. Vho vuwa hani?"),
        ["vi"]   = ("Vietnamese",      "Tiếng Việt",   "Xin chào thế giới. Bạn khỏe không?"),
        ["xh"]   = ("isiXhosa",        "isiXhosa",     "Molo mhlaba. Unjani namhlanje?"),
        ["yo"]   = ("Yoruba",          "Yorùbá",       "Pẹlẹ o ayé. Bawo ni o ṣe wà?"),
        ["yue"]  = ("Cantonese",       "粵語",          "你好世界。你今日好嗎？"),
        ["zh"]   = ("Mandarin",        "中文",          "你好世界。你今天好吗？"),
        ["zu"]   = ("isiZulu",         "isiZulu",      "Sawubona mhlaba. Unjani namuhla?"),
    };

    readonly List<Row> _rows = new();
    LinearLayout _list = null!;
    EditText _search = null!;
    TextView _status = null!;
    CancellationTokenSource? _running;

    sealed record Row(string Tag, string Name, string Native, string Phrase, long Bytes, View Card, TextView Sub);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActionBar?.Hide();
        BuildUi();
        LoadLanguages();
    }

    void BuildUi()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Ui.Bg);

        // HOME, NOT BACK. This screen had a "‹ Back" link calling Finish(), which
        // is a statement about history rather than about the product — reached from
        // the typing screen it returned you to typing, not to the circle. The bar
        // goes to the circle from wherever you are.
        root.AddView(Ui.HomeBar(this, "Languages"), Ui.Fill());

        var head = new LinearLayout(this) { Orientation = Orientation.Vertical };
        head.SetBackgroundColor(Ui.Surface);
        head.SetPadding(Ui.Dp(this, 20), Ui.Dp(this, 16), Ui.Dp(this, 20), Ui.Dp(this, 16));

        head.AddView(Ui.Label(this, "Pick a language", 26f, Ui.Ink, bold: true));

        _status = Ui.Label(this, "Loading…", 14f, Ui.InkSoft);
        _status.SetPadding(0, Ui.Dp(this, 6), 0, 0);
        head.AddView(_status);

        _search = new EditText(this) { Hint = "Search 74 languages" };
        _search.SetTextColor(Ui.Ink);
        _search.SetHintTextColor(Ui.InkSoft);
        _search.SetSingleLine(true);
        _search.TextSize = 16f;
        _search.Background = Ui.Rounded(this, Ui.Raised, 10f);
        _search.SetPadding(Ui.Dp(this, 14), Ui.Dp(this, 12), Ui.Dp(this, 14), Ui.Dp(this, 12));
        _search.TextChanged += (s, e) => Filter(_search.Text ?? "");
        var sp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        sp.TopMargin = Ui.Dp(this, 14);
        head.AddView(_search, sp);

        root.AddView(head, Ui.Fill());

        var scroll = new ScrollView(this);
        // Scrollbars are visual noise on a list this long and the house rule is to
        // never show them; the content still scrolls exactly as before.
        scroll.VerticalScrollBarEnabled = false;
        _list = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _list.SetPadding(Ui.Dp(this, 12), Ui.Dp(this, 8), Ui.Dp(this, 12), Ui.Dp(this, 24));
        scroll.AddView(_list);
        root.AddView(scroll, Ui.Fill(1f));

        SetContentView(root);
    }

    void LoadLanguages()
    {
        using var registry = new ModelRegistryService();
        var voices = registry.AllModels
            .Where(m => m.Modality == CircleAI.Core.ModelModality.Tts)
            .ToList();

        // One row per LANGUAGE, not per voice: a person looking for Portuguese does
        // not care that two regional voices serve it.
        //
        // WHICH voice, though, is not this screen's decision to make. It used to
        // pick the smallest — reasonable on its own terms, megabytes being money
        // — while the thing that actually speaks asks SpeechModelSelector. Two
        // rules, so the row could describe a voice you would never hear: Japanese
        // read "122 MB" for the old table-driven voice while "Hear it" played the
        // 137.6 MB Open JTalk one. A size that belongs to a different voice is
        // worse than no size, because it looks checked.
        //
        // So the label asks the same selector the probe does, and the row now
        // describes the voice that will play.
        ISpeechModelSelector selector = new SpeechModelSelector(registry);
        var device = CircleAI.Core.DeviceProbe.Snapshot();

        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in voices)
            foreach (var raw in (v.Language ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = raw.Trim();
                if (t.Length > 0) tags.Add(t);
            }

        var best = new Dictionary<string, (string Id, long Bytes)>();
        foreach (var tag in tags)
        {
            try
            {
                var plan = selector.PlanFor(device, CircleAI.Core.ModelModality.Tts, tag);
                if (plan.IsAvailable && plan.Model is not null)
                {
                    var entry = registry.GetLatestModel(plan.Model.ModelId);
                    if (entry is not null) { best[tag] = (entry.Name, entry.TotalBytes); continue; }
                }
            }
            catch
            {
                // A selector that cannot answer for one language must not empty
                // the whole list; fall through to the smallest-voice estimate.
            }

            // Fallback only: the selector declined or threw. Better a rough size
            // than a row that vanishes.
            foreach (var v in voices)
                foreach (var raw in (v.Language ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (string.Equals(raw.Trim(), tag, StringComparison.OrdinalIgnoreCase)
                        && (!best.TryGetValue(tag, out var cur) || v.TotalBytes < cur.Bytes))
                        best[tag] = (v.Name, v.TotalBytes);
        }

        foreach (var tag in best.Keys.OrderBy(t => Display(t).Name, StringComparer.OrdinalIgnoreCase))
        {
            var (name, native, greeting) = Display(tag);
            var phrase = greeting ?? native;
            AddRow(tag, name, native, phrase, best[tag].Bytes);
        }

        _status.Text = $"{_rows.Count} languages · runs on this phone · nothing leaves the device";
    }

    static (string Name, string Native, string? Greeting) Display(string tag) =>
        Languages.TryGetValue(tag, out var d) ? d : (tag, tag, null);

    void AddRow(string tag, string name, string native, string phrase, long bytes)
    {
        // A SCREEN CALLED "PICK A LANGUAGE" HAS TO PICK ONE. The row's only click
        // handler used to be Speak(row) — tapping played a sample and did nothing
        // else, so a person selected their language, heard it, and was left on the
        // same screen with no way forward and nothing applied. Reported as a stall,
        // and it was one: the tap spoke, and that was the whole behaviour.
        //
        // Tapping the row now SELECTS. Hearing it first is still worth having, so
        // it keeps its own control rather than stealing the tap that everybody will
        // read as "choose this one".
        var card = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        card.Background = Ui.Rounded(this, Ui.Surface);
        card.SetPadding(Ui.Dp(this, 16), Ui.Dp(this, 14), Ui.Dp(this, 16), Ui.Dp(this, 14));
        card.Clickable = true;
        card.SetGravity(GravityFlags.CenterVertical);

        // The words take the space; the listen button takes what it needs.
        var text = new LinearLayout(this) { Orientation = Orientation.Vertical };
        card.AddView(text, new LinearLayout.LayoutParams(0,
            ViewGroup.LayoutParams.WrapContent, 1f));

        var title = Ui.Label(this, name, 18f, Ui.Ink, bold: true);
        text.AddView(title);

        // Native name and size on one quiet line. The size matters: this is the
        // number that decides whether somebody on a metered connection taps.
        //
        // Forced left-to-right. Android picks a paragraph's direction from its
        // first strong character, so the Arabic, Persian and Urdu rows flipped the
        // whole line and rendered as "MB 114 · العربية" — the language's own name
        // displayed wrongly, on the screen meant to show it respect. The native
        // word still renders right-to-left within the line, which is correct; only
        // the line's own direction is pinned.
        var sub = Ui.Label(this, SubtitleFor(native, bytes), 14f, Ui.InkSoft);
        sub.TextDirection = Android.Views.TextDirection.Ltr;
        sub.TextAlignment = Android.Views.TextAlignment.ViewStart;
        sub.SetPadding(0, Ui.Dp(this, 4), 0, 0);
        text.AddView(sub);

        // WORDS, NOT A GLYPH. A bare speaker icon is a guess for anyone who has not
        // met one, and this screen is read by people meeting the product for the
        // first time. "Hear it" says what happens.
        var hear = Ui.Label(this, "Hear it", 15f, Ui.Blue, bold: true);
        hear.Clickable = true;
        hear.SetPadding(Ui.Dp(this, 14), Ui.Dp(this, 8), Ui.Dp(this, 4), Ui.Dp(this, 8));

        var lp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        lp.BottomMargin = Ui.Dp(this, 10);
        _list.AddView(card, lp);

        var row = new Row(tag, name, native, phrase, bytes, card, sub);
        _rows.Add(row);

        card.AddView(hear, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        card.Click += (s, e) => Choose(row);
        hear.Click += (s, e) => Speak(row);
    }

    /// <summary>Answers in this language from now on, and goes back to the circle.</summary>
    /// <remarks>
    /// PERSISTED, THEN LEFT. Storing it without leaving would be the same defect in
    /// a quieter form — the person would still be looking at a list, with no signal
    /// that their choice took. Saying it aloud in the language they just chose is
    /// the confirmation that needs no reading, and returning to the circle is where
    /// they were trying to get to.
    /// </remarks>
    void Choose(Row row)
    {
        _running?.Cancel();

        // Choose, not Set: this is a person deciding, and the next turn's
        // detection must not quietly undo it.
        SpokenLanguage.Choose(this, row.Tag);
        Android.Util.Log.Info("CircleAI.It",
            $"language chosen: {row.Tag} ({row.Name}) — returning to the circle");

        Android.Widget.Toast
            .MakeText(this, $"Answering in {row.Name}", Android.Widget.ToastLength.Short)
            ?.Show();

        // AND MAKE IT LISTEN FOR THE NEW NAME. The wake phrase is fixed when the
        // resident listener is built, and returning to the circle does not
        // rebuild it — the wake loop is already running, so nothing re-enters
        // ResidentAssistant.StartAsync and the staleness check there never runs.
        // Verified on the phone: the language changed to Japanese and the
        // microphone went on waiting for "Hey B", with nothing on screen to say
        // so. The person deciding is the event that should trigger this, not a
        // service restart that may never happen.
        //
        // Fire-and-forget deliberately: rebuilding stops the old detector and
        // loads a model, which is too slow to hold a tap on, and the screen is
        // closing anyway. Failure leaves the previous wake word running, which is
        // the safe direction — see ResidentWakeWord.KeywordsFor.
        _ = RebuildWakeWordAsync(row.Tag);

        Finish();
    }

    /// <summary>Rebuilds the resident wake word for a newly chosen language.</summary>
    /// <remarks>
    /// Uses the application context, not this activity: Finish() runs immediately
    /// after this is started, and a rebuild that outlived the screen would
    /// otherwise be holding a destroyed Activity.
    /// </remarks>
    async Task RebuildWakeWordAsync(string tag)
    {
        try
        {
#if IT_VOICE_ANDROID
            var app = ApplicationContext;
            if (app is null) return;

            var bundle = WakeWordActivity.FindBundle(app);
            if (bundle is null)
            {
                Android.Util.Log.Info("CircleAI.Kws", "no wake bundle installed — nothing to rebuild");
                return;
            }

            var old = CircleAI.Device.CircleNeuronService.Listener;
            if (old is not null &&
                string.Equals(ResidentWakeWord.InstalledLanguage, tag,
                              StringComparison.OrdinalIgnoreCase))
                return;   // already listening in this language

            Android.Util.Log.Info("CircleAI.Kws",
                $"language changed to '{tag}' — rebuilding the wake word " +
                $"(was '{ResidentWakeWord.InstalledLanguage ?? "none"}')");

            CircleAI.Device.CircleNeuronService.Listener = null;
            if (old is not null)
            {
                // Stopped before the replacement is built: Android gives out
                // AudioRecord to one owner, so a detector that is merely dropped
                // keeps the microphone and the new one comes up deaf.
                try { await old.StopAsync().ConfigureAwait(false); } catch { }
                try { await old.DisposeAsync().ConfigureAwait(false); } catch { }
            }

            if (ResidentWakeWord.Install(app, bundle, languageCode: tag))
                await CircleAI.Device.CircleNeuronService.StartListeningAsync().ConfigureAwait(false);
#else
            await Task.CompletedTask;
#endif
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("CircleAI.Kws", "wake word rebuild failed: " + ex.Message);
        }
    }

    void Filter(string q)
    {
        q = q.Trim();
        var shown = 0;
        foreach (var r in _rows)
        {
            var hit = q.Length == 0
                   || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || r.Native.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || r.Tag.StartsWith(q, StringComparison.OrdinalIgnoreCase);
            r.Card.Visibility = hit ? ViewStates.Visible : ViewStates.Gone;
            if (hit) shown++;
        }
        _status.Text = q.Length == 0
            ? $"{_rows.Count} languages · runs on this phone · nothing leaves the device"
            : $"{shown} of {_rows.Count} match “{q}”";
    }

    async void Speak(Row row)
    {
        // One utterance at a time: tapping a second language mid-download used to
        // leave two synthesisers racing for the speaker.
        _running?.Cancel();
        var cts = new CancellationTokenSource();
        _running = cts;

        BeginHeartbeat(row, "preparing");
        try
        {
#if IT_VOICE_ANDROID
            var store = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "CircleAI", "Models");
            // Written to the EXTERNAL files dir when there is one. Same app-private
            // storage as far as the user is concerned, but adb can read it without
            // run-as — and run-as refuses on a Release build ("package not
            // debuggable"), which is the only build worth judging. Without this the
            // audio a language actually produces cannot be got off the phone, so
            // "it spoke" rests on a symbol count instead of on listening to it.
            var audioDir = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath;
            var wav = System.IO.Path.Combine(audioDir, $"say-{row.Tag}.wav");

            // OFF THE UI THREAD, DELIBERATELY. RunCataloguedAsync is `async`, but an
            // async method runs SYNCHRONOUSLY until its first real await, and on the
            // warm path — voice already downloaded, so the prereq/sideload/download
            // awaits are all skipped — the first one it reaches is the synthesis
            // itself. Everything before that (registry scan, config and tokens.txt
            // parsing, and the ONNX session build, which is the expensive part) was
            // therefore running on the UI thread.
            //
            // The symptom was not a missing progress line: the lines were being
            // produced all along and RunOnUiThread was faithfully QUEUEING them
            // behind the very work that was blocking the looper. They all flushed
            // at the end, so the row sat on "preparing…" for six seconds and the
            // app looked hung — which is exactly what it was. Task.Run frees the
            // looper so those callbacks land while the work is still happening.
            var report = await Task.Run(
                () => CircleAI.Samples.It.Voice.ItTtsProbe.RunCataloguedAsync(
                    store, row.Tag, row.Phrase, wav,
                    line => RunOnUiThread(() => Phase(row, Summarise(line))), cts.Token),
                cts.Token);

            if (cts.IsCancellationRequested) return;

            if (System.IO.File.Exists(wav) && report.Contains("SYNTHESIS OK", StringComparison.Ordinal))
            {
                EndHeartbeat();
                SetSub(row, $"“{row.Phrase}”");
                await MainActivity.PlayWavStaticAsync(wav);
                RestoreSub(row);
            }
            else
            {
                EndHeartbeat();
                // Say what actually failed, in the row the user tapped, instead of
                // logging it somewhere they will never look.
                SetSub(row, FirstLine(report));
            }
#else
            // The chat-only APK has no synthesiser, so the list still shows what it
            // would say in each language — the phrase, in the row you tapped. That
            // is the honest version of this screen without the speech stack, and it
            // is better than a button that looks live and does nothing.
            await Task.Yield();
            EndHeartbeat();
            SetSub(row, $"“{row.Phrase}”");
#endif
        }
        catch (System.OperationCanceledException) { EndHeartbeat(); RestoreSub(row); }
        catch (Exception ex) { EndHeartbeat(); SetSub(row, ex.Message); }
    }

    // ---- the row's "still working" heartbeat -------------------------------
    //
    // Freeing the looper (above) is what makes progress possible; this is what
    // makes it VISIBLE. Even running properly off-thread, one stage dominates —
    // building the ONNX session takes seconds and reports once, at the start. A
    // caption that is correct but motionless for four seconds still reads as a
    // hang to the person holding the phone, so the caption animates while the
    // stage it names is still running. The text is the truth; the dots are the
    // proof that something is still happening.

    Android.OS.Handler? _beat;
    Java.Lang.IRunnable? _beatTick;
    string _beatPhase = "";
    int _beatDots;

    void BeginHeartbeat(Row row, string phase)
    {
        EndHeartbeat();
        _beatPhase = phase;
        _beatDots  = 0;
        _beat = new Android.OS.Handler(Android.OS.Looper.MainLooper!);
        _beatTick = new Java.Lang.Runnable(() =>
        {
            _beatDots = (_beatDots + 1) % 4;
            SetSub(row, _beatPhase + new string('.', _beatDots));
            _beat?.PostDelayed(_beatTick!, 400);
        });
        SetSub(row, _beatPhase);
        _beat.PostDelayed(_beatTick, 400);
    }

    /// <summary>Swap the caption without losing the animation.</summary>
    void Phase(Row row, string phase)
    {
        if (_beat is null) { SetSub(row, phase); return; }
        _beatPhase = phase.TrimEnd('.', '…');
        _beatDots  = 0;
        SetSub(row, _beatPhase);
    }

    void EndHeartbeat()
    {
        if (_beat is not null && _beatTick is not null) _beat.RemoveCallbacks(_beatTick);
        _beat = null;
        _beatTick = null;
    }

    /// <summary>Turn an engine log line into something worth showing a person.</summary>
    /// <remarks>
    /// Anything that falls through to "working" is a stage the person cannot name,
    /// and on the warm path most of them did — the engine emits eight or nine lines
    /// and only three were recognised. A caption that says "working" for six seconds
    /// carries no more information than a frozen one, which is the complaint this
    /// screen earned. Each branch below is a stage the engine actually reports, in
    /// the order it reports them.
    ///
    /// ORDER MATTERS: "voice-under-test" also starts with "voice", so the specific
    /// prefix has to be tested before the general one or every voice line collapses
    /// into "found the voice".
    /// </remarks>
    static string Summarise(string line)
    {
        line = line.Trim();
        if (line.Contains("%", StringComparison.Ordinal))                             return "downloading… " + line;
        if (line.StartsWith("prereq", StringComparison.OrdinalIgnoreCase))            return "fetching what it needs";
        if (line.StartsWith("sideload", StringComparison.OrdinalIgnoreCase))          return "importing the voice";
        if (line.StartsWith("downloaded", StringComparison.OrdinalIgnoreCase))        return "downloaded — loading";
        if (line.StartsWith("voice-under-test", StringComparison.OrdinalIgnoreCase))  return "getting ready";
        if (line.StartsWith("voice", StringComparison.OrdinalIgnoreCase))             return "found the voice";
        if (line.StartsWith("engine", StringComparison.OrdinalIgnoreCase))
            return line.Contains("WARM", StringComparison.Ordinal) ? "voice ready" : "loading the voice";
        if (line.StartsWith("phones", StringComparison.OrdinalIgnoreCase))            return "sounding out the words";
        if (line.StartsWith("saying it", StringComparison.OrdinalIgnoreCase))         return "saying it";
        if (line.StartsWith("respelt", StringComparison.OrdinalIgnoreCase))           return "saying it";
        if (line.StartsWith("synthesised", StringComparison.OrdinalIgnoreCase))       return "ready to play";
        return "getting ready";
    }

    static string FirstLine(string report)
    {
        var line = report.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "could not speak";
        return line.Length > 90 ? line[..90] + "…" : line;
    }

    void SetSub(Row row, string text) => row.Sub.Text = text;

    void RestoreSub(Row row) => row.Sub.Text = SubtitleFor(row.Native, row.Bytes);

    /// <summary>"العربية · 114 MB", laid out left-to-right whatever the script.</summary>
    /// <remarks>
    /// Setting TextDirection on the view was not enough — the Arabic, Persian and
    /// Urdu rows still rendered as "MB 114 · العربية". Android chooses a line's
    /// direction from its first strong character, so the fix has to be in the
    /// string: U+2066 (isolate) wraps the native name so it renders right-to-left
    /// internally without flipping the line around it, and the leading U+200E makes
    /// the line itself unambiguously left-to-right.
    /// </remarks>
    static string SubtitleFor(string native, long bytes) =>
        $"‎⁦{native}⁩  ·  {bytes / 1_000_000} MB";

    protected override void OnDestroy()
    {
        _running?.Cancel();
        EndHeartbeat();
        base.OnDestroy();
    }
}
