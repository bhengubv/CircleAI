#nullable enable

// SeeingActivity.cs
//
// Show it a picture and it tells you what is in it.
//
// SEEING WAS A DEAD ROW. The catalogue had two vision models with every file and
// hash pinned, the bridge could encode an image, CircleAISession.RunImageTurnAsync
// was complete and DeviceBrain.SeeAsync called it - and the only way to reach any
// of it was the web head. On the phone the abilities screen offered to download
// 311 MB for "Looks at a photo and tells you what is in it", and then there was
// nowhere to take a photo to. An ability is code that runs, and this is the code.
//
// THE PICTURE IS SHRUNK BEFORE THE MODEL SEES IT, and that is not a nicety. A
// phone camera produces twelve megapixels; the encoder turns area into vision
// tokens and prefills every one before the first word comes back. Handing over
// the original is minutes of frozen phone or an out-of-memory kill.
// ImageBudget says how far to subsample and BitmapFactory does it DURING the
// decode, so the full-size bitmap is never allocated at all - which matters on a
// device that has 1,4 GB to spend and has already spent most of it on the model.
//
// NO PERMISSION IS ASKED FOR. ActionGetContent hands back a URI the system has
// already granted; asking for storage access would be a dialog for something
// the picker gives for free, on a phone where every extra permission prompt is a
// reason to stop.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using CircleAI.Inference;

// Android.OS ships its own OperationCanceledException and it is NOT the one a
// CancellationToken throws. Aliased rather than fully-qualified, which is how
// WakeWordActivity handles the same three-way clash in this head.
using Cancelled = System.OperationCanceledException;

namespace CircleAI.Assistant.Device;

[Activity(Label = "Seeing", Exported = false)]
public class SeeingActivity : Activity
{
    const string Tag = "CircleAI.Seeing";

    /// <summary>Request code for the photo picker.</summary>
    const int PickPhoto = 4711;

    /// <summary>
    /// What it is asked when the person does not say.
    /// </summary>
    /// <remarks>
    /// A QUESTION, NOT AN INSTRUCTION TO DESCRIBE. "Describe this image" invites
    /// a small model to produce a paragraph of hedging; asking what is in it gets
    /// a list of things, which is what a person opening this screen wants and
    /// what a 256M model can actually deliver.
    /// </remarks>
    const string DefaultQuestion = "What is in this picture?";

    ImageView _preview = null!;
    EditText  _question = null!;
    TextView  _answer = null!;
    Button    _pick = null!;
    Button    _ask = null!;

