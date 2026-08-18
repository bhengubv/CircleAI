#nullable enable

// CareerActivity.cs
//
// The hour spent building something, with the something visible the whole time.
//
// WHY THE CV IS ON SCREEN WHILE THE QUESTIONS ARE BEING ASKED. An interview that
// collects answers and produces a document at the end is data entry with a
// promise attached — the person has no evidence anything is happening until it is
// over, and on a slow link "over" is an hour away. Here every answer redraws the
// document underneath the question. A minute in there is a page with their name
// on it; five minutes in it is recognisably a CV. The progress bar stops being
// the thing they are watching.
//
// IT NEEDS NO MODEL, which is the reason it can start two minutes after install
// while 22 GB is still arriving. Laying out known facts is a template and some
// arithmetic. The brain is needed later and for something else — choosing which
// facts to lead with for a particular job — and by then it has landed.
//
// PREVIEW NATIVELY, PDF ONLY ON APPROVAL. Rendering a PDF per keystroke on a P30
// would make every answer cost a second of layout. The preview is Android views
// built from the same CvDocument the renderer takes, so what somebody sees is
// what gets printed, without paying for print on every answer.
//
// TYPED AND SPOKEN BOTH, and neither is the fallback. Descriptions are easier
// said out loud; a surname, a phone number and a place are safer typed, because
// whisper mishears exactly those and a CV with the wrong number cannot be
// answered. The script marks which is which (InterviewQuestion.Verify) and this
// screen follows it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using CircleAI.Career;
using CircleAI.Documents;

namespace CircleAI.Samples.It.Mobile;

[Activity(Label = "Your CV", Exported = false)]
public class CareerActivity : Activity
{
    SqliteCareerStore _store = null!;
    CareerProfile     _profile = CareerProfile.Empty;
    InterviewQuestion? _current;

    TextView    _question = null!;
    TextView    _why      = null!;
    EditText    _answer   = null!;
    Button      _next     = null!;
    Button      _speak    = null!;
    TextView    _progress = null!;
    LinearLayout _preview = null!;

