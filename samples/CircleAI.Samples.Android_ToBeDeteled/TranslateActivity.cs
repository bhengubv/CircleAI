#nullable enable

// TranslateActivity.cs
//
// Standing between two people who do not share a language.
//
// THE LAST OF THE FOUR DEAD ROWS IN THIS HEAD. Seeing, Listening and Music each
// had a capability built and no screen; translation was worse, because the
// capability was not merely unreachable - the engine had ZERO consumers anywhere
// while the web head built its own prompt against the raw brain. That has since
// been made one owner, and this is the door on the side that had none: the
// native head holds a CircleAISession, not an IConversation, so until
// CircleAISession.TranslateAsync existed there was nothing here to call.
//
// TWO HALVES, NOT A FORM, and the top one is upside down. The phone is meant to
// be put on the table between two people rather than held by one, so the other
// person's half faces them. That is the whole reason this is a screen of its own
// rather than a text box with a language dropdown.
//
// NOTHING IS ANSWERED. The circle elsewhere replies; here it only carries. A
// model that answers "where is the toilet" with directions instead of the words
// to say has failed at the one job this screen exists for - which is why the
// instruction lives in the engine and not here.

using System;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using System.Linq;
using CircleAI.Assistant;

using Cancelled = System.OperationCanceledException;

namespace CircleAI.Assistant.Device;

[Activity(Label = "Translate", Exported = false)]
public class TranslateActivity : Activity
{
    const string Tag = "CircleAI.Translate";

    /// <summary>Which half is speaking.</summary>
    enum Side { Theirs, Mine }

    EditText _input = null!;
    TextView _output = null!;
    TextView _status = null!;
    Button _translate = null!;
    Button _theirLanguage = null!;
    Button _myLanguage = null!;
    Button _swap = null!;

    string _from = "en";
    string _to = "zu";
    CancellationTokenSource? _turn;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActionBar?.Hide();

