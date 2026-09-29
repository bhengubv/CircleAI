#nullable enable

// MusicActivity.cs
//
// A piece of music, made on the phone, from nothing.
//
// THE THIRD DEAD CAPABILITY IN THIS HEAD, AFTER SEEING AND LISTENING.
// ProceduralMusicBedGenerator has been in CircleAI.Music - referenced by this
// project - producing real PCM from a mood and a duration, with no screen
// anywhere and no caller outside the capability sweep.
//
// AND IT IS THE ONE THAT NEEDS NO MODEL. Everything else on this phone waits on
// a download: the brain, the voice, the ears, the eyes. This is arithmetic -
// chords, an arpeggio and an envelope, synthesised in managed code - so it works
// on a phone with no network, no storage to spare and nothing installed. That
// makes it the only capability here that can be demonstrated the moment the app
// opens, which is worth a screen on its own.
//
// WHAT IT IS NOT. This is a procedural BED, not neural music generation. The
// Music modality exists in the catalogue and has nothing in it, so the neural
// path is an empty seam - and calling a chord bed "AI music" would be the kind
// of claim this codebase keeps a register to avoid. The screen says bed.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using CircleAI.Music;

// Android.OS ships Environment (device storage) and its own
// OperationCanceledException, and neither is the one this file wants.
// Aliased the way WakeWordActivity handles the same clash.
using Cancelled = System.OperationCanceledException;
using SysEnv = System.Environment;
using IOPath = System.IO.Path;

namespace CircleAI.Assistant.Device;

[Activity(Label = "Music", Exported = false)]
public class MusicActivity : Activity
{
    const string Tag = "CircleAI.Music";
    const int SaveWav = 4714;

    /// <summary>
    /// How long a bed to make.
    /// </summary>
    /// <remarks>
    /// Thirty seconds because synthesis is CPU-bound and this runs on a P30:
    /// long enough to hear the shape of it, short enough that nobody waits.
    /// </remarks>
    static readonly TimeSpan Length = TimeSpan.FromSeconds(30);

    static readonly Mood[] Moods =
    [
        Mood.Calm, Mood.Warm, Mood.Reflective, Mood.Uplifting,
        Mood.Corporate, Mood.Focus, Mood.Energetic, Mood.Playful, Mood.Cinematic,
    ];

    TextView _status = null!;
    LinearLayout _choices = null!;
    Button _save = null!;

    Android.Media.MediaPlayer? _player;
    string? _made;
    Mood _mood = Mood.Calm;
    CancellationTokenSource? _work;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActionBar?.Hide();

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Ui.Bg);
        root.AddView(Ui.HomeBar(this, "Music"), Ui.Fill());

        var pad = Ui.Dp(this, 16);
        var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        body.SetPadding(pad, pad, pad, pad);

        var blurb = Ui.Label(this,
            "Thirty seconds of music, made on this phone. No model, no network — " +
            "it works with nothing installed.",
            13f, Ui.InkSoft);
        body.AddView(blurb, Ui.Fill());

        _choices = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var cParams = Ui.Fill();
        cParams.TopMargin = Ui.Dp(this, 14);
        body.AddView(_choices, cParams);

        foreach (var mood in Moods)
        {
            var button = Ui.Action(this, mood.ToString(), primary: mood == _mood);
            var captured = mood;
            button.Click += (_, _) => Make(captured);

            var bParams = Ui.Fill();
            bParams.TopMargin = Ui.Dp(this, 8);
            _choices.AddView(button, bParams);
        }

        _status = Ui.Label(this, "Pick a mood.", 13f, Ui.InkSoft);
        var sParams = Ui.Fill();
        sParams.TopMargin = Ui.Dp(this, 16);
        body.AddView(_status, sParams);

        _save = Ui.Action(this, "Save as WAV", primary: false);
        _save.Enabled = false;
        _save.Click += (_, _) => Save();
        var saveParams = Ui.Fill();
        saveParams.TopMargin = Ui.Dp(this, 12);
        body.AddView(_save, saveParams);

        var scroll = new ScrollView(this);
        scroll.AddView(body);
        root.AddView(scroll, Ui.Fill(1f));

        SetContentView(root);
    }

    void Make(Mood mood)
    {
        _mood = mood;
        _status.Text = $"Making {mood.ToString().ToLowerInvariant()}...";
        _save.Enabled = false;
        Stop();

        _work?.Cancel();
        _work = new CancellationTokenSource();
        var ct = _work.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                var started = SysEnv.TickCount64;

                // OFF THE UI THREAD ON PURPOSE. Synthesis is CPU-bound and
                // synchronous - the generator's own remarks say so - and a
                // thirty-second bed on a P30 is not instant.
                var bed = await new ProceduralMusicBedGenerator()
                    .GenerateAsync(MusicSpec.ForMood(mood, Length), ct)
                    .ConfigureAwait(false);

                var took = SysEnv.TickCount64 - started;

                var path = IOPath.Combine(AppPaths.Cache, $"bed-{mood}.wav");
                await File.WriteAllBytesAsync(path, bed.ToWav(), ct).ConfigureAwait(false);
                _made = path;

                RunOnUiThread(() =>
                {
                    _status.Text =
                        $"{mood} · {bed.Duration.TotalSeconds:0}s · " +
                        $"made in {took} ms · {bed.Backend.ToString().ToLowerInvariant()}";
                    _save.Enabled = true;
                    Play(path);
                });
            }
            catch (Cancelled) when (ct.IsCancellationRequested)
            {
                // Another mood replaced this one, or the screen went away.
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"could not make a bed: {ex}");
                RunOnUiThread(() => _status.Text = $"That did not work: {ex.Message}");
            }
        });
    }

    void Play(string path)
    {
        Stop();
        try
        {
            _player = new Android.Media.MediaPlayer();
            _player.SetDataSource(path);
            _player.Prepare();
            _player.Start();
        }
        catch (Exception ex)
        {
            // A bed that cannot be played is still a bed that can be saved, so
            // this is a note rather than a failure.
            Log.Warn(Tag, $"playback failed: {ex.Message}");
            _status.Text += " (saved, but this phone would not play it)";
        }
    }

    void Stop()
    {
        try { _player?.Stop(); } catch { /* already stopped */ }
        _player?.Release();
        _player?.Dispose();
        _player = null;
    }

    void Save()
    {
        if (_made is null) return;

        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("audio/wav");
        intent.PutExtra(Intent.ExtraTitle, $"{_mood.ToString().ToLowerInvariant()}-bed.wav");
        StartActivityForResult(intent, SaveWav);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != SaveWav || resultCode != Result.Ok || data?.Data is null) return;
        if (_made is null) return;

        try
        {
            using var output = ContentResolver?.OpenOutputStream(data.Data);
            if (output is null) { _status.Text = "That location could not be written to."; return; }

            using var input = File.OpenRead(_made);
            input.CopyTo(output);

            _status.Text = "Saved.";
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"could not save: {ex}");
            _status.Text = $"Could not save that: {ex.Message}";
        }
    }

    protected override void OnPause()
    {
        // THE MUSIC STOPS WHEN THE SCREEN DOES. A bed still playing after
        // somebody has left is the app talking over whatever they opened next.
        Stop();
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        _work?.Cancel();
        _work?.Dispose();
        _work = null;
        Stop();
        base.OnDestroy();
    }
}