    byte[]? _image;
    CancellationTokenSource? _turn;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActionBar?.Hide();

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Ui.Bg);
        root.AddView(Ui.HomeBar(this, "Seeing"), Ui.Fill());

        var pad = Ui.Dp(this, 16);

        var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        body.SetPadding(pad, pad, pad, pad);

        _preview = new ImageView(this);
        _preview.SetBackgroundColor(Ui.Surface);
        _preview.SetMinimumHeight(Ui.Dp(this, 200));
        _preview.SetScaleType(ImageView.ScaleType.CenterInside);
        body.AddView(_preview, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Ui.Dp(this, 220)));

        _pick = Ui.Action(this, "Choose a photo", primary: true);
        _pick.Click += (_, _) => Pick();
        var pickParams = Ui.Fill();
        pickParams.TopMargin = Ui.Dp(this, 14);
        body.AddView(_pick, pickParams);

        _question = new EditText(this)
        {
            Hint = DefaultQuestion,
            TextSize = 15f,
        };
        _question.SetTextColor(Ui.Ink);
        _question.SetHintTextColor(Ui.InkSoft);
        _question.Background = Ui.Rounded(this, Ui.Raised);
        _question.SetPadding(pad, Ui.Dp(this, 12), pad, Ui.Dp(this, 12));
        var qParams = Ui.Fill();
        qParams.TopMargin = Ui.Dp(this, 10);
        body.AddView(_question, qParams);

        _ask = Ui.Action(this, "Ask", primary: false);
        _ask.Enabled = false;
        _ask.Click += (_, _) => Ask();
        var askParams = Ui.Fill();
        askParams.TopMargin = Ui.Dp(this, 10);
        body.AddView(_ask, askParams);

        _answer = Ui.Label(this, "Pick a photo to start.", 15f, Ui.InkSoft);
        var aParams = Ui.Fill();
        aParams.TopMargin = Ui.Dp(this, 18);
        body.AddView(_answer, aParams);

        var scroll = new ScrollView(this);
        scroll.AddView(body);
        root.AddView(scroll, Ui.Fill(1f));

        SetContentView(root);
    }

    void Pick()
    {
        // ActionGetContent rather than ActionPick: it returns a URI this app is
        // granted for the life of the result, needs no storage permission, and
        // is served by whatever gallery the phone has rather than by a Google one.
        var intent = new Intent(Intent.ActionGetContent);
        intent.SetType("image/*");
        StartActivityForResult(
            Intent.CreateChooser(intent, "Choose a photo"), PickPhoto);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != PickPhoto || resultCode != Result.Ok || data?.Data is null) return;

        try
        {
            _image = ReadWithinBudget(data.Data);
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"could not read the picked image: {ex}");
            _answer.Text = "That picture could not be opened.";
            return;
        }

        if (_image is null || _image.Length == 0)
        {
            _answer.Text = "That picture came back empty.";
            return;
        }

        var bmp = BitmapFactory.DecodeByteArray(_image, 0, _image.Length);
        if (bmp is not null) _preview.SetImageBitmap(bmp);

        _ask.Enabled = true;
        _answer.Text = "Ready. Ask it something, or just tap Ask.";
    }

    /// <summary>
    /// Read the picked image, subsampling on the way in so a twelve-megapixel
    /// photo never exists in memory at full size.
    /// </summary>
    /// <remarks>
    /// TWO PASSES OVER THE STREAM, NOT ONE. The first decodes bounds only
    /// (<c>InJustDecodeBounds</c>) and allocates nothing; the second decodes for
    /// real at the factor ImageBudget worked out. A content URI stream is not
    /// seekable, so it is opened twice rather than rewound.
    /// <para>
    /// Re-encoded as JPEG at 90 because the bridge takes encoded bytes and
    /// sniffs the format. Ninety rather than a hundred: the difference is
    /// invisible to a vision encoder that is about to resize it again, and the
    /// bytes crossing into native are a third of the size.
    /// </para>
    /// </remarks>
    byte[]? ReadWithinBudget(Android.Net.Uri uri)
    {
        var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        using (var probe = ContentResolver?.OpenInputStream(uri))
        {
            if (probe is null) return null;
            BitmapFactory.DecodeStream(probe, null, bounds);
        }

        var sample = ImageBudget.SampleFor(bounds.OutWidth, bounds.OutHeight);
        Log.Info(Tag,
            $"picked {bounds.OutWidth}x{bounds.OutHeight} -> 1/{sample} " +
            $"(budget {ImageBudget.MaxEdge})");

        var options = new BitmapFactory.Options { InSampleSize = sample };
        Bitmap? bitmap;
        using (var stream = ContentResolver?.OpenInputStream(uri))
        {
            if (stream is null) return null;
            bitmap = BitmapFactory.DecodeStream(stream, null, options);
        }

        if (bitmap is null) return null;

        try
        {
            using var ms = new MemoryStream();
            bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 90, ms);
            return ms.ToArray();
        }
        finally
        {
            bitmap.Recycle();
            bitmap.Dispose();
        }
    }

    void Ask()
    {
        if (_image is null) return;

        var question = string.IsNullOrWhiteSpace(_question.Text)
            ? DefaultQuestion
            : _question.Text!.Trim();

        _ask.Enabled = false;
        _pick.Enabled = false;
        _answer.Text = "Looking...";

        _turn?.Cancel();
        _turn = new CancellationTokenSource();
        var ct = _turn.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                var session = await CircleAISessionHost.GetAsync(this).ConfigureAwait(false);

                var said = new System.Text.StringBuilder();
                await session.RunImageTurnAsync(
                    question,
                    _image,
                    // Diagnostics to logcat, the reply to the screen. Wired the
                    // other way round, the concierge's routing line lands in the
                    // answer and the answer goes nowhere - the exact slip
                    // DeviceBrain.AskAsync carries a comment about.
                    line => Log.Info(Tag, line),
                    fragment =>
                    {
                        said.Append(fragment);
                        var so_far = said.ToString();
                        RunOnUiThread(() => _answer.Text = so_far);
                    }).ConfigureAwait(false);

                if (said.Length == 0)
                    RunOnUiThread(() => _answer.Text = "It had nothing to say about that.");
            }
            catch (Cancelled) when (ct.IsCancellationRequested)
            {
                // The screen went away or another question replaced this one.
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"vision turn failed: {ex}");
                RunOnUiThread(() => _answer.Text = $"That did not work: {ex.Message}");
            }
            finally
            {
                RunOnUiThread(() =>
                {
                    _ask.Enabled = true;
                    _pick.Enabled = true;
                });
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
