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
        ["ak"]   = ("Akan",            "Twi",          null),
        ["am"]   = ("Amharic",         "አማርኛ",         "ሰላም ለዓለም። እንዴት ናችሁ?"),
        ["ar"]   = ("Arabic",          "العربية",       "مرحبا بالعالم. كيف حالك؟"),
        ["bem"]  = ("Bemba",           "Ichibemba",    null),
        ["bm"]   = ("Bambara",         "Bamanankan",   null),
        ["bn"]   = ("Bengali",         "বাংলা",         "হ্যালো বিশ্ব। আপনি কেমন আছেন?"),
        ["ee"]   = ("Ewe",             "Eʋegbe",       null),
        ["en"]   = ("English",         "English",      "Hello world. How are you today?"),
        // es-ES and es-MX are separate rows because they are separate to the
        // people who speak them. Collapsing them under "Spanish" served one and
        // silently told the other they were the same, which they are not.
        ["es-ES"] = ("Spanish (Spain)",  "Español (España)", "Hola mundo. ¿Cómo estás hoy?"),
        ["es-MX"] = ("Spanish (Mexico)", "Español (México)", "Hola mundo. ¿Cómo estás hoy?"),
        ["fa"]   = ("Persian",         "فارسی",         "سلام دنیا. حال شما چطور است؟"),
        ["ff"]   = ("Fula",            "Fulfulde",     null),
        ["fon"]  = ("Fon",             "Fɔngbè",       null),
        ["fr"]   = ("French",          "Français",     "Bonjour le monde. Comment allez-vous ?"),
        ["gn"]   = ("Guarani",         "Avañe'ẽ",      null),
        ["gu"]   = ("Gujarati",        "ગુજરાતી",       "નમસ્તે વિશ્વ. તમે કેમ છો?"),
        ["ha"]   = ("Hausa",           "Harshen Hausa", "Sannu duniya. Yaya kake?"),
        ["hi"]   = ("Hindi",           "हिन्दी",         "नमस्ते दुनिया। आप कैसे हैं?"),
        ["ht"]   = ("Haitian Creole",  "Kreyòl",       "Bonjou monn. Kijan ou ye?"),
        ["id"]   = ("Indonesian",      "Bahasa Indonesia", "Halo dunia. Apa kabar?"),
        ["ig"]   = ("Igbo",            "Asụsụ Igbo",   "Ndewo ụwa. Kedu ka ị mere?"),
        ["ja"]   = ("Japanese",        "日本語",        "こんにちは世界。お元気ですか。"),
        ["jv"]   = ("Javanese",        "Basa Jawa",    null),
        ["ki"]   = ("Kikuyu",          "Gĩkũyũ",       null),
        ["kn"]   = ("Kannada",         "ಕನ್ನಡ",         "ನಮಸ್ಕಾರ ಜಗತ್ತು. ಹೇಗಿದ್ದೀರಿ?"),
        ["kr"]   = ("Kanuri",          "Kanuri",       null),
        ["lg"]   = ("Luganda",         "Luganda",      null),
        ["lgg"]  = ("Lugbara",         "Lugbarati",    null),
        ["ln"]   = ("Lingala",         "Lingála",      "Mbote na yo. Ozali malamu?"),
        ["mg"]   = ("Malagasy",        "Malagasy",     null),
        ["ml"]   = ("Malayalam",       "മലയാളം",       "നമസ്കാരം ലോകം. സുഖമാണോ?"),
        ["mos"]  = ("Mossi",           "Mooré",        null),
        ["mr"]   = ("Marathi",         "मराठी",         "नमस्कार जग. तुम्ही कसे आहात?"),
        ["my"]   = ("Burmese",         "မြန်မာ",        "မင်္ဂလာပါ ကမ္ဘာ။ နေကောင်းလား။"),
        ["ne"]   = ("Nepali",          "नेपाली",         "नमस्ते संसार। तपाईं कस्तो हुनुहुन्छ?"),
        ["nl-NL"] = ("Dutch",           "Nederlands",         "Hallo wereld. Hoe gaat het met je?"),
        ["nl-BE"] = ("Flemish",         "Vlaams",             "Hallo wereld. Hoe gaat het met je?"),
        ["nr"]   = ("isiNdebele",      "isiNdebele",   "Lotjhani phasi. Unjani namhlanje?"),
        ["nso"]  = ("Sepedi",          "Sepedi",       "Dumela lefase. O kae lehono?"),
        ["ny"]   = ("Chichewa",        "Chichewa",     null),
        ["nyn"]  = ("Nyankole",        "Runyankole",   null),
        ["om"]   = ("Oromo",           "Afaan Oromoo", null),
        ["pa"]   = ("Punjabi",         "ਪੰਜਾਬੀ",         "ਸਤ ਸ੍ਰੀ ਅਕਾਲ ਦੁਨੀਆ। ਤੁਸੀਂ ਕਿਵੇਂ ਹੋ?"),
        ["pt-BR"] = ("Portuguese (Brazil)",   "Português (Brasil)",   "Olá mundo. Como você está hoje?"),
        ["pt-PT"] = ("Portuguese (Portugal)", "Português (Portugal)", "Olá mundo. Como está hoje?"),
        ["qu"]   = ("Quechua",         "Runa Simi",    null),
        ["rn"]   = ("Kirundi",         "Ikirundi",     null),
        ["ru"]   = ("Russian",         "Русский",      "Привет мир. Как дела сегодня?"),
        ["rw"]   = ("Kinyarwanda",     "Ikinyarwanda", null),
        ["sg"]   = ("Sango",           "Sängö",        null),
        ["si"]   = ("Sinhala",         "සිංහල",        "ආයුබෝවන් ලෝකය. කොහොමද?"),
        ["sn"]   = ("Shona",           "chiShona",     "Mhoro nyika. Wakadii nhasi?"),
        ["so"]   = ("Somali",          "Soomaali",     "Salaan aduunka. Sidee tahay?"),
        ["ss"]   = ("siSwati",         "siSwati",      "Sawubona live. Unjani lamuhla?"),
        ["st"]   = ("Sesotho",         "Sesotho",      "Dumela lefatshe. O phela joang?"),
        ["su"]   = ("Sundanese",       "Basa Sunda",   null),
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
        // not care that two regional voices serve it. Smallest wins — on the phones
        // this is built for, megabytes are money.
        var best = new Dictionary<string, (string Id, long Bytes)>();
        foreach (var v in voices)
        {
            foreach (var raw in (v.Language ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var tag = raw.Trim();
                if (tag.Length == 0) continue;
                if (!best.TryGetValue(tag, out var cur) || v.TotalBytes < cur.Bytes)
                    best[tag] = (v.Name, v.TotalBytes);
            }
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
        var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
        card.Background = Ui.Rounded(this, Ui.Surface);
        card.SetPadding(Ui.Dp(this, 16), Ui.Dp(this, 14), Ui.Dp(this, 16), Ui.Dp(this, 14));
        card.Clickable = true;

        var title = Ui.Label(this, name, 18f, Ui.Ink, bold: true);
        card.AddView(title);

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
        card.AddView(sub);

        var lp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        lp.BottomMargin = Ui.Dp(this, 10);
        _list.AddView(card, lp);

        var row = new Row(tag, name, native, phrase, bytes, card, sub);
        _rows.Add(row);
        card.Click += (s, e) => Speak(row);
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

        SetSub(row, "preparing…");
        try
        {
#if IT_VOICE_ANDROID
            var store = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "CircleAI", "Models");
            var wav = System.IO.Path.Combine(FilesDir!.AbsolutePath, $"say-{row.Tag}.wav");

            var report = await CircleAI.Samples.It.Voice.ItTtsProbe.RunCataloguedAsync(
                store, row.Tag, row.Phrase, wav,
                line => RunOnUiThread(() => SetSub(row, Summarise(line))), cts.Token);

            if (cts.IsCancellationRequested) return;

            if (System.IO.File.Exists(wav) && report.Contains("SYNTHESIS OK", StringComparison.Ordinal))
            {
                SetSub(row, $"“{row.Phrase}”");
                await MainActivity.PlayWavStaticAsync(wav);
                RestoreSub(row);
            }
            else
            {
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
            SetSub(row, $"“{row.Phrase}”");
#endif
        }
        catch (System.OperationCanceledException) { RestoreSub(row); }
        catch (Exception ex) { SetSub(row, ex.Message); }
    }

    /// <summary>Turn an engine log line into something worth showing a person.</summary>
    static string Summarise(string line)
    {
        line = line.Trim();
        if (line.Contains("%", StringComparison.Ordinal)) return "downloading… " + line;
        if (line.StartsWith("voice", StringComparison.OrdinalIgnoreCase)) return "found the voice";
        if (line.StartsWith("downloaded", StringComparison.OrdinalIgnoreCase)) return "downloaded — loading";
        if (line.StartsWith("engine", StringComparison.OrdinalIgnoreCase)) return "loading the voice";
        return "working…";
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
        base.OnDestroy();
    }
}
