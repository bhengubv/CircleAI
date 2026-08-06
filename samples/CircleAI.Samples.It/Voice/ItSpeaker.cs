#nullable enable

// ItSpeaker.cs
//
// Gives IT! a voice, through the REAL SDK path — the sample's proof that the
// speech ladder works end to end, not just in a tool:
//
//   SpeechModelSelector.BestFor(device, Tts)   ← pick a de-Googled voice by modality
//     → registry entry (Repo / Source=HuggingFace / pinned BundleFiles)
//     → ModelDownloadService.EnsureBundleAsync  ← fetch + SHA-verify from HF
//     → OnnxTtsEngine + EspeakPhonemizer         ← text → IPA → speech (now real)
//     → a playable WAV
//
// Lives under Voice/ ON PURPOSE: the console sample's default recursive glob
// compiles it, but the Android head's non-recursive `*.cs` glob does not — so
// the phone APK stays free of ONNX Runtime until Android voice is a deliberate,
// separate build.
//
// Uses ModelDownloadService directly rather than BundleModelLoader: the loader
// assumes MNN's config.json anchor and would reject a Piper bundle (which has no
// config.json). That loader-is-MNN-shaped limitation is recorded in
// capabilities.json; the download service itself is modality-agnostic.

using System.Diagnostics;
using System.Text;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using CircleAI.Voice;

namespace CircleAI.Samples.It.Voice;

/// <summary>Acquires a de-Googled TTS voice and speaks text to a WAV file.</summary>
public sealed class ItSpeaker : IDisposable
{
    private readonly OnnxTtsEngine _engine;

    private ItSpeaker(OnnxTtsEngine engine) => _engine = engine;

    /// <summary>
    /// The loaded TTS engine, so a caller can hand it to a
    /// <see cref="VoiceLoop"/> and speak straight to the device speaker instead
    /// of via a WAV file. Same instance — the voice model is loaded once.
    /// </summary>
    public ITtsEngine Engine => _engine;

    /// <summary>
    /// Answers the next utterances in <paramref name="languageCode"/>.
    /// </summary>
    /// <remarks>
    /// Set from the transcriber's detected language, so a question asked in
    /// isiZulu is answered in isiZulu by the same speaker. Unknown codes leave the
    /// voice where it was rather than guessing.
    /// </remarks>
    public void SpeakLanguage(string? languageCode)
        => _engine.LanguageId = LanguageIdFor(languageCode);

    /// <summary>
    /// The same voice, but respelling borrowings for <paramref name="hostLanguage"/>
    /// on the way out.
    /// </summary>
    /// <remarks>
    /// This is what makes learning worth anything. Without it the respelling chain
    /// only ever ran inside the test probe, so a person could teach their phone how
    /// they say a word and the conversation would go on saying it the old way.
    ///
    /// A language with no respelling table gets the plain engine back rather than a
    /// wrapper that would rewrite nothing — one less layer on the audio path of the
    /// slowest phone we support.
    /// </remarks>
    public ITtsEngine RespellingEngine(
        string hostLanguage,
        PersonalRespellings? personal = null,
        IPhonemizer? englishPhonemizer = null)
    {
        if (!LoanwordRespeller.IsNguniOrSotho(hostLanguage ?? "")) return _engine;

        return new RespellingTtsEngine(_engine, new Respeller
        {
            HostLanguage      = hostLanguage!,
            Personal          = personal,
            EnglishPhonemizer = englishPhonemizer,
        });
    }

    /// <summary>
    /// Mobile-only hook that builds the phonemizer for on-device TTS. The Android
    /// head sets this to the OUT-OF-PROCESS espeak client — CircleAI must not link
    /// GPL espeak-ng in-process. The argument is the voice, e.g. "en-us". Left null
    /// on mobile, TTS is reported unavailable rather than throwing.
    /// </summary>
    public static Func<string, IPhonemizer>? MobilePhonemizerFactory { get; set; }