        // The phone's own language is the one the owner reads, so it is the
        // half nearest them. The other half is a guess until they change it.
        _from = SpokenLanguage.Current(this) ?? "en";
        _to = _from.StartsWith("zu", StringComparison.OrdinalIgnoreCase) ? "en" : "zu";

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Ui.Bg);
        root.AddView(Ui.HomeBar(this, "Translate"), Ui.Fill());

        var pad = Ui.Dp(this, 16);
        var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        body.SetPadding(pad, pad, pad, pad);

        // ── Theirs, rotated to face across the table ────────────────────
        _output = Ui.Label(this, "", 19f, Ui.Ink);
        _output.Rotation = 180f;
        _output.SetMinimumHeight(Ui.Dp(this, 120));
        _output.SetBackgroundColor(Ui.Surface);
        _output.SetPadding(pad, pad, pad, pad);
        _output.SetTextIsSelectable(true);
        body.AddView(_output, Ui.Fill());

        _theirLanguage = Ui.Action(this, Name(_to), primary: false);
        _theirLanguage.Rotation = 180f;
        _theirLanguage.Click += (_, _) => Pick(Side.Theirs);
        var tParams = Ui.Fill();
        tParams.TopMargin = Ui.Dp(this, 8);
        body.AddView(_theirLanguage, tParams);

        // ── The seam ────────────────────────────────────────────────────
        _swap = Ui.Action(this, "⇄  Swap sides", primary: false);
        _swap.Click += (_, _) => Swap();
        var sParams = Ui.Fill();
        sParams.TopMargin = Ui.Dp(this, 18);
        sParams.BottomMargin = Ui.Dp(this, 18);
        body.AddView(_swap, sParams);

        _status = Ui.Label(this, "Type what you want to say.", 12.5f, Ui.InkSoft);
        body.AddView(_status, Ui.Fill());

        // ── Yours ───────────────────────────────────────────────────────
        _input = new EditText(this) { Hint = "What you want to say", TextSize = 17f };
        _input.SetTextColor(Ui.Ink);
        _input.SetHintTextColor(Ui.InkSoft);
        _input.Background = Ui.Rounded(this, Ui.Raised);
        _input.SetPadding(pad, Ui.Dp(this, 12), pad, Ui.Dp(this, 12));
        _input.SetMinimumHeight(Ui.Dp(this, 96));
        var iParams = Ui.Fill();
        iParams.TopMargin = Ui.Dp(this, 8);
        body.AddView(_input, iParams);

        _myLanguage = Ui.Action(this, Name(_from), primary: false);
        _myLanguage.Click += (_, _) => Pick(Side.Mine);
        var mParams = Ui.Fill();
        mParams.TopMargin = Ui.Dp(this, 8);
        body.AddView(_myLanguage, mParams);

        _translate = Ui.Action(this, "Translate", primary: true);
        _translate.Click += (_, _) => Translate();
        var trParams = Ui.Fill();
        trParams.TopMargin = Ui.Dp(this, 10);
        body.AddView(_translate, trParams);

        var scroll = new ScrollView(this);
        scroll.AddView(body);
        root.AddView(scroll, Ui.Fill(1f));

        SetContentView(root);
    }

    static string Name(string tag) => SampleLanguages.Find(tag)?.Name ?? tag;

    void Swap()
    {
        (_from, _to) = (_to, _from);
        _myLanguage.Text = Name(_from);
        _theirLanguage.Text = Name(_to);
        _status.Text = $"{Name(_from)} to {Name(_to)}.";
    }

    /// <summary>The languages on offer, named, in one place.</summary>
    /// <remarks>
    /// READ FROM SampleLanguages, NOT TYPED HERE. This is a second PRESENTATION
    /// of the list and must never become a second COPY of it - "which languages
    /// are offered" already had two owners this week and the disagreement cost
    /// six voices nobody could reach.
    /// <para>
    /// A dialog rather than LanguagePickerActivity, because that screen SETS the
    /// app's language rather than returning a choice: sending somebody there to
    /// pick the other person's half would change the language of the whole app.
    /// </para>
    /// </remarks>
    static readonly SampleLanguage[] Offered =
        [.. SampleLanguages.All.Values.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)];

    void Pick(Side side)
    {
        var names = Offered.Select(l => l.Name).ToArray();

        new AlertDialog.Builder(this)
            .SetTitle(side == Side.Mine ? "Your language" : "Their language")!
            .SetItems(names, (_, e) =>
            {
                var chosen = Offered[e.Which].Tag;

                if (side == Side.Mine) { _from = chosen; _myLanguage.Text = Name(_from); }
                else                   { _to = chosen;   _theirLanguage.Text = Name(_to); }

                _status.Text = $"{Name(_from)} to {Name(_to)}.";
            })!
            .Show();
    }

    void Translate()
    {
        var said = _input.Text?.Trim();
        if (string.IsNullOrWhiteSpace(said)) return;

        if (string.Equals(_from, _to, StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = "Both sides are set to the same language.";
            return;
        }

        _translate.Enabled = false;
        _status.Text = $"{Name(_from)} to {Name(_to)}...";

        _turn?.Cancel();
        _turn = new CancellationTokenSource();
        var ct = _turn.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                var session = await CircleAISessionHost.GetAsync(this).ConfigureAwait(false);
                var rendered = await session.TranslateAsync(said!, _from, _to, ct).ConfigureAwait(false);

                RunOnUiThread(() =>
                {
                    _output.Text = string.IsNullOrWhiteSpace(rendered)
                        ? "Nothing came back."
                        : rendered.Trim();
                    _status.Text = $"{Name(_from)} to {Name(_to)}.";
                });
            }
            catch (Cancelled) when (ct.IsCancellationRequested)
            {
                // Another sentence replaced this one, or the screen went away.
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"translation failed: {ex}");
                RunOnUiThread(() => _status.Text = $"That did not work: {ex.Message}");
            }
            finally
            {
                RunOnUiThread(() => _translate.Enabled = true);
            }
        });
    }

    protected override void OnDestroy()
    {
        _turn?.Cancel();
        _turn?.Dispose();
        _turn = null;
        base.OnDestroy();
    }
}
