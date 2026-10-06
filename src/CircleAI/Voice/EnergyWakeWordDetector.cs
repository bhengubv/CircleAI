namespace CircleAI.Voice;

/// <summary>
/// <see cref="IWakeWordDetector"/> implementation that combines energy-based
/// VAD with speech-to-text transcription to detect a configurable wake word
/// phrase. Audio is captured continuously via <see cref="IAudioCapture"/>,
/// short speech segments are transcribed, and when the transcription contains
/// the wake word the <see cref="WakeWordDetected"/> event is fired.
/// </summary>
/// <remarks>
/// <para>
/// This is a practical, dependency-light approach to wake-word detection
/// that reuses the existing <see cref="IVoiceTranscriber"/> infrastructure.
/// For production use with very low latency requirements, consider a
/// dedicated keyword-spotting model.
/// </para>
/// <para>
/// The background listening loop runs on the thread pool and can be
/// started/stopped via <see cref="StartAsync"/> / <see cref="StopAsync"/>.
/// </para>
/// </remarks>
public sealed class EnergyWakeWordDetector : IWakeWordDetector
{
    /// <summary>
    /// Longest speech segment still considered a wake-phrase candidate. Anything
    /// longer is conversation and is dropped WITHOUT transcription — see the
    /// duration gate in the listen loop. Generous enough for "hey b" plus a
    /// little leading/trailing speech.
    /// </summary>
    public double MaxWakePhraseSeconds { get; init; } = 2.5;

    private readonly IAudioCapture _capture;
    private readonly IVoiceTranscriber _transcriber;
    private readonly EnergyVadDetector _vad;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _disposed;

    /// <summary>
    /// Initialise a new energy-based wake-word detector.
    /// </summary>
    /// <param name="capture">
    /// Audio capture source providing PCM 16-bit, 16 kHz mono audio.
    /// </param>
    /// <param name="transcriber">
    /// Voice transcriber used to convert detected speech segments to text.
    /// </param>
    /// <param name="wakeWord">
    /// The phrase to listen for. Matching is case-insensitive and uses
    /// <see cref="string.Contains(string, StringComparison)"/> so that
    /// surrounding words do not prevent detection. Default is <c>"hey b"</c>.
    /// </param>
    /// <param name="energyThreshold">
    /// RMS energy threshold for voice activity detection. See
    /// <see cref="EnergyVadDetector"/> for details.
    /// </param>
    public EnergyWakeWordDetector(
        IAudioCapture capture,
        IVoiceTranscriber transcriber,
        string wakeWord = DefaultWakeWord,
        float energyThreshold = 0.02f)
        : this(capture, transcriber, new[] { wakeWord }, energyThreshold)
    {
    }

    /// <summary>
    /// Listens for ANY phrase in <paramref name="wakeWords"/> — the access list.
    /// See <see cref="IWakeWordDetector.WakeWords"/> for what that does and does
    /// not protect.
    /// </summary>
    /// <param name="wakeWords">
    /// One or more phrases. Blank entries are dropped and duplicates collapsed
    /// (case-insensitively) so a sloppily-built list cannot produce a detector
    /// that matches on an empty string — which would fire on every utterance.
    /// </param>
    public EnergyWakeWordDetector(
        IAudioCapture capture,
        IVoiceTranscriber transcriber,
        IEnumerable<string> wakeWords,
        float energyThreshold = 0.02f)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(transcriber);
        ArgumentNullException.ThrowIfNull(wakeWords);

        var list = wakeWords
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(w => w.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (list.Length == 0)
            throw new ArgumentException(
                "At least one wake phrase is required. An empty list would match nothing " +
                "and present as a dead microphone rather than a misconfiguration.",
                nameof(wakeWords));

        _capture = capture;
        _transcriber = transcriber;
        WakeWords = list;
        WakeWord = list[0];
        _vad = new EnergyVadDetector(energyThreshold, silenceFrames: 10, frameSizeBytes: 640);
    }

    /// <summary>The product default. "Butler" is internal-only and never spoken.</summary>
    public const string DefaultWakeWord = "Hey B!";