    // ── WHICH VOICE IT! ACTUALLY SPEAKS IN ──────────────────────────────────
    //
    // THIS WAS DECIDED AND THEN LOST. The 11-language South African model carries
    // 130 speaker embeddings; speaker 129 is the isiZulu voice, and a centroid of
    // all 130 was later appended as speaker 130 — "a speaker who has never
    // existed" — to generalise across languages.
    //
    // None of that reached this file. TryCreateAsync asked the selector for
    // whatever TTS FITS THE DEVICE and ran the first .onnx in the bundle, with no
    // speaker id and no language id. On a 4 GB phone that resolves to an MMS voice
    // — mms-npi, Nepali, 114 MB — so the assistant answered English in a South
    // Asian accent while the greeting screen, which goes through ItTtsProbe and
    // DOES pass ids, sang in eleven languages. Two paths, one of them voiceless.
    //
    // The engine has always had the inputs (sid / langid). They were simply never
    // set here.

    /// <summary>The multi-speaker, multi-lingual South African voice.</summary>
    private const string PreferredVoice = "Vits-11ZA";

    /// <summary>
    /// Speaker to answer as. 129 is the isiZulu voice that ships in the bucket.
    /// </summary>
    /// <remarks>
    /// The centroid is speaker 130, but it lives in a LOCALLY MODIFIED model with
    /// 131 rows that was side-loaded and never published — the bucket copy stops at
    /// 129, so asking for 130 here would index past the end of the table. Publish
    /// the 131-row model and this becomes a one-line change.
    /// </remarks>
    public static long PreferredSpeakerId { get; set; } = 129;

    /// <summary>
    /// Language to answer in: 0=afr 1=eng 2=nbl 3=nso 4=sot 5=ssw 6=tsn 7=tso
    /// 8=ven 9=xho 10=zul.
    /// </summary>
    /// <remarks>
    /// The DEFAULT only. A turn answers in the language it was asked in — see
    /// <see cref="LanguageIdFor"/> — because being spoken to in isiZulu and
    /// answered in English is the rudest thing a multilingual assistant can do.
    /// </remarks>
    public static long PreferredLanguageId { get; set; } = 1;

