#if IT_VOICE_ANDROID
#nullable enable

// HandsFree.cs
//
// The wake word, running on the landing screen, so the phone answers its name.
//
// UNTIL NOW THE WAKE WORD EXISTED BUT NOTHING LISTENED. Every piece was built and
// proven — the spotter, the two-stage confirm, the bundle, a whole screen that
// demonstrates it hearing "Hey B" — and none of it was connected to the circle
// people actually look at. The assistant could be woken only by tapping, which is
// the one thing a voice-first assistant is supposed to make unnecessary.
//
// ONE MICROPHONE, TWO CONSUMERS. This is the whole difficulty. Android hands out
// AudioRecord exclusively: while this loop holds the mic, the turn that follows a
// wake CANNOT open it. So the sequence has to be strictly ordered — release, then
// listen, then take it back — and "release" has to mean the capture is actually
// closed, not merely that a token was cancelled. StopAsync therefore AWAITS the
// loop's completion. Cancelling and returning immediately looks correct, races
// perfectly on a fast machine, and drops the first second of every wake on a slow
// one, which reads to a person as "it ignored me".
//
// Deliberately not the resident foreground service (ResidentWakeWord). This
// listens only while the screen is up, which is the honest default: a microphone
// that is always open is a promise about privacy, and that promise should be made
// explicitly by someone who chose it, not switched on by a landing screen.

using System;
using System.Threading;
using System.Threading.Tasks;
using Android.Util;
using CircleAI.Voice;

namespace CircleAI.Samples.It.Mobile;

/// <summary>Listens for the wake phrase while the screen is up.</summary>
public sealed class HandsFree : IAsyncDisposable
{
    const string Tag = "CircleAI.HandsFree";

    readonly string _bundleDir;
    CancellationTokenSource? _cts;
    Task? _loop;

    public HandsFree(string bundleDir) => _bundleDir = bundleDir;

    /// <summary>Raised off the UI thread when the phrase lands.</summary>
    public event EventHandler<string>? Woke;

    /// <summary>True while the microphone is open for the wake phrase.</summary>
    public bool IsListening => _loop is { IsCompleted: false };

    /// <summary>Opens the microphone. Does nothing if already listening.</summary>
    public void Start()
    {
        if (IsListening) return;

        var cts = new CancellationTokenSource();
        _cts = cts;
        _loop = Task.Run(() => ListenAsync(cts.Token));
    }

    /// <summary>
    /// Closes the microphone and waits until it is genuinely closed.
    /// </summary>
    /// <remarks>
    /// The await is the point — see the note at the top of this file. The caller's
    /// very next act is usually to open the mic for the spoken turn, and that will
    /// fail or come up empty if this has only asked the loop to stop.
    /// </remarks>
    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;
        _cts = null;
        _loop = null;

        if (cts is null) return;
        cts.Cancel();

        if (loop is not null)
        {
            // Never let a stuck capture wedge the screen: the turn is more
            // important than a tidy shutdown, and the mic gets reclaimed by the
            // process either way.
            try { await Task.WhenAny(loop, Task.Delay(2000)).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn(Tag, "wake loop did not stop cleanly: " + ex); }
        }

        cts.Dispose();
    }

    async Task ListenAsync(CancellationToken ct)
    {
        try
        {
            using var kws = new ConfirmedKeywordSpotter(new ZipformerKwsSpotter(_bundleDir));
            Log.Info(Tag, $"listening for: {string.Join(" | ", kws.Keywords)}");

            kws.Woke += (_, d) =>
            {
                Log.Info(Tag, $"HEARD \"{d.Phrase}\" p={d.Probability:F4} @{d.AtFrame}");
                Woke?.Invoke(this, d.Phrase);
            };
            kws.Rejected += (_, r) =>
                Log.Info(Tag, $"VETOED \"{r.Detection.Phrase}\" — {r.Reason}");

            await using var mic = new AndroidAudioCapture();
            var pcm = new float[1600];

            await foreach (var chunk in mic.CaptureAsync(ct).ConfigureAwait(false))
            {
                // PCM16 little-endian to float in [-1, 1]. NOT scaled to the int16
                // range: KaldiFbank takes normalised samples, and multiplying here
                // is exactly the bug that made the wake word deaf for a day.
                var samples = chunk.Length / 2;
                if (samples > pcm.Length) pcm = new float[samples];
                var span = chunk.Span;
                for (var i = 0; i < samples; i++)
                    pcm[i] = (short)(span[i * 2] | (span[i * 2 + 1] << 8)) / 32768f;

                kws.AcceptWaveform(pcm.AsSpan(0, samples));
            }
        }
        catch (OperationCanceledException)
        {
            // Ordinary stop.
        }
        catch (Exception ex)
        {
            // A wake word that cannot start must not take the screen down with it;
            // the circle still works by tap.
            Log.Error(Tag, "wake loop failed: " + ex);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
#endif