    /// <inheritdoc />
    public string WakeWord { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> WakeWords { get; }

    /// <inheritdoc />
    public bool IsListening { get; private set; }

    /// <inheritdoc />
    public event EventHandler<WakeWordDetectedEventArgs>? WakeWordDetected;

    /// <inheritdoc />
    /// <remarks>
    /// Starts a background loop that captures audio, runs VAD, transcribes
    /// detected speech segments, and checks for the wake word. Idempotent:
    /// calling when already listening has no effect.
    /// </remarks>
    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (IsListening) return Task.CompletedTask;

            _cts = new CancellationTokenSource();
            IsListening = true;
            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Cancels the background listening loop and waits for it to complete.
    /// Idempotent: calling when not listening has no effect.
    /// </remarks>
    public async Task StopAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Task? taskToAwait;

        lock (_gate)
        {
            if (!IsListening) return;

            _cts?.Cancel();
            IsListening = false;
            taskToAwait = _listenTask;
        }

        if (taskToAwait is not null)
        {
            try
            {
                await taskToAwait.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelling the listen loop.
            }
        }

        lock (_gate)
        {
            _cts?.Dispose();
            _cts = null;
            _listenTask = null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        // STOP FIRST, THEN MARK DISPOSED — order is load-bearing.
        //
        // StopAsync opens with ObjectDisposedException.ThrowIf(_disposed), so
        // setting the flag before calling it made EVERY DisposeAsync throw
        // ObjectDisposedException. The catch below only covers cancellation, so
        // it escaped to the caller: stopping voice on the phone threw rather
        // than releasing the microphone.
        //
        // It hid because VoiceLoop.StopAsync wraps _ears.StopAsync() in a
        // swallowing catch — the throw only surfaced on a direct dispose, which
        // nothing exercised until there was a test.
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Swallow — we're disposing.
        }

        _disposed = true;
    }

    // ── Private helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Lowercases, drops everything that is not a letter, digit or space, and
    /// collapses runs of whitespace — so "Hey B!" and " hey, b. " compare equal.
    /// </summary>
    /// <remarks>
    /// Deliberately keeps digits: a household may well list "Hey B2". Uses
    /// <see cref="char.IsLetterOrDigit(char)"/> rather than an ASCII range so
    /// non-English phrases survive normalisation instead of being erased.
    /// </remarks>
    public static string Normalise(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var sb = new System.Text.StringBuilder(text.Length);
        var lastWasSpace = true;              // trims the leading space too

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        // A trailing separator became a single space — drop it.
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }

    /// <summary>
    /// Background loop that captures audio, runs VAD, transcribes speech
    /// segments, and fires <see cref="WakeWordDetected"/> when the phrase
    /// is found.
    /// </summary>
    private async Task ListenLoopAsync(CancellationToken ct)
    {
        try
        {
            var audioStream = _capture.CaptureAsync(ct);

            await foreach (var segment in _vad.DetectAsync(audioStream, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();

                if (!segment.IsSpeech || segment.Audio.Length == 0)
                    continue;

                // DURATION GATE — the cheapest battery win available here.
                // A wake phrase ("hey b") is ~1 s. Without this we hand every
                // utterance to the transcriber, so ordinary conversation in the
                // room runs ASR continuously — the thing that makes a
                // transcribe-and-match wake word a battery killer on a cheap
                // phone. Segments longer than the gate cannot be the wake phrase,
                // so skip them without ever waking the model.
                var seconds = segment.Audio.Length /
                              (double)(AudioFormat.Pcm16Mono16k.SampleRate * 2);
                if (seconds > MaxWakePhraseSeconds)
                    continue;

                // Transcribe the speech segment.
                TranscriptionResult result;
                try
                {
                    result = await _transcriber
                        .TranscribeAsync(segment.Audio, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Transcription failed for this segment — skip and keep listening.
                    continue;
                }

                if (string.IsNullOrWhiteSpace(result.Text))
                    continue;

                // Match ANY phrase on the access list, comparing NORMALISED text.
                // A raw Contains would never fire on the product default: the
                // phrase is written "Hey B!" but a transcriber emits "hey b" —
                // no exclamation mark, and often a trailing comma or period. The
                // punctuation is a branding artifact, not something anyone says.
                var heard = Normalise(result.Text);
                var matched = WakeWords.FirstOrDefault(
                    w => heard.Contains(Normalise(w), StringComparison.Ordinal));

                if (matched is not null)
                {
                    WakeWordDetected?.Invoke(this, new WakeWordDetectedEventArgs
                    {
                        // Report the phrase AS CONFIGURED, not as transcribed, so
                        // a host can tell which listed speaker woke it.
                        WakeWord = matched,
                        DetectedAt = DateTimeOffset.UtcNow,
                        Confidence = result.Confidence
                    });
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown — swallow.
        }
        finally
        {
            lock (_gate)
            {
                IsListening = false;
            }
        }
    }
}
