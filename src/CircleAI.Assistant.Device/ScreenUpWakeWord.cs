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
using CircleAI.Device;
using CircleAI.Voice;

namespace CircleAI.Assistant.Device;

/// <summary>Listens for the wake phrase while the app is on screen.</summary>
/// <remarks>
/// AND IT IS AN <see cref="IResidentListener"/>, WHICH IS HOW THE REST OF THE
/// PRODUCT FINDS OUT IT IS RUNNING. This loop was started directly and told nobody,
/// so <c>CircleNeuronService.IsListening</c> — the one flag three other places read
/// to answer "is the microphone open" — stayed false while the microphone was open.
/// The Settings screen showed the result side by side: "Answer to its name" ticked,
/// because that reads this object, and the Waking ability offering "Turn on",
/// because that reads the flag. Same phone, same second, two answers.
/// <para>
/// Implementing the seam rather than being adapted to it, because the seam is four
/// members this class already has in all but name, and the service needs them to
/// hold the CPU wake lock and to write the microphone disclosure on the shade —
/// neither of which a loop nobody knows about can get.
/// </para>
/// </remarks>
public sealed class ScreenUpWakeWord : IAsyncDisposable, IResidentListener
{
    private const string Tag = "CircleAI.ScreenWake";

    /// <summary>
    /// A log line with dots for decimal points, whatever the phone's locale is.
    /// </summary>
    /// <remarks>
    /// Interpolation uses the current culture, so this read "peak 0,015" on a device
    /// set to af-ZA - fine to a person, and a nuisance the moment a log is pasted
    /// into anything that parses numbers.
    /// </remarks>
    private static string Invariant(FormattableString s)
        => s.ToString(System.Globalization.CultureInfo.InvariantCulture);

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

    /// <inheritdoc />
    /// <remarks>
    /// THE PHRASE IS NOT KNOWN UNTIL THE SPOTTER IS BUILT, which happens on the loop
    /// thread a moment after Start returns — so this reads "its name" for that moment
    /// rather than a phrase invented here. The notification is refreshed again when
    /// the listener reports it is up, by which time this is the real list.
    /// </remarks>
    public string Describe => _describe;

    private volatile string _describe = "its name";

    /// <inheritdoc />
    /// <remarks>
    /// RAISED FROM THE CONFIRMER'S VETO, which is the one near miss this loop can
    /// actually see: a phrase that matched in full and was turned down for being too
    /// quiet, too fast or too soon after the last one. Partial matches are counted
    /// inside the spotter and never surface here, so they are not claimed.
    /// </remarks>
    public event EventHandler<ResidentNearMiss>? Nearly;

    /// <summary>True while the microphone is open for the wake phrase.</summary>
    public bool IsListening => _loop is { IsCompleted: false };

    /// <inheritdoc />
    /// <remarks>Synchronous underneath: starting is handing a loop to the thread pool.</remarks>
    public Task StartAsync(CancellationToken ct = default)
    {
        Start();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>The token is accepted for the seam; stopping is cancelling the loop.</remarks>
    public Task StopAsync(CancellationToken ct) => StopAsync();

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

            // WHAT THE SHADE SAYS IT IS LISTENING FOR. The notification is written
            // from Describe, and until the spotter existed there was no phrase to
            // put in it - so the disclosure named the phrase only by luck of timing.
            _describe = kws.Keywords.Count > 0 ? kws.Keywords[0] : "its name";
            CircleNeuronService.RefreshNotification();

            kws.Woke += (_, d) =>
            {
                Log.Info(Tag, $"HEARD \"{d.Phrase}\" p={d.Probability:F4} @{d.AtFrame}");
                Woke?.Invoke(this, d.Phrase);
            };
            kws.Rejected += (_, r) =>
            {
                Log.Info(Tag, $"VETOED \"{r.Detection.Phrase}\" — {r.Reason}");

                // A COMPLETE MATCH, REFUSED - which is what ResidentNearMiss.Refused
                // is for, and the only thing that distinguishes "stand closer" from
                // "say it again more clearly" on a screen watching the listener.
                var tokens = kws.TokenCountOf(r.Detection.Phrase);
                Nearly?.Invoke(this, new ResidentNearMiss(
                    r.Detection.Phrase, tokens, tokens, r.Detection.Probability, r.Reason));
            };

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

            // HOW OFTEN THIS SAYS SO. The decision lives in CircleAI.Assistant
            // because this library is net10.0-android and no test project can
            // reference one of those - the exact wall OPEN-GAPS records for
            // ModelChoice, where "the one rule that decides what a person is
            // offered was the one rule nothing asserted".
            var watch = new CircleAI.Assistant.MicWatch();

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

                // SILENCE IS THE NEWS, NOT THE HEARTBEAT. A dead microphone
                // delivers zeros; a quiet room delivers a peak around 0.015 with a
                // mean near 0.0018, measured on a P30 on 2026-10-04 - two orders of
                // magnitude apart, so the two cases the remark above cares about are
                // distinguishable from the signal itself and do not need a line
                // every five seconds to tell them apart.
                var mean = since > 0 ? sum / since : 0.0;
                switch (watch.Observe(samples, peak))
                {
                    case CircleAI.Assistant.MicSay.Nothing:
                        continue;

                    case CircleAI.Assistant.MicSay.WentSilent:
                        Log.Warn(Tag, Invariant(
                            $"mic SILENT: {chunks} chunks and no signal at all - peak {peak:F3}"));
                        break;

                    case CircleAI.Assistant.MicSay.StillSilent:
                        Log.Warn(Tag, Invariant(
                            $"mic STILL silent: {chunks} chunks, nothing arriving"));
                        break;

                    case CircleAI.Assistant.MicSay.CameBack:
                        Log.Info(Tag, Invariant(
                            $"mic back: {chunks} chunks, peak {peak:F3}, mean {mean:F4}"));
                        break;

                    default:
                        Log.Info(Tag, Invariant(
                            $"mic alive: {chunks} chunks, peak {peak:F3}, mean {mean:F4}"));
                        break;
                }

                since = 0; peak = 0f; sum = 0;
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
