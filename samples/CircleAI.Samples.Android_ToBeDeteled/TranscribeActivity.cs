#nullable enable

// TranscribeActivity.cs
//
// Hand it a recording and it writes down what was said, and when.
//
// LISTENING WAS A DEAD ROW TOO. The abilities screen has offered "Listening -
// understands you when you speak" for as long as it has existed, with Whisper
// catalogued and downloadable, and ScreenFor(Asr) returned null - so the row
// fetched 78 MB and led nowhere, exactly as Seeing did. This head could not
// transcribe anything at all: not a file, and not the microphone either.
//
// A FILE, NOT THE MICROPHONE, AND THAT IS THE POINT. Dictation and meeting
// capture already exist on IConversation and both open a microphone. What did
// not exist anywhere was a way to hand over a recording that already exists -
// a voice memo, an interview, an episode - which is the thing people actually
// reach for a transcriber to do. Transcribing.FileAsync could do it for a
// while before anything could ask.
//
// PROGRESS IS THE FEATURE, NOT DECORATION. Tiny runs at roughly real time on a
// P30, so forty minutes of audio is a forty-minute wait, and a wait with no
// number is indistinguishable from a hang. The percentage is why this screen is
// usable at all.
//
// NO PERMISSION IS ASKED FOR, TWICE OVER. ActionGetContent hands back a URI the
// system has already granted, and ActionCreateDocument lets somebody choose
// where the subtitles land. Asking for storage access would be a dialog for
// something the pickers give for free.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using CircleAI.Assistant.Voice;
using CircleAI.Voice;

using Cancelled = System.OperationCanceledException;
using IOPath = System.IO.Path;

namespace CircleAI.Assistant.Device;

[Activity(Label = "Transcribe", Exported = false)]
public class TranscribeActivity : Activity
{
    const string Tag = "CircleAI.Transcribe";

    const int PickAudio = 4712;
    const int SaveSubtitles = 4713;

    TextView _status = null!;
    TextView _body = null!;
    Button _pick = null!;
    Button _save = null!;
    ProgressBar _bar = null!;

    CancellationTokenSource? _run;

