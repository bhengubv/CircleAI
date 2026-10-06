#nullable enable

// CircleAIListener.cs
//
// Gives IT! ears, through the REAL SDK path — the mirror of CircleAISpeaker:
//
//   SpeechModelSelector.BestFor(device, Asr)   ← pick a de-Googled ASR model by modality
//     → registry entry (ggerganov/whisper.cpp, Source=HuggingFace, pinned)
//     → ModelDownloadService.EnsureBundleAsync  ← fetch + SHA-verify ggml-tiny from HF
//     → WhisperNetTranscriber                    ← whisper.cpp via Whisper.net (native lib from NuGet)
//     → text
//
// Under Voice/ for the same reason as CircleAISpeaker: the console's recursive glob
// compiles it, the Android head's non-recursive `*.cs` glob does not — so the
// phone APK stays free of Whisper.net until Android voice is a deliberate build.

using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using CircleAI.Voice;

namespace CircleAI.Assistant.Voice;

/// <summary>Acquires a de-Googled ASR model and transcribes a WAV to text.</summary>
public sealed class CircleAIListener : IAsyncDisposable
{
    private readonly WhisperNetTranscriber _transcriber;

    private CircleAIListener(WhisperNetTranscriber t) => _transcriber = t;

    /// <summary>
    /// The underlying transcriber, so a caller can build a full
    /// <see cref="VoicePipeline"/>/<see cref="VoiceLoop"/> (wake word + VAD +
    /// mic) around the SAME loaded Whisper model instead of loading it twice —
    /// a second copy would be tens of MB of RAM on a phone that has none spare.
    /// </summary>
    public IVoiceTranscriber Transcriber => _transcriber;

    /// <summary>
    /// Selects the best ASR model the device can hold, downloads it (first run),
    /// and wires Whisper. Returns null with a reason when the chain cannot be
    /// completed — the caller degrades rather than crashing.
    /// </summary>
    public static async Task<(CircleAIListener? listener, string status)> TryCreateAsync(
        string storageDir, Action<string>? log = null, CancellationToken ct = default)
    {
        using var registry = new ModelRegistryService();
        var selector = new SpeechModelSelector(registry);

        var probe = DeviceProbe.Snapshot();

        // Ask for the PLAN, not a nullable pick: the selector knows whether ASR
        // can be served at all, and its Reason is the sentence to show the user.
        // ASR has no non-model fallback, so an unavailable plan is genuinely the
        // end of the road here — but the caller learns that from one decision
        // rather than inferring it from a null.
        var plan = selector.PlanFor(probe, ModelModality.Asr);
        if (!plan.IsAvailable || plan.Model is null)
            return (null, plan.Reason);

        var pick = plan.Model;
        var entry = registry.GetLatestModel(pick.ModelId);
        if (entry is null || entry.BundleFiles is null || string.IsNullOrWhiteSpace(entry.Repo))
            return (null, $"'{pick.ModelId}' is not a downloadable bundle");

        log?.Invoke($"ears    : {entry.Name} ({pick.Quality}) from {entry.Source}:{entry.Repo}");

        var specs = new List<BundleFileSpec>(entry.BundleFiles.Count);
        foreach (var f in entry.BundleFiles)
            specs.Add(new BundleFileSpec(f.Name, f.Sha256, f.SizeBytes));

        using var downloads = new ModelDownloadService(storageDir);

        // Rich progress: MB, rate, ETA, file N of M, and phase. The old version
        // printed "download 10%" every 10% and nothing else — no way for a user
        // to tell a slow link from a stalled one.
        var lastPct = -1;
        var progress = new Progress<DownloadProgress>(p =>
        {
            // Always announce a phase change; otherwise throttle to every 5%.
            var pct = (int)(p.Ratio * 100);
            var notable = p.Phase is not DownloadPhase.Downloading;
            if (!notable && pct < lastPct + 5) return;
            if (!notable) lastPct = pct;
            log?.Invoke($"  {p.Describe()}");
        });

        var dir = await downloads.EnsureBundleAsync(entry.Name, entry.Repo!, entry.Source, specs, progress, ct)
            .ConfigureAwait(false);

        var ggml = Directory.EnumerateFiles(dir, "*.bin", SearchOption.AllDirectories).FirstOrDefault();
        if (ggml is null)
            return (null, $"no ggml .bin found under '{dir}'");

        var transcriber = new WhisperNetTranscriber(ggml, "en");
        log?.Invoke($"engine  : WhisperNetTranscriber on {Path.GetFileName(ggml)}");
        return (new CircleAIListener(transcriber), "ready");
    }

