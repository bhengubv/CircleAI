#nullable enable

// SearchActivity.cs
//
// Finding something you said, or something somebody said to you.
//
// THE INDEX WAS BUILT AND HAD NO CALLER, which is the defect this whole register
// exists to catch - and I committed it once before wiring this. LexicalIndex and
// Bm25 ranked text correctly and nothing asked them anything.
//
// BUT THE REAL BLOCKER WAS NOT THE RANKING. It was that there was nothing to
// search: a transcript was shown on a screen, optionally written out as an .srt
// to a location the person chose, and then discarded. "Search across everything"
// meant "search memory", which already has a better search of its own. The
// transcript store is what made this screen worth having.
//
// TWO STORES, TWO SEARCHES, ONE LIST. Memory goes through its own recall, which
// knows about corrections, staleness and wear; transcripts go through BM25,
// which needs no model and so works on a handset with nothing downloaded. The
// rows say which is which, because "you told me this" and "a microphone caught
// this" are different kinds of answer and a person should be able to tell.

using System;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using CircleAI.Assistant;

using Cancelled = System.OperationCanceledException;

namespace CircleAI.Assistant.Device;

[Activity(Label = "Search", Exported = false)]
public class SearchActivity : Activity
{
    const string Tag = "CircleAI.Search";

    EditText _query = null!;
    TextView _status = null!;
    LinearLayout _results = null!;
    Button _go = null!;

    CancellationTokenSource? _run;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActionBar?.Hide();

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Ui.Bg);
        root.AddView(Ui.HomeBar(this, "Search"), Ui.Fill());

        var pad = Ui.Dp(this, 16);
        var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        body.SetPadding(pad, pad, pad, pad);

        _query = new EditText(this)
        {
            Hint = "What are you looking for?",
            TextSize = 16f,
        };
        _query.SetTextColor(Ui.Ink);
        _query.SetHintTextColor(Ui.InkSoft);
        _query.Background = Ui.Rounded(this, Ui.Raised);
        _query.SetPadding(pad, Ui.Dp(this, 12), pad, Ui.Dp(this, 12));
        _query.SetSingleLine(true);
        _query.ImeOptions = Android.Views.InputMethods.ImeAction.Search;
        _query.EditorAction += (_, e) =>
        {
            if (e.ActionId != Android.Views.InputMethods.ImeAction.Search) return;
            e.Handled = true;
            Look();
        };
        body.AddView(_query, Ui.Fill());

        _go = Ui.Action(this, "Search", primary: true);
        _go.Click += (_, _) => Look();
        var gParams = Ui.Fill();
        gParams.TopMargin = Ui.Dp(this, 10);
        body.AddView(_go, gParams);

        _status = Ui.Label(this,
            "Looks through what you have told it and what it has written down. " +
            "Everything stays on this phone.",
            12.5f, Ui.InkSoft);
        var sParams = Ui.Fill();
        sParams.TopMargin = Ui.Dp(this, 12);
        body.AddView(_status, sParams);

        _results = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var rParams = Ui.Fill();
        rParams.TopMargin = Ui.Dp(this, 14);
        body.AddView(_results, rParams);

        var scroll = new ScrollView(this);
        scroll.AddView(body);
        root.AddView(scroll, Ui.Fill(1f));

        SetContentView(root);
    }

    void Look()
    {
        var asked = _query.Text?.Trim();
        if (string.IsNullOrWhiteSpace(asked)) return;

        _go.Enabled = false;
        _results.RemoveAllViews();
        _status.Text = "Looking...";

        _run?.Cancel();
        _run = new CancellationTokenSource();
        var ct = _run.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                // NO SESSION. Searching needs the transcript store and the
                // memory, both of which are process-wide and already wired by
                // CircleAIApplication - it does not need a model, a device probe
                // or a catalogue refresh, and asking for a session got all three.
                // This screen hung for ninety seconds on a P30 doing exactly that.
                var found = await CircleAISession
                    .SearchAsync(asked!, ct: ct).ConfigureAwait(false);

                RunOnUiThread(() =>
                {
                    _results.RemoveAllViews();

                    if (found.Count == 0)
                    {
                        // NOT "NO RESULTS". A person who has never recorded
                        // anything and never told it anything has an EMPTY
                        // phone, not a failed search, and those want different
                        // sentences - one is "try other words", the other is
                        // "there is nothing here yet".
                        _status.Text = "Nothing matched. Transcripts you keep and things you tell it show up here.";
                        return;
                    }

                    _status.Text = $"{found.Count} result{(found.Count == 1 ? "" : "s")}.";
                    foreach (var one in found) _results.AddView(Row(one), Ui.Fill());
                });
            }
            catch (Cancelled) when (ct.IsCancellationRequested)
            {
                // Another search replaced this one, or the screen went away.
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"search failed: {ex}");
                RunOnUiThread(() => _status.Text = $"That did not work: {ex.Message}");
            }
            finally
            {
                RunOnUiThread(() => _go.Enabled = true);
            }
        });
    }

    /// <summary>One result, saying where it came from.</summary>
    /// <remarks>
    /// THE SOURCE IS ON THE ROW ON PURPOSE. "You told me this" and "a microphone
    /// caught this in a meeting" are different kinds of answer, and a list that
    /// renders them identically invites somebody to trust the second as much as
    /// the first.
    /// </remarks>
    View Row(Found one)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Vertical };
        row.SetPadding(Ui.Dp(this, 14), Ui.Dp(this, 12), Ui.Dp(this, 14), Ui.Dp(this, 12));
        row.SetBackgroundColor(Ui.Surface);

        var heading = one.Kind == "memory"
            ? "Something you told it"
            : one.Title;

        row.AddView(Ui.Label(this, heading, 12.5f, Ui.Blue, bold: true), Ui.Fill());

        var text = Ui.Label(this, one.Text, 15f, Ui.Ink);
        text.SetPadding(0, Ui.Dp(this, 4), 0, 0);
        row.AddView(text, Ui.Fill());

        if (one.When is { } when)
        {
            var stamp = Ui.Label(this, when.ToLocalTime().ToString("d MMM yyyy"), 11.5f, Ui.InkSoft);
            stamp.SetPadding(0, Ui.Dp(this, 4), 0, 0);
            row.AddView(stamp, Ui.Fill());
        }

        var wrap = Ui.Fill();
        wrap.BottomMargin = Ui.Dp(this, 8);
        row.LayoutParameters = wrap;
        return row;
    }

    protected override void OnDestroy()
    {
        _run?.Cancel();
        _run?.Dispose();
        _run = null;
        base.OnDestroy();
    }
}