    /// <summary>The last transcript, kept so it can be saved after the fact.</summary>
    TranscriptionResult? _result;
    string _sourceName = "transcript";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActionBar?.Hide();

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Ui.Bg);
        root.AddView(Ui.HomeBar(this, "Transcribe"), Ui.Fill());

        var pad = Ui.Dp(this, 16);
        var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        body.SetPadding(pad, pad, pad, pad);

        _pick = Ui.Action(this, "Choose a recording", primary: true);
        _pick.Click += (_, _) => Pick();
        body.AddView(_pick, Ui.Fill());

        _bar = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal)
        {
            Max = 100,
            Indeterminate = false,
        };
        _bar.Visibility = ViewStates.Gone;
        var barParams = Ui.Fill();
        barParams.TopMargin = Ui.Dp(this, 12);
        body.AddView(_bar, barParams);

        _status = Ui.Label(this, "Pick a recording to start.", 13f, Ui.InkSoft);
        var sParams = Ui.Fill();
        sParams.TopMargin = Ui.Dp(this, 10);
        body.AddView(_status, sParams);

        _save = Ui.Action(this, "Save subtitles", primary: false);
        _save.Enabled = false;
        _save.Click += (_, _) => SaveSrt();
        var saveParams = Ui.Fill();
        saveParams.TopMargin = Ui.Dp(this, 12);
        body.AddView(_save, saveParams);

        _body = Ui.Label(this, "", 15f, Ui.Ink);
        _body.SetTextIsSelectable(true);
        var bParams = Ui.Fill();
        bParams.TopMargin = Ui.Dp(this, 16);
        body.AddView(_body, bParams);

        var scroll = new ScrollView(this);
        scroll.AddView(body);
        root.AddView(scroll, Ui.Fill(1f));

        SetContentView(root);
    }

    void Pick()
    {
        // audio/* AND video/*: the audio somebody wants written down is very
        // often inside a video they recorded, and MediaExtractor reads the
        // audio track out of an .mp4 exactly as it reads an .m4a.
        var intent = new Intent(Intent.ActionGetContent);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraMimeTypes, new[] { "audio/*", "video/*" });
        StartActivityForResult(Intent.CreateChooser(intent, "Choose a recording"), PickAudio);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != Result.Ok || data?.Data is null) return;

        if (requestCode == PickAudio) Transcribe(data.Data);
        else if (requestCode == SaveSubtitles) WriteSrt(data.Data);
    }

    void Transcribe(Android.Net.Uri uri)
    {
        _pick.Enabled = false;
        _save.Enabled = false;
        _result = null;
        _body.Text = "";
        _bar.Visibility = ViewStates.Visible;
        _bar.Progress = 0;
        _status.Text = "Opening the recording...";

        _run?.Cancel();
        _run = new CancellationTokenSource();
        var ct = _run.Token;

        _ = Task.Run(async () =>
        {
            string? working = null;
            try
            {
                // COPIED TO A REAL PATH FIRST. A content URI is not a file path,
                // and MediaExtractor.SetDataSource(string) wants one - so the
                // stream is spooled to the cache, transcribed, and deleted. The
                // alternative is a file descriptor overload that behaves
                // differently across OEMs; a copy is boring and works.
                working = await SpoolAsync(uri, ct).ConfigureAwait(false);
                if (working is null)
                {
                    RunOnUiThread(() => Fail("That recording could not be opened."));
                    return;
                }

                RunOnUiThread(() => _status.Text = "Listening to it...");

                var (listener, why) = await CircleAIListener
                    .TryCreateAsync(ModelStore.Path, ct: ct).ConfigureAwait(false);

                if (listener is null)
                {
                    RunOnUiThread(() => Fail($"The ears are not on this phone yet. {why}"));
                    return;
                }

                await using (listener)
                {
                    var progress = new Progress<double>(p => RunOnUiThread(() =>
                    {
                        _bar.Progress = (int)(p * 100);
                        _status.Text = $"{(int)(p * 100)}% — this runs at about real time.";
                    }));

                    var result = await listener.TranscribeFileAsync(
                        working, AndroidAudioDecoder.Instance,
                        language: null, progress: progress, ct: ct).ConfigureAwait(false);

                    _result = result;

                    // KEPT, NOT JUST SHOWN. Until there was somewhere for a
                    // transcript to live, "search across everything" searched
                    // only memory - a transcript was displayed, optionally
                    // written out as an .srt to a location the person chose, and
                    // then discarded. Somebody who recorded a clinic appointment
                    // could not find it again an hour later.
                    //
                    // App-private storage, nothing synced, and forgetting one
                    // deletes it. Failing to keep it must not lose the transcript
                    // already on the screen, so this is best-effort and said out
                    // loud rather than thrown.
                    try
                    {
                        await CircleAI.Assistant.CircleAISession.Transcripts
                            .KeepAsync(_sourceName, new CircleAI.Assistant.Transcript(
                                result.Text,
                                [.. result.Timed.Select(x => new CircleAI.Assistant.TranscriptLine(
                                    x.Text, x.Start, x.End, x.Speaker))],
                                result.LanguageCode,
                                result.Confidence), ct)
                            .ConfigureAwait(false);
                    }
                    catch (Exception keep)
                    {
                        Log.Warn(Tag, $"could not keep the transcript: {keep.Message}");
                    }

                    RunOnUiThread(() =>
                    {
                        _bar.Visibility = ViewStates.Gone;
                        _body.Text = string.IsNullOrWhiteSpace(result.Text)
                            ? "Nothing was said in that recording."
                            : result.Text;

                        _status.Text = result.Timed.Count > 0
                            ? $"{result.Timed.Count} lines · {result.LanguageCode} · " +
                              $"confidence {result.Confidence:0.00}"
                            : $"{result.LanguageCode} · no timings reported";

                        _save.Enabled = result.Timed.Count > 0;
                    });
                }
            }
            catch (Cancelled) when (ct.IsCancellationRequested)
            {
                // The screen went away, or another recording replaced this one.
            }
            catch (NotSupportedException ex)
            {
                // The decoder said no. That is a sentence about the BUILD, not
                // about their recording, and it is worth showing verbatim.
                Log.Warn(Tag, ex.Message);
                RunOnUiThread(() => Fail(ex.Message));
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"transcription failed: {ex}");
                RunOnUiThread(() => Fail($"That did not work: {ex.Message}"));
            }
            finally
            {
                if (working is not null)
                    try { File.Delete(working); } catch { /* the cache is swept anyway */ }

                RunOnUiThread(() =>
                {
                    _pick.Enabled = true;
                    _bar.Visibility = ViewStates.Gone;
                });
            }
        });
    }

    void Fail(string message)
    {
        _bar.Visibility = ViewStates.Gone;
        _status.Text = message;
        _save.Enabled = false;
    }

    /// <summary>Copy a content URI into the cache and return its path.</summary>
    async Task<string?> SpoolAsync(Android.Net.Uri uri, CancellationToken ct)
    {
        var name = DisplayName(uri) ?? "recording";
        _sourceName = IOPath.GetFileNameWithoutExtension(name);

        var target = IOPath.Combine(AppPaths.Cache, $"transcribe-{Guid.NewGuid():N}{IOPath.GetExtension(name)}");

        using var input = ContentResolver?.OpenInputStream(uri);
        if (input is null) return null;

        using (var output = File.Create(target))
            await input.CopyToAsync(output, ct).ConfigureAwait(false);

        return target;
    }

    /// <summary>The name the picker shows, so the subtitles can be named after it.</summary>
    string? DisplayName(Android.Net.Uri uri)
    {
        try
        {
            using var cursor = ContentResolver?.Query(uri, null, null, null, null);
            if (cursor is null || !cursor.MoveToFirst()) return null;

            var column = cursor.GetColumnIndex(Android.Provider.IOpenableColumns.DisplayName);
            return column >= 0 ? cursor.GetString(column) : null;
        }
        catch
        {
            return null;
        }
    }

    void SaveSrt()
    {
        if (_result is null || _result.Timed.Count == 0) return;

        // ActionCreateDocument: the person chooses where it goes, and the app
        // needs no storage permission to write there.
        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("application/x-subrip");
        intent.PutExtra(Intent.ExtraTitle, $"{_sourceName}.srt");
        StartActivityForResult(intent, SaveSubtitles);
    }

    void WriteSrt(Android.Net.Uri target)
    {
        if (_result is null) return;

        try
        {
            using var output = ContentResolver?.OpenOutputStream(target);
            if (output is null) { _status.Text = "That location could not be written to."; return; }

            using var writer = new StreamWriter(output);
            writer.Write(Subtitles.ToSrt(_result.Timed));

            _status.Text = "Subtitles saved.";
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"could not save subtitles: {ex}");
            _status.Text = $"Could not save that: {ex.Message}";
        }
    }

    protected override void OnDestroy()
    {
        _run?.Cancel();
        _run?.Dispose();
        _run = null;
        base.OnDestroy();
    }
}