    /// <summary>
    /// Builds the wake-word detector the SELECTOR chose for this device, rather
    /// than hard-coding one.
    /// </summary>
    /// <remarks>
    /// Today every device lands on <see cref="EnergyWakeWordDetector"/>, because
    /// no keyword-spotting model is catalogued yet — but that is the selector's
    /// verdict, not a decision baked into this call site. The moment an
    /// openWakeWord entry is catalogued with a real hash, devices that can hold
    /// it get it and this method does not change.
    /// <para>
    /// Returns the reason alongside, so the host can show WHY it is listening the
    /// way it is: a transcribe-and-match wake word costs meaningfully more
    /// battery than a keyword spotter, and that is worth surfacing rather than
    /// hiding.
    /// </para>
    /// </remarks>
    /// <param name="wakeWords">
    /// The access list. Defaults to the product phrase alone; pass more to let
    /// specific people wake it, or a different set to lock others out.
    /// </param>
    public async Task<(IWakeWordDetector detector, string reason)> CreateWakeDetectorAsync(
        IAudioCapture capture,
        IEnumerable<string>? wakeWords = null,
        string? storageDir = null,
        CancellationToken ct = default)
    {
        var phrases = wakeWords?.ToArray() is { Length: > 0 } supplied
            ? supplied
            : new[] { EnergyWakeWordDetector.DefaultWakeWord };

        using var registry = new ModelRegistryService();
        var plan = new SpeechModelSelector(registry)
            .PlanFor(DeviceProbe.Snapshot(), ModelModality.WakeWord);

        // The selector chose a keyword-spotting model — fetch and run it.
        if (plan.Model is not null)
        {
            var entry = registry.GetLatestModel(plan.Model.ModelId);
            if (entry?.BundleFiles is not null && !string.IsNullOrWhiteSpace(entry.Repo))
            {
                // Same directory as everything else - see CircleAISession.
                var dir = storageDir ?? ModelPaths.Default;

                var specs = entry.BundleFiles
                    .Select(f => new BundleFileSpec(f.Name, f.Sha256, f.SizeBytes))
                    .ToList();

                using var downloads = new ModelDownloadService(dir);
                // Cast required: a bare `null` is ambiguous between the
                // IProgress<DownloadProgress> and IProgress<double> overloads.
                var modelDir = await downloads
                    .EnsureBundleAsync(entry.Name, entry.Repo!, entry.Source, specs,
                        (IProgress<DownloadProgress>?)null, ct)
                    .ConfigureAwait(false);

                var onnx = Directory
                    .EnumerateFiles(modelDir, "*.onnx", SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (onnx is not null)
                {
                    // WakeWordFactory decides which runtime this bundle needs by
                    // looking at what is IN it: a three-graph transducer gets the
                    // zipformer spotter, a single graph gets the classifier. That
                    // used to be decided here, by taking the first .onnx found and
                    // handing it to the classifier — which for a transducer is an
                    // arbitrary third of a model that cannot work.
                    //
                    // It also stops the multi-phrase downgrade below from being
                    // needed at all: the classifier scores one phrase, so an access
                    // list had to fall back to transcribe-and-match, at the cost of
                    // running an ASR model continuously. The transducer matches any
                    // number of phrases from text, so several wake words no longer
                    // cost anything.
                    var bundleDir = Path.GetDirectoryName(onnx)!;
                    var engine = WakeWordFactory.EngineFor(bundleDir);

                    if (engine == WakeEngine.ZipformerTransducer)
                    {
                        var probe = DeviceProbe.Snapshot();
                        var calibrationPath = Path.Combine(dir, "..", "wake-calibration.json");
                        var detector = WakeWordFactory.Create(
                            capture,
                            bundleDir,
                            new WakeHostCapabilities(probe.RamTotalBytes, TranscriberAvailable: true),
                            WakeCalibration.Load(Path.GetFullPath(calibrationPath)),
                            _transcriber);

                        var listening = string.Join(", ", detector.WakeWords);
                        return (detector, $"{plan.Reason} — zipformer transducer, listening for {listening}");
                    }

                    // Single-graph classifier: one phrase only. Rather than accept
                    // an access list and quietly match just the first — access
                    // control in appearance only — fall back to the transcribing
                    // detector, which really does distinguish phrases, and say why.
                    if (phrases.Length > 1)
                        return (new EnergyWakeWordDetector(capture, _transcriber, phrases),
                                $"{plan.Reason} — using transcribe-and-match instead of the KWS model: " +
                                $"{phrases.Length} wake phrases are configured and a KWS model scores only one");

                    return (new KwsWakeWordDetector(capture, new KwsConfig(onnx, phrases[0])),
                            plan.Reason);
                }
            }

            // Catalogued but unusable. Fall back rather than go deaf — but SAY so,
            // because a silent downgrade is how "we ship a keyword spotter"
            // survives as a claim long after it stopped being true.
            return (new EnergyWakeWordDetector(capture, _transcriber, phrases),
                    $"{plan.Reason} — WARNING: wake model '{plan.Model.ModelId}' could not be " +
                    "prepared; fell back to transcribe-and-match");
        }

        return (new EnergyWakeWordDetector(capture, _transcriber, phrases), plan.Reason);
    }

    /// <summary>Transcribes an audio file to text.</summary>
    /// <remarks>
    /// THE THIRD PRIVATE WAV READER LIVED HERE. This class carried its own RIFF
    /// walker, downmix and resampler - beside WavIo, which had all three and
    /// four more sample formats, and beside a fourth copy in tools/stt-hear.
    /// Three owners of one fact, and this one was the most limited: it threw on
    /// anything that was not 16-bit, so a 24-bit recording failed here while the
    /// same file read fine everywhere else.
    /// <para>
    /// It also read the WHOLE file into one buffer and one decode. An hour of
    /// audio is 230 MB of float samples on a phone that is already holding a
    /// model, and not one word reached the caller until the last one was
    /// decoded. <see cref="Transcribing.FileAsync"/> chunks it.
    /// </para>
    /// </remarks>
    public async Task<string> HearAsync(string wavPath, CancellationToken ct = default)
        => (await TranscribeFileAsync(wavPath, ct: ct).ConfigureAwait(false)).Text;

    /// <summary>
    /// Transcribes an audio file and keeps everything the engine reported.
    /// </summary>
    /// <param name="path">The recording. WAV needs no decoder.</param>
    /// <param name="decoder">
    /// Reads anything that is not a WAV (Android: MediaExtractor + MediaCodec).
    /// <c>null</c> means WAV only, and a voice memo then comes back as a clear
    /// sentence about the build rather than a parse error about RIFF headers.
    /// </param>
    /// <param name="language">BCP-47 code, or null to detect.</param>
    /// <param name="progress">Fraction done, 0 to 1, after each chunk.</param>
    /// <param name="ct">Cancels between chunks.</param>
    /// <remarks>
    /// SEPARATE FROM <see cref="HearAsync"/> BECAUSE HearAsync THREW MOST OF IT
    /// AWAY. It returned result.Text and discarded the timings, the detected
    /// language and the confidence - so a caller wanting subtitles, or wanting
    /// to know whether to trust the transcript, had no way to ask.
    /// </remarks>
    public Task<TranscriptionResult> TranscribeFileAsync(
        string path,
        IAudioDecoder? decoder = null,
        string? language = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
        => Transcribing.FileAsync(_transcriber, path, decoder, language, progress, ct);

    public ValueTask DisposeAsync() => _transcriber.DisposeAsync();

}
