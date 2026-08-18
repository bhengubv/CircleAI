#nullable enable

// JobSpecActivity.cs
//
// Aiming the same profile at one particular job.
//
// THIS IS THE PART THAT IS WORTH AN HOUR. Anybody can hold a CV; what changes
// somebody's odds is sending a different one to each employer — the security
// work first for the security job, the driving first for the driving job. Doing
// that by hand means keeping several documents that slowly disagree with each
// other. Doing it from one profile means every version is true.
//
// HOW SPECS ACTUALLY ARRIVE, which decides the intake. Job adverts reach people
// in this market on WhatsApp — forwarded text, or a photograph of a printed page
// on a shop window. So the share sheet is the primary route and this screen is
// registered to receive text from other apps; typing is the fallback, not the
// assumption.
//
// THE MODEL CHOOSES IDS, NOT WORDS. See ProfileTailoring: the model is asked
// which stored facts to lead with, and the document is rendered from those rows.
// It cannot write an employer onto a CV because it never writes onto the CV at
// all. "Best foot forward" is ordering and emphasis over things the person said,
// which is what an honest CV has always been.

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

namespace CircleAI.Samples.It.Mobile;

[Activity(Label = "Aim at a job", Exported = true)]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "text/plain")]
public class JobSpecActivity : Activity
{
    SqliteCareerStore _store = null!;
    EditText _spec = null!;
    Button   _go   = null!;
    TextView _out  = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActionBar?.Hide();
        _store = new SqliteCareerStore(System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "CircleAI", "career.db"));

        BuildUi();

        // Arrived by sharing an advert from WhatsApp — the common case, and the
        // one that must not require any typing.
        if (Intent?.Action == Intent.ActionSend &&
            Intent.GetStringExtra(Intent.ExtraText) is { Length: > 0 } shared)
        {
            _spec.Text = shared;
            _out.Text  = "Advert received. Tap to aim your CV at it.";
        }
    }

    void BuildUi()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Ui.Bg);
        var pad = Ui.Dp(this, 20);

        root.AddView(Ui.HomeBar(this, "Aim at a job"), Ui.Fill());

        var lead = Ui.Label(this,
            "Paste the advert, or share it here from WhatsApp. Your CV will be rearranged "
          + "to put the parts that match it first — using only what you already told me.",
            14f, Ui.InkSoft);
        lead.SetPadding(pad, Ui.Dp(this, 8), pad, Ui.Dp(this, 12));
        root.AddView(lead, Ui.Fill());

        _spec = new EditText(this) { Hint = "Paste the job advert here" };
        _spec.SetTextColor(Ui.Ink);
        _spec.SetHintTextColor(Ui.InkSoft);
        _spec.Background = Ui.Rounded(this, Ui.Surface, 10f);
        _spec.SetPadding(Ui.Dp(this, 14), Ui.Dp(this, 12), Ui.Dp(this, 14), Ui.Dp(this, 12));
        _spec.SetLines(6);
        _spec.Gravity = GravityFlags.Top;
        var specLp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        specLp.SetMargins(pad, 0, pad, 0);
        root.AddView(_spec, specLp);

        _go = Ui.Action(this, "Aim my CV at this job", primary: true);
        var goLp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        goLp.SetMargins(pad, Ui.Dp(this, 14), pad, 0);
        _go.Click += (_, _) => Tailor();
        root.AddView(_go, goLp);

        _out = Ui.Label(this, "", 14f, Ui.Ink);
        _out.SetPadding(pad, Ui.Dp(this, 18), pad, pad);
        var scroll = new ScrollView(this);
        scroll.AddView(_out);
        root.AddView(scroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        SetContentView(root);
    }

    /// <summary>Asks the model which facts to lead with, then shows the result.</summary>
    async void Tailor()
    {
        var text = _spec.Text?.Trim() ?? "";
        if (text.Length < 20) { _out.Text = "Paste a bit more of the advert."; return; }

        var profile = _store.Load();
        if (profile.History.Count == 0)
        {
            _out.Text = "Tell me about your work first — then I can aim it at a job.";
            return;
        }

        try
        {
            _go.Enabled = false;
            _go.Text = "Reading the advert…";

            // The title is the advert's first line unless it says otherwise —
            // enough to tell two applications apart in the list afterwards.
            var title = text.Split('\n').First().Trim();
            if (title.Length > 60) title = title[..60];

            var spec   = new JobSpec(title, null, text, Source: "typed");
            var specId = _store.AddSpec(spec);

            var prompt = ProfileTailoring.BuildPrompt(profile, spec with { Id = specId });

            // THE SHARED BRAIN, NOT A PRIVATE ONE. This used to build its own
            // session per tailoring and dispose it after — a full model load
            // and unload for every job advert, on the screen where somebody is
            // waiting to see their CV rearranged. The comment that used to sit
            // here called that deliberate, on the grounds that a resident
            // second copy would cost RAM; the answer to two copies is one copy,
            // not a copy loaded and thrown away each time.
            var brain = await ItSessionHost.GetAsync(this);
            var answer = await brain.RunTurnStreamingAsync(prompt, _ => { }, _ => { }, _ => { });

            var choice  = ProfileTailoring.Parse(answer, profile);
            var cv      = ProfileToCv.Render(profile,
                              ProfileTailoring.SelectedFacts(choice).ToHashSet());

            // WHAT CHANGED AND WHY, in the person's own interest. They are about
            // to put their name on it, so the reasoning is shown to them rather
            // than logged for us.
            var lines = new List<string>
            {
                choice.Reasoning,
                "",
                $"Leading with: {string.Join(", ", cv.Experience.Select(e => e.Title))}",
                $"Skills shown: {string.Join(", ", cv.Skills)}",
                "",
                "Nothing here was invented — every line came from what you told me.",
            };
            _out.Text = string.Join("\n", lines);
            _go.Text = "Aim at another job";
        }
        catch (Exception ex)
        {
            _out.Text = "Could not read that advert.";
            Android.Util.Log.Error("CircleAI.Career", "tailoring failed: " + ex);
            _go.Text = "Try again";
        }
        finally { _go.Enabled = true; }
    }

    protected override void OnDestroy()
    {
        _store.Dispose();
        base.OnDestroy();
    }
}