    /// <summary>
    /// The eleven languages this voice speaks, by the codes a transcriber reports.
    /// </summary>
    /// <remarks>
    /// Whisper returns ISO 639-1 where it has one ("zu", "af", "xh") and 639-3
    /// otherwise, so both are accepted for each language. "und" — whisper's own
    /// marker for "could not tell" — deliberately has no entry and falls back
    /// rather than guessing at somebody's language.
    /// </remarks>
    private static readonly Dictionary<string, long> LanguageIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["af"]  = 0,  ["afr"] = 0,
        ["en"]  = 1,  ["eng"] = 1,
        ["nr"]  = 2,  ["nbl"] = 2,
        ["nso"] = 3,  ["ns"]  = 3,
        ["st"]  = 4,  ["sot"] = 4,
        ["ss"]  = 5,  ["ssw"] = 5,
        ["tn"]  = 6,  ["tsn"] = 6,
        ["ts"]  = 7,  ["tso"] = 7,
        ["ve"]  = 8,  ["ven"] = 8,
        ["xh"]  = 9,  ["xho"] = 9,
        ["zu"]  = 10, ["zul"] = 10,
    };

    /// <summary>
    /// The voice id for a spoken language code, or the default when it is unknown.
    /// </summary>
    /// <remarks>
    /// ANSWER IN THE LANGUAGE YOU WERE ASKED IN. The transcriber already detects
    /// this — TranscriptionResult carries LanguageCode and whisper runs in "auto" —
    /// and the value was simply thrown away, so every reply came out in whatever
    /// langid happened to be pinned.
    /// </remarks>
    public static long LanguageIdFor(string? languageCode)
        => !string.IsNullOrWhiteSpace(languageCode)
           && LanguageIds.TryGetValue(languageCode.Trim(), out var id)
            ? id
            : PreferredLanguageId;

    /// <summary>The language's name, for telling the model which to reply in.</summary>
    /// <remarks>
    /// Names rather than codes, because a small model follows "Reply only in
    /// isiZulu" far more reliably than "Reply only in zu". Returns null when the
    /// language is unknown or already English, so an English turn is not burdened
    /// with a pointless instruction.
    /// </remarks>
    public static string? NameForLanguage(string? languageCode) =>
        LanguageIdFor(languageCode) switch
        {
            0  => "Afrikaans",
            2  => "isiNdebele",
            3  => "Sepedi",
            4  => "Sesotho",
            5  => "siSwati",
            6  => "Setswana",
            7  => "Xitsonga",
            8  => "Tshivenda",
            9  => "isiXhosa",
            10 => "isiZulu",
            _  => null,          // English, or not one of the eleven
        };

    /// <summary>
    /// Selects the best TTS voice the device can hold, downloads it (first run),
    /// and wires the synthesis engine. Returns null with a reason when the chain
    /// cannot be completed (no voice catalogued, or espeak-ng absent) — the
    /// caller degrades to text-only rather than crashing.
    /// </summary>
    public static async Task<(ItSpeaker? speaker, string status)> TryCreateAsync(
        string storageDir, Action<string>? log = null, CancellationToken ct = default)
    {
        using var registry = new ModelRegistryService();
        var selector = new SpeechModelSelector(registry);

        var probe = DeviceProbe.Snapshot();

        // Plan, not a nullable pick — see ItListener. TTS likewise has no
        // non-model fallback (the de-Googled rule rules out the platform engine),
        // so unavailable means silent, and the plan says why in one sentence.
        // ASK FOR THE VOICE BY NAME FIRST. The selector answers "what TTS fits this
        // device", which is the right question for a phone with no chosen voice and
        // the wrong one for a product that HAS chosen. Fit alone put a Nepali voice
        // in an English assistant's mouth.
        string modelId;
        string why;
        if (registry.GetLatestModel(PreferredVoice) is not null)
        {
            modelId = PreferredVoice;
            why     = "chosen";
        }
        else
        {
            // Not catalogued on this build — fall back to device fit rather than
            // going mute, and say so, because the accent will not be the intended one.
            var plan = selector.PlanFor(probe, ModelModality.Tts);
            if (!plan.IsAvailable || plan.Model is null) return (null, plan.Reason);
            modelId = plan.Model.ModelId;
            why     = $"best fit — '{PreferredVoice}' is not in the registry";
        }

        // PHONEMIZER CHOICE IS PLATFORM-DEPENDENT.
        // Desktop shells out to the espeak-ng *executable* (already out-of-process).
        // On mobile there is no executable to launch, and espeak-ng is GPL-3.0 so it
        // must not be linked in-process either — CircleAI is permissive-licensed. So
        // mobile G2P crosses to a SEPARATE espeak app; the Android head wires that
        // out-of-process client into MobilePhonemizerFactory.
        var onMobile = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();
        string? espeak = null;
        // Checked but NOT fatal yet: a grapheme-driven voice needs no phonemizer at
        // all, and failing here would silence a phone that was perfectly able to
        // speak. The decision moves to below, once the model's own config has said
        // which alphabet it wants.
        string? phonemizerProblem = null;
        if (onMobile)
        {
            if (MobilePhonemizerFactory is null)
                phonemizerProblem = "on-device phonemizer not wired — set ItSpeaker.MobilePhonemizerFactory (the out-of-process espeak G2P client)";
        }
        else
        {
            espeak = ResolveEspeak();
            // Fail fast on the one dependency this needs, with an actionable
            // message — a silent text-only fallback would hide why IT! never spoke.
            if (espeak is null && !EspeakOnPath())
                return (null, "espeak-ng not found (install it or add to PATH) — TTS needs it for arbitrary text");
        }

        var entry = registry.GetLatestModel(modelId);
        if (entry is null)
            return (null, $"registry has no entry for '{modelId}'");

        if (entry.BundleFiles is null || string.IsNullOrWhiteSpace(entry.Repo))
            return (null, $"'{entry.Name}' is not a downloadable bundle");

        log?.Invoke($"voice   : {entry.Name} ({why}) from {entry.Source}:{entry.Repo}");

        var specs = new List<BundleFileSpec>(entry.BundleFiles.Count);
        foreach (var f in entry.BundleFiles)
            specs.Add(new BundleFileSpec(f.Name, f.Sha256, f.SizeBytes));

        using var downloads = new ModelDownloadService(storageDir);
        // See ItListener — MB / rate / ETA / file N of M / phase, not a bare %.
        var lastPct = -1;
        var progress = new Progress<DownloadProgress>(p =>
        {
            var pct = (int)(p.Ratio * 100);
            var notable = p.Phase is not DownloadPhase.Downloading;
            if (!notable && pct < lastPct + 5) return;
            if (!notable) lastPct = pct;
            log?.Invoke($"  {p.Describe()}");
        });

        var dir = await downloads.EnsureBundleAsync(entry.Name, entry.Repo!, entry.Source, specs, progress, ct)
            .ConfigureAwait(false);

        // The Piper bundle nests the model (en/en_US/.../*.onnx); find it.
        var onnx = Directory.EnumerateFiles(dir, "*.onnx", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (onnx is null)
            return (null, $"no .onnx found under '{dir}'");

        // THE FRONT END MUST MATCH THE MODEL, and this one did not.
        //
        // The SA voice is GRAPHEME-driven — its sidecar says phoneme_type: "text",
        // meaning the characters ARE the tokens. Running espeak first turns "hello"
        // into IPA, and IPA symbols are mostly absent from that model's map, so they
        // are dropped or mismapped and the output stops sounding like the language
        // it was asked for. Feeding a text model phonemes is not a worse accent; it
        // is the wrong alphabet.
        //
        // So the config decides. Espeak voices get espeak; text voices get the
        // passthrough, which is what OnnxTtsEngine already defaults to when the
        // phonemizer is null.
        var voiceCfg = PiperVoiceConfig.TryLoadForModel(onnx);
        var graphemeDriven = string.Equals(voiceCfg?.PhonemeType, "text", StringComparison.OrdinalIgnoreCase);

        // Only an espeak voice can be blocked by a missing phonemizer.
        if (!graphemeDriven && phonemizerProblem is not null)
            return (null, phonemizerProblem);

        IPhonemizer? phonemizer = graphemeDriven
            ? null                                           // characters are the phonemes
            : onMobile
                ? MobilePhonemizerFactory!("en-us")          // out-of-process espeak service
                : new EspeakPhonemizer("en-us", espeak);     // shell to the binary

        var engine = new OnnxTtsEngine(onnx, phonemizer);

        // THE TWO LINES THAT WERE MISSING. Ignored by single-speaker voices, which
        // declare no sid/langid input, so this is safe on the fallback path too.
        engine.SpeakerId  = PreferredSpeakerId;
        engine.LanguageId = PreferredLanguageId;

        log?.Invoke($"engine  : OnnxTtsEngine on {Path.GetFileName(onnx)} " +
                    $"(sid {PreferredSpeakerId}, langid {PreferredLanguageId}, " +
                    $"{(onMobile ? "out-of-process espeak" : espeak ?? "espeak on PATH")})");
        return (new ItSpeaker(engine), "ready");
    }

    /// <summary>Synthesises <paramref name="text"/> and writes a playable WAV.</summary>
    public async Task<string> SpeakToWavAsync(string text, string wavPath, CancellationToken ct = default)
    {
        var result = await _engine.SynthesiseAsync(text, ct).ConfigureAwait(false);
        WriteWav(wavPath, result.AudioData.Span, result.SampleRate, result.Channels, result.BitsPerSample);
        return wavPath;
    }

    public void Dispose() => _engine.Dispose();

    // ── helpers ──────────────────────────────────────────────────────────

    private static string? ResolveEspeak()
    {
        foreach (var p in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "eSpeak NG", "espeak-ng.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "eSpeak NG", "espeak-ng.exe"),
        })
            if (File.Exists(p)) return p;
        return null;
    }

    private static bool EspeakOnPath()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "espeak-ng", Arguments = "--version",
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            });
            p?.WaitForExit(3000);
            return p is { ExitCode: 0 };
        }
        catch { return false; }
    }

    private static void WriteWav(string path, ReadOnlySpan<byte> pcm, int sampleRate, int channels, int bits)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        int byteRate = sampleRate * channels * bits / 8;
        short blockAlign = (short)(channels * bits / 8);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + pcm.Length);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16); w.Write((short)1); w.Write((short)channels);
        w.Write(sampleRate); w.Write(byteRate); w.Write(blockAlign); w.Write((short)bits);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(pcm.Length);
        w.Write(pcm);
    }
}