    /// <summary>Where the career record lives. App-private, never synced.</summary>
    static string StorePath => System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "CircleAI", "career.db");

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActionBar?.Hide();

        _store   = new SqliteCareerStore(StorePath);
        _profile = _store.Load();

        BuildUi();
        AskNext();
    }

    void BuildUi()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Ui.Bg);
        var pad = Ui.Dp(this, 20);

        root.AddView(Ui.HomeBar(this, "Your CV"), Ui.Fill());

        // ── the question ─────────────────────────────────────────────────
        var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
        card.Background = Ui.Rounded(this, Ui.Surface, 14f);
        card.SetPadding(pad, Ui.Dp(this, 18), pad, Ui.Dp(this, 18));
        var cardLp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        cardLp.SetMargins(pad, Ui.Dp(this, 12), pad, 0);

        _question = Ui.Label(this, "", 18f, Ui.Ink, bold: true);
        _why      = Ui.Label(this, "", 13.5f, Ui.InkSoft);
        _why.SetPadding(0, Ui.Dp(this, 6), 0, 0);

        _answer = new EditText(this) { Hint = "Say it or type it" };
        _answer.SetTextColor(Ui.Ink);
        _answer.SetHintTextColor(Ui.InkSoft);
        _answer.Background = Ui.Rounded(this, Ui.Bg, 10f);
        _answer.SetPadding(Ui.Dp(this, 14), Ui.Dp(this, 12), Ui.Dp(this, 14), Ui.Dp(this, 12));
        var ansLp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        ansLp.TopMargin = Ui.Dp(this, 14);

        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetPadding(0, Ui.Dp(this, 12), 0, 0);

        _speak = Ui.Action(this, "Say it", primary: false);
        _next  = Ui.Action(this, "Next", primary: true);
        var half = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        var halfGap = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        halfGap.LeftMargin = Ui.Dp(this, 10);

        _speak.Click += (_, _) => SpeakAnswer();
        _next.Click  += (_, _) => Accept();

        row.AddView(_speak, half);
        row.AddView(_next, halfGap);

        card.AddView(_question, Ui.Fill());
        card.AddView(_why, Ui.Fill());
        card.AddView(_answer, ansLp);
        card.AddView(row, Ui.Fill());
        root.AddView(card, cardLp);

        // ── how far along ────────────────────────────────────────────────
        _progress = Ui.Label(this, "", 13f, Ui.Blue);
        _progress.Gravity = GravityFlags.Center;
        _progress.SetPadding(pad, Ui.Dp(this, 14), pad, Ui.Dp(this, 6));
        root.AddView(_progress, Ui.Fill());

        // ── the document, growing ────────────────────────────────────────
        var scroll = new ScrollView(this);
        _preview = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _preview.Background = Ui.Rounded(this, Ui.White, 8f);
        _preview.SetPadding(pad, pad, pad, pad);
        var pvLp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        pvLp.SetMargins(pad, 0, pad, pad);
        scroll.AddView(_preview, pvLp);
        root.AddView(scroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        SetContentView(root);
        Redraw();
    }

    // ── the interview ────────────────────────────────────────────────────────

    void AskNext()
    {
        _current = CareerInterview.Next(_profile);

        if (_current is null)
        {
            // OUT OF QUESTIONS IS NOT THE END OF THE SCREEN. The document is the
            // point, so the screen stays on it and offers what comes next.
            _question.Text = "That is everything I need";
            _why.Text      = "Your CV is below. You can change anything, or aim it at a job.";
            _answer.Visibility = ViewStates.Gone;
            _speak.Text = "Aim it at a job";
            _next.Text  = "Save my CV";
            return;
        }

        _question.Text = _current.Ask;
        _why.Text      = _current.Why;
        _answer.Text   = "";
        _answer.Visibility = ViewStates.Visible;

        // The script says which answers must be read back before they are kept.
        // A mis-heard surname is worse than a blank one.
        _speak.Text = _current.Verify ? "Say it (I will check)" : "Say it";
    }

    /// <summary>Takes the typed or spoken answer and stores it.</summary>
    void Accept()
    {
        if (_current is null) { SaveAndFinish(); return; }

        var text = _answer.Text?.Trim() ?? "";
        var q    = _current;

        // "No" is a real answer to several of these — no certificate, no formal
        // employer, no schooling finished. A script that will not take it traps
        // somebody on a question they cannot answer.
        if (!CareerInterview.IsDecline(text)) Store(q.Field, text);
        else if (q.Field is ProfileField.WorkRole) { AskNext(); Redraw(); return; }

        _profile = _store.Load();
        AskNext();
        Redraw();
    }

    /// <summary>Writes one answer into the right table.</summary>
    void Store(ProfileField field, string text)
    {
        var id = _profile.Identity;

        switch (field)
        {
            case ProfileField.FullName:
                _store.SaveIdentity(id with { FullName = text }); break;
            case ProfileField.Phone:
                _store.SaveIdentity(id with { Phone = text }); break;
            case ProfileField.Headline:
                _store.SaveIdentity(id with { Headline = text }); break;
            case ProfileField.Location:
                _store.SaveIdentity(id with { Location = text }); break;
            case ProfileField.Summary:
                _store.SaveIdentity(id with { Summary = text }); break;

            case ProfileField.WorkRole:
                _store.AddHistory(new ProfileHistory(text)); break;

            case ProfileField.WorkOrganisation:
                ReplaceLatestHistory(h => h with { Organisation = text, Formal = true }); break;

            case ProfileField.WorkWhen:
                // Kept as they said it. "About two years, until last winter" is
                // more honest on a CV than a date this app invented.
                ReplaceLatestHistory(h => h with { Start = text }); break;

            case ProfileField.WorkDid:
                ReplaceLatestHistory(h => h with
                {
                    Achievements = SplitList(text)
                });
                break;

            case ProfileField.Skills:
                foreach (var s in SplitList(text)) _store.AddSkill(new ProfileSkill(s));
                break;

            case ProfileField.Certification:
                foreach (var c in SplitList(text)) _store.AddCertification(new ProfileCertification(c));
                break;

            case ProfileField.Education:
                _store.AddEducation(new ProfileEducation(text)); break;

            case ProfileField.Languages:
                foreach (var l in SplitList(text)) _store.AddLanguage(new ProfileLanguage(l));
                break;
        }
    }

    /// <summary>
    /// Rewrites the newest job. The work questions build one row across four
    /// answers, so each has to update rather than insert.
    /// </summary>
    void ReplaceLatestHistory(Func<ProfileHistory, ProfileHistory> edit)
    {
        var latest = _store.Load().History.FirstOrDefault();
        if (latest is null) { _store.AddHistory(edit(new ProfileHistory("Work"))); return; }

        _store.Remove("history", latest.Id);
        _store.AddHistory(edit(latest));
    }

    /// <summary>
    /// Splits a spoken list into items.
    /// </summary>
    /// <remarks>
    /// People say lists as "forklift, cash handling and dealing with customers",
    /// so commas and "and" both separate. Kept deliberately simple: over-splitting
    /// produces two short skills, which is recoverable, while under-splitting
    /// produces one absurd one, which looks like the app did not understand.
    /// </remarks>
    static IReadOnlyList<string> SplitList(string text) =>
        text.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(p => p.Split(" and ", StringSplitOptions.RemoveEmptyEntries))
            .Select(p => p.Trim())
            .Where(p => p.Length > 1)
            .ToList();

    // ── the document ─────────────────────────────────────────────────────────

    /// <summary>Redraws the CV from what is known now.</summary>
    void Redraw()
    {
        _profile = _store.Load();
        var cv = ProfileToCv.Render(_profile);

        _progress.Text = $"Your CV is {_profile.Completeness() * 100:0}% there";

        _preview.RemoveAllViews();
        AddLine(cv.FullName, 20f, bold: true, dark: true);
        if (!string.IsNullOrWhiteSpace(cv.Headline)) AddLine(cv.Headline, 14f, dark: true);

        var contact = new[] { cv.Contact.Phone, cv.Contact.Email, cv.Contact.Location }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        if (contact.Any()) AddLine(string.Join("  ·  ", contact), 12.5f, dark: true);

        if (!string.IsNullOrWhiteSpace(cv.Summary)) { Gap(); AddLine(cv.Summary!, 13f, dark: true); }

        if (cv.Experience.Count > 0)
        {
            Gap(); AddLine("WORK", 12f, bold: true, dark: true);
            foreach (var e in cv.Experience)
            {
                var where = string.IsNullOrWhiteSpace(e.Organisation) ? "" : $" — {e.Organisation}";
                var when  = string.IsNullOrWhiteSpace(e.StartDate) ? "" : $"  ({e.StartDate}{(e.EndDate is null ? " – present" : " – " + e.EndDate)})";
                AddLine(e.Title + where + when, 13f, bold: true, dark: true);
                foreach (var h in e.Highlights) AddLine("•  " + h, 12.5f, dark: true);
            }
        }

        if (cv.Skills.Count > 0)
        {
            Gap(); AddLine("SKILLS", 12f, bold: true, dark: true);
            AddLine(string.Join("  ·  ", cv.Skills), 12.5f, dark: true);
        }

        if (cv.Certifications is { Count: > 0 })
        {
            Gap(); AddLine("CERTIFICATES", 12f, bold: true, dark: true);
            foreach (var c in cv.Certifications) AddLine("•  " + c.Name, 12.5f, dark: true);
        }

        if (cv.Education.Count > 0)
        {
            Gap(); AddLine("EDUCATION", 12f, bold: true, dark: true);
            foreach (var e in cv.Education)
                AddLine($"{e.Qualification}{(string.IsNullOrWhiteSpace(e.Institution) ? "" : " — " + e.Institution)}", 12.5f, dark: true);
        }

        if (cv.Languages is { Count: > 0 })
        {
            Gap(); AddLine("LANGUAGES", 12f, bold: true, dark: true);
            AddLine(string.Join("  ·  ", cv.Languages), 12.5f, dark: true);
        }
    }

    /// <summary>Ink on the white CV page. Brand navy — the page is not a terminal.</summary>
    static readonly Android.Graphics.Color PageInk = Android.Graphics.Color.ParseColor("#2c3e50");

    void AddLine(string text, float size, bool bold = false, bool dark = false)
    {
        var tv = Ui.Label(this, text, size, dark ? PageInk : Ui.Ink, bold);
        _preview.AddView(tv, Ui.Fill());
    }

    void Gap()
    {
        var v = new View(this);
        _preview.AddView(v, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Ui.Dp(this, 10)));
    }

    // ── voice ────────────────────────────────────────────────────────────────

    /// <summary>Answers the current question by speaking.</summary>
    async void SpeakAnswer()
    {
        if (_current is null) { AimAtJob(); return; }

#if IT_VOICE_ANDROID
        try
        {
            _speak.Enabled = false;
            _speak.Text = "Listening…";

            var store = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "CircleAI", "Models");

            var turn = new VoiceTurn();
            await using var mic = new AndroidAudioCapture();
            var audio = await turn.ListenAsync(mic);

            if (audio.Length == 0) { _speak.Text = "I did not catch that"; return; }

            var (listener, status) = await CircleAI.Samples.It.Voice.ItListener
                .TryCreateAsync(store, _ => { });
            if (listener is null) { _speak.Text = status; return; }
            await using var ears = listener;

            var heard = (await ears.Transcriber.TranscribeAsync(audio)).Text;

            // PUT IT IN THE BOX, DO NOT COMMIT IT. Especially for the fields the
            // script marks Verify — the person reads it back and fixes it before
            // it becomes their phone number.
            _answer.Text = heard?.Trim() ?? "";
            _speak.Text = _current.Verify ? "Check it, then Next" : "Say it again";
        }
        catch (Exception ex)
        {
            _speak.Text = "Could not listen";
            Android.Util.Log.Error("CircleAI.Career", "voice answer failed: " + ex);
        }
        finally { _speak.Enabled = true; }
