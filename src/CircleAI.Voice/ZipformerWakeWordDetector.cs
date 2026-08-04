#nullable enable

// ZipformerWakeWordDetector.cs
//
// The zipformer keyword spotter, wearing the interface the rest of the system
// actually talks to.
//
// WITHOUT THIS FILE THE ENGINE IS AN ISLAND. VoicePipeline, VoiceLoop and
// VoiceCompanionListener all take an IWakeWordDetector; ZipformerKwsSpotter
// implements none of it, so everything measured on the P30 was reachable only
// from a sample activity written specially for it. An engine that cannot be
// composed is a demo.
//
// IT CAN DO SOMETHING THE OLD DETECTOR CANNOT, and the interface already has a
// name for it. KwsWakeWordDetector scores the ONE phrase its model was trained
// on and reports SupportsPerPhraseMatching = false, so the access list the
// interface documents — a household or office giving each permitted person their
// own phrase — has never been implementable. Here keywords are text in a trie:
// any number of phrases, each with its own threshold, matched independently. The
// access list becomes real.
//
// STILL A KNOCK, NOT A LOCK. Repeating the interface's own warning because it
// matters more now that per-person phrases actually work: a phrase is a shared
// secret. Anyone who overhears one can use it, and nothing here tells two
// speakers apart. Speaker identification is a different model.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Voice;

/// <summary>How the zipformer wake detector is set up.</summary>
/// <param name="BundleDirectory">Extracted bundle: encoder, decoder, joiner, tokens.</param>
/// <param name="KeywordsFile">Defaults to keywords.txt inside the bundle.</param>
/// <param name="Threshold">Default acceptance probability; per-phrase <c>#</c> overrides win.</param>
/// <param name="Confirmer">Stage two. Null means the default onset check.</param>
/// <param name="MinIntervalBetweenFires">
/// Debounce. One spoken phrase can satisfy the decoder more than once as the beam
/// settles, and a caller that starts a recording per event would start two.
/// </param>
public sealed record ZipformerWakeConfig(
    string BundleDirectory,
    string? KeywordsFile = null,
    double Threshold = 0.5,
    IWakeConfirmer? Confirmer = null,
    TimeSpan? MinIntervalBetweenFires = null);

/// <summary>
/// Wake-word detection on a streaming zipformer transducer, with a second stage.
/// </summary>
public sealed class ZipformerWakeWordDetector : IWakeWordDetector
{
    private readonly IAudioCapture _capture;
    private readonly ZipformerWakeConfig _config;
    private readonly ConfirmedKeywordSpotter _spotter;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTimeOffset _lastFireUtc = DateTimeOffset.MinValue;
    private bool _disposed;

    public ZipformerWakeWordDetector(IAudioCapture capture, ZipformerWakeConfig config)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(config);

        _capture = capture;
        _config = config;
        _debounce = config.MinIntervalBetweenFires ?? TimeSpan.FromMilliseconds(1200);

        _spotter = new ConfirmedKeywordSpotter(
            new ZipformerKwsSpotter(config.BundleDirectory, config.KeywordsFile)
            {
                Threshold = config.Threshold,
            },
            config.Confirmer);

        _spotter.Woke += OnWoke;
        _spotter.Rejected += (_, r) => Vetoed?.Invoke(this, (r.Detection.Phrase, r.Reason));

        WakeWords = _spotter.Keywords.Count > 0
            ? _spotter.Keywords.ToArray()
            : new[] { "Hey B" };
        WakeWord = WakeWords[0];
    }

    /// <inheritdoc />
    public string WakeWord { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Genuinely several, independently matched — see the header. Each may carry
    /// its own acceptance threshold in the keywords file, so a phrase that is hard
    /// to hear can be given more room without loosening anyone else's.
    /// </remarks>
    public IReadOnlyList<string> WakeWords { get; }

    /// <summary><c>true</c> — every phrase is matched on its own merits.</summary>
    public bool SupportsPerPhraseMatching => true;

    /// <summary>Phrases that can never fire because a shorter one finishes inside them.</summary>
    /// <remarks>
    /// Exposed on the detector, not buried in the spotter, because this is the
    /// level a host configures the access list at — and a listed phrase that
    /// cannot fire looks exactly like a phrase nobody is saying.
    /// </remarks>
    public IReadOnlyList<(string Phrase, string ShadowedBy)> UnreachableWakeWords =>
        _spotter.ShadowedKeywords;

    /// <inheritdoc />
    public bool IsListening { get; private set; }

    /// <inheritdoc />
    public event EventHandler<WakeWordDetectedEventArgs>? WakeWordDetected;

    /// <summary>Stage one fired and stage two turned it down, with the reason.</summary>
    /// <remarks>
    /// Not part of the interface, and worth having anyway: without it "the wake
    /// word did not fire" and "it fired and we vetoed it" are the same silence.
    /// </remarks>
    public event EventHandler<(string Phrase, string? Reason)>? Vetoed;

    private void OnWoke(object? sender, KwsDetection d)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastFireUtc < _debounce) return;
            _lastFireUtc = now;
        }

        WakeWordDetected?.Invoke(this, new WakeWordDetectedEventArgs
        {
            WakeWord = d.Phrase,
            Confidence = (float)Math.Clamp(d.Probability, 0, 1),
        });
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (IsListening) return Task.CompletedTask;
            IsListening = true;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        _spotter.Reset();
        _loop = Task.Run(() => ListenAsync(_cts!.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        var pcm = Array.Empty<float>();
        try
        {
            await foreach (var chunk in _capture.CaptureAsync(ct).ConfigureAwait(false))
            {
                // PCM16 little-endian to float in [-1, 1]. NOT scaled to int16
                // range: KaldiFbank takes normalised samples, and multiplying here
                // is exactly the inversion that made this deaf for a day.
                var samples = chunk.Length / 2;
                if (samples > pcm.Length) pcm = new float[samples];
                var span = chunk.Span;
                for (var i = 0; i < samples; i++)
                    pcm[i] = (short)(span[i * 2] | (span[i * 2 + 1] << 8)) / 32768f;

                _spotter.AcceptWaveform(pcm.AsSpan(0, samples));
            }
        }
        catch (OperationCanceledException) { /* StopAsync */ }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct = default)
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            if (!IsListening) return;
            IsListening = false;
            cts = _cts; _cts = null;
            loop = _loop; _loop = null;
        }

        cts?.Cancel();
        if (loop is not null)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
        }
        cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _spotter.Dispose();
        await _capture.DisposeAsync().ConfigureAwait(false);
    }
}
