// ScreenUpWakeWord.cs
//
// The wake word, running in this process, for the phones that will not let a
// service hold the microphone.
//
// WHY THERE HAS TO BE A SECOND ONE. The resident path is a foreground service
// with a persistent notification, which is the only durable form Android offers
// - and Huawei, Xiaomi, Oppo and Vivo stop it anyway, on their own schedule,
// whatever the notification says. DeviceSetup.AllowBackgroundAsync asks the
// owner for an exemption and a great many phones simply do not grant one. On
// those handsets the resident assistant returned Failed and that was the end of
// it: the product's headline feature, absent, on a large share of exactly the
// phones this product exists for.
//
// So: when the service cannot hold the mic, this does, for as long as somebody
// is looking at the app. It is a strictly smaller promise and the screen has to
// say so - see ResidentState.ScreenOnly.
//
// ONE MICROPHONE, TWO CONSUMERS, AND THIS IS THE WHOLE DIFFICULTY. Android hands
// out AudioRecord exclusively: while this loop holds the mic, the turn that
// follows a wake CANNOT open it. The sequence has to be strictly ordered -
// release, listen, take it back - and "release" has to mean the capture is
// actually closed, not that a token was cancelled. StopAsync therefore AWAITS
// the loop. Cancelling and returning immediately looks correct, races perfectly
// on a fast machine, and drops the first second of every wake on a slow one,
// which reads to a person as "it ignored me".
//
// Ported from the other head's HandsFree, which was proven on a P30 and is the
// only reason any of the numbers in here are trusted.

using Android.Util;
using CircleAI.Voice;

namespace CircleAI.Assistant.Device;

/// <summary>Listens for the wake phrase while the app is on screen.</summary>
public sealed class ScreenUpWakeWord : IAsyncDisposable
{
    private const string Tag = "CircleAI.ScreenWake";

    private readonly string _bundleDir;
    private readonly string? _keywordsFile;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <param name="bundleDir">Where the wake bundle was unpacked.</param>
    /// <param name="keywordsFile">
    /// The phrase list for the chosen language, or null for the bundle's own.
    /// </param>
    public ScreenUpWakeWord(string bundleDir, string? keywordsFile = null)
    {
        _bundleDir = bundleDir;
        _keywordsFile = keywordsFile;
    }

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

    /// <summary>Closes the microphone and waits until it is genuinely closed.</summary>
    /// <remarks>
    /// THE AWAIT IS THE POINT - see the note at the top of this file. The
    /// caller's very next act is usually to open the mic for the spoken turn,
    /// and that will fail or come up empty if this has only asked the loop to
    /// stop.
    /// </remarks>
    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;
        _cts = null;
        _loop = null;

        if (cts is null) return;

        try { cts.Cancel(); } catch (ObjectDisposedException) { return; }

        if (loop is not null)
        {
            // Never let a stuck capture wedge the app: the turn matters more than
            // a tidy shutdown, and the process reclaims the mic either way.
            try { await Task.WhenAny(loop, Task.Delay(2000)).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn(Tag, "wake loop did not stop cleanly: " + ex); }
        }

        cts.Dispose();
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        try
        {
            using var kws = new ConfirmedKeywordSpotter(
                new ZipformerKwsSpotter(_bundleDir, _keywordsFile));

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

            // A HEARTBEAT, BECAUSE SILENCE HAS TWO CAUSES AND THEY LOOK THE SAME.
            // Nothing is logged unless the phrase fires or the confirmer vetoes
            // it, so a phone that hears nothing and a phone that is not listening
            // at all produce identical logs - which is exactly the position the
            // other head was in when the wake word stopped responding and every
            // structural check said it was fine.
            var chunks = 0L;
            var since = 0L;
            var peak = 0f;
            var sum = 0.0;

            await foreach (var chunk in mic.CaptureAsync(ct).ConfigureAwait(false))
            {
                // PCM16 little-endian to float in [-1, 1]. NOT scaled to the
                // int16 range: KaldiFbank takes normalised samples, and
                // multiplying here is exactly the bug that made the wake word
                // deaf for a day.
                var samples = chunk.Length / 2;
                if (samples > pcm.Length) pcm = new float[samples];
                var span = chunk.Span;
                for (var i = 0; i < samples; i++)
                    pcm[i] = (short)(span[i * 2] | (span[i * 2 + 1] << 8)) / 32768f;

                for (var i = 0; i < samples; i++)
                {
                    var a = Math.Abs(pcm[i]);
                    if (a > peak) peak = a;
                    sum += a;
                }

                chunks++;
                since += samples;

                kws.AcceptWaveform(pcm.AsSpan(0, samples));

                // Roughly every 5 s at 16 kHz. Cheap, and it is the only evidence
                // that the microphone is actually delivering sound.
                if (since >= 80_000)
                {
                    Log.Info(Tag, $"mic alive: {chunks} chunks, peak {peak:F3}, mean {sum / since:F4}");
                    since = 0; peak = 0f; sum = 0;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ordinary stop.
        }
        catch (Exception ex)
        {
            // A wake word that cannot start must not take the app down with it;
            // the circle still works by tap.
            Log.Error(Tag, "wake loop failed: " + ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