#else
        _speak.Text = "Type it instead";
        await Task.CompletedTask;
#endif
    }

    // ── finishing ────────────────────────────────────────────────────────────

    /// <summary>Renders the PDF, stores it, and hands it to the person.</summary>
    async void SaveAndFinish()
    {
        try
        {
            _next.Enabled = false;
            _next.Text = "Making your CV…";

            var cv = ProfileToCv.Render(_profile);
            IDocumentEngine engine = new PdfSharpDocumentEngine();
            var result = await engine.RenderAsync(new DocumentRequest(DocumentKind.Cv, cv));
            var pdf = result.Bytes;

            _store.Approve(specId: null, pdf, Array.Empty<long>());

            // Written where the share sheet can reach it. A CV that can only be
            // seen inside our app is a demo, not a document.
            var outPath = System.IO.Path.Combine(
                GetExternalFilesDir(null)!.AbsolutePath,
                $"CV-{_profile.Identity.FullName.Replace(' ', '-')}.pdf");
            System.IO.File.WriteAllBytes(outPath, pdf);

            _next.Text = "Saved";
            _progress.Text = "Saved to your phone — you can send it from here";
            Share(outPath);
        }
        catch (Exception ex)
        {
            _next.Text = "Could not save";
            Android.Util.Log.Error("CircleAI.Career", "save failed: " + ex);
        }
        finally { _next.Enabled = true; }
    }

    void Share(string path)
    {
        try
        {
            var uri = Android.Net.Uri.FromFile(new Java.IO.File(path));
            var send = new Intent(Intent.ActionSend);
            send.SetType("application/pdf");
            send.PutExtra(Intent.ExtraStream, uri);
            send.AddFlags(ActivityFlags.GrantReadUriPermission);
            StartActivity(Intent.CreateChooser(send, "Send your CV"));
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("CircleAI.Career", "share failed: " + ex);
        }
    }

    /// <summary>Opens the job-spec screen, where the model does the tailoring.</summary>
    void AimAtJob() => StartActivity(new Intent(this, typeof(JobSpecActivity)));

    protected override void OnDestroy()
    {
        _store.Dispose();
        base.OnDestroy();
    }
}
