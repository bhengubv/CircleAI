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
    /// <summary>Vits-11ZA: the eleven South African languages.</summary>
    private readonly OnnxTtsEngine _sa;

    /// <summary>Piper lessac: English only, and null when it is not available.</summary>
    private readonly OnnxTtsEngine? _en;

    private bool _speakingEnglish;

    private ItSpeaker(OnnxTtsEngine sa, OnnxTtsEngine? alt, string family = "za")
    {
        _sa = sa;
        _en = alt;
        _family = alt is null ? "za" : family;
        _speakingEnglish = alt is not null;
    }

    /// <summary>
    /// Which voice family is resident: "en" or "za".
    /// </summary>
    /// <remarks>
    /// ONE VOICE AT A TIME, AND THE PHONE DECIDED THAT, NOT THE DESIGN. Holding
    /// both was tried and it broke the assistant outright on a P30: with the
    /// language model at ~550 MB, whisper, Vits-11ZA at 32 MB, Piper lessac at
    /// 63 MB and espeak in its own process, the OEM's memory manager started
    /// killing things —
    /// <code>
    ///   Killing com.bhengubv.espeakng (adj 935): iAwareF[LowMem](cch-empty)
    ///   TURN: ... answer 9996 | first sound never
    /// </code>
    /// — and that turn produced no generation at all, not merely no speech. A
    /// second voice that costs the answer is not an upgrade.
    /// <para>
    /// So the caller says which language it is about to speak and gets the voice
    /// for it. Switching family means loading the other one and letting this go,
    /// which costs a load on a language switch and keeps the phone alive.
    /// </para>
    /// </remarks>
    public string Family => _family;

    /// <summary>Which family is actually resident: "en", "ja" or "za".</summary>
    private string _family = "za";

    /// <summary>True when <paramref name="languageCode"/> needs the English voice.</summary>
    public static bool IsEnglish(string? languageCode)
    {
        var c = languageCode?.Trim();
        return !string.IsNullOrEmpty(c) &&
               (c.Equals("en", StringComparison.OrdinalIgnoreCase) ||
                c.StartsWith("en-", StringComparison.OrdinalIgnoreCase) ||
                c.Equals("eng", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the language needs the Japanese voice.</summary>
    public static bool IsJapanese(string? languageCode)
    {
        var c = languageCode?.Trim();
        return !string.IsNullOrEmpty(c) &&
               (c.Equals("ja", StringComparison.OrdinalIgnoreCase) ||
                c.StartsWith("ja-", StringComparison.OrdinalIgnoreCase) ||
                c.Equals("jpn", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The family a language needs, without loading anything.</summary>
    /// <remarks>
    /// THREE FAMILIES, ONE RESIDENT. Vits-11ZA has no kana and no IPA — it takes
    /// characters, which is right for the ten South African languages and cannot
    /// say Japanese at all. Routing Japanese to it does not produce an accent; it
    /// produces silence, because the alphabet guard refuses text the vocabulary
    /// cannot spell. Japanese needs its own voice or none.
    /// </remarks>
    public static string FamilyFor(string? languageCode)
        => IsEnglish(languageCode) ? "en"
         : IsJapanese(languageCode) ? "ja"
         : "za";

    /// <summary>
    /// The engine for the language currently being spoken.
    /// </summary>
    /// <remarks>
    /// ONE VOICE CANNOT DO BOTH JOBS, and pretending otherwise was the defect.
    /// Vits-11ZA is grapheme-driven — its config says <c>phoneme_type: "text"</c>,
    /// so characters ARE the tokens and there is no pronunciation model at all.
    /// That is right for isiZulu, isiXhosa and Sesotho, whose orthography is
    /// phonetically regular, and structurally wrong for English, whose spelling
    /// cannot be recovered from its letters. Measured against the same whisper the
    /// product listens with, on "The capital of France is Paris, and it is best
    /// known for the Eiffel Tower":
    /// <code>
    ///   Piper lessac (espeak G2P, 22 kHz)   WER 0.00
    ///   Windows SAPI (commercial baseline)  WER 0.07
    ///   Vits-11ZA sid 128                   WER 0.17
    /// </code>
    /// <para>
    /// The failures were not spread evenly either — they landed on the content
    /// words. "Paris" came back as bears, theirs, Belles, built, Francis Desk. No
    /// choice of speaker and no tone correction touches that, because the front
    /// end is the problem.
    /// </para>
    /// <para>
    /// So English gets a voice with a real pronunciation model and everything else
    /// keeps the one built for it. Falls back to Vits-11ZA when the English voice
    /// is unavailable — accented English is worse than good English and far better
    /// than silence.
    /// </para>
    /// </remarks>
    public ITtsEngine Engine => _speakingEnglish && _en is not null ? _en : _sa;

    /// <summary>True when the current language is served by the English voice.</summary>
    public bool UsingEnglishVoice => _speakingEnglish && _en is not null;

    /// <summary>
    /// Answers the next utterances in <paramref name="languageCode"/>.
    /// </summary>
    /// <remarks>
    /// Picks the VOICE as well as the language id now. A question asked in isiZulu
    /// is answered in isiZulu by Vits-11ZA; one asked in English is answered by
    /// the English voice. Unknown codes leave both where they were rather than
    /// guessing.
    /// </remarks>
    public void SpeakLanguage(string? languageCode)
    {
        var code = languageCode?.Trim();

        // Only the resident voice can be used. A caller that wants the other
        // family compares Family against FamilyFor and builds a new speaker —
        // this cannot conjure a model that was deliberately not loaded.
        _speakingEnglish = _en is not null;
        _sa.LanguageId = LanguageIdFor(code);

        // NAME THE VOICE THAT IS ACTUALLY LOADED. This line was written when there
        // were two families and hard-coded "Piper lessac" for whichever alternate
        // was resident — so once Japanese was added it reported
        // "speaking: ja via Piper lessac (English)" while VITS-jp was doing the
        // speaking. The routing was right and the log said otherwise, which is the
        // kind of line that sends the next person hunting the wrong bug.
        Report(null, _family switch
        {
            "en" => $"speaking: {code} via {EnglishVoice}",
            "ja" => $"speaking: {code} via {JapaneseVoice} (lexicon, no phonemizer)",
            _    => $"speaking: {code} via {PreferredVoice} langid {_sa.LanguageId}"
                    + (IsEnglish(code) ? " (no English voice — accented)"
                     : IsJapanese(code) ? " (no Japanese voice — cannot say it)"
                     : ""),
        });
    }

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
        // Wraps whichever engine is currently speaking. Respelling only applies to
        // the Nguni and Sotho languages, so in practice this is always the SA
        // voice — but reading the active one keeps the two from drifting apart.
        if (!LoanwordRespeller.IsNguniOrSotho(hostLanguage ?? "")) return Engine;

        return new RespellingTtsEngine(Engine, new Respeller
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
    internal const string PreferredVoice = CircleAI.Samples.It.VoiceNames.Preferred;

    /// <summary>The same voice, quantised to int8.</summary>
    /// <remarks>
    /// SAME VOICE, SAME SPEAKERS, SAME ELEVEN LANGUAGES — a third of the size and
    /// measured at 2.6x the speed. Speech synthesis was the largest single wait
    /// in a spoken turn once the model loads were moved off the critical path,
    /// and it is dominated by the graph itself rather than by anything the code
    /// around it does, so this is the only change that moves it by a multiple.
    /// <para>
    /// Statically quantised, not dynamically. Dynamic quantisation cannot touch
    /// this model: VITS keeps its weights in Conv1d and the decoder is HiFi-GAN,
    /// so the MatMul-only path leaves the file the same size, and including Conv
    /// produces ConvInteger, which ORT's CPU kernel does not implement. The
    /// static QDQ path emits QLinearConv, which it does.
    /// </para>
    /// <para>
    /// PREFERRED ONLY WHEN ALREADY ON DISK, because it is not in the published
    /// bucket yet — asking for it by name would mean trying to download a file
    /// that is not there and going mute rather than merely being slower. Once it
    /// is published this collapses to preferring it outright.
    /// </para>
    /// </remarks>
    private const string FastVoice = "Vits-11ZA-int8";

    /// <summary>The English voice: a real pronunciation model, not spelling.</summary>
    /// <remarks>
    /// Already catalogued, so this is a routing change rather than a new
    /// dependency. See <see cref="TryLoadEnglishAsync"/> for the measurements.
    /// </remarks>
    internal const string EnglishVoice = CircleAI.Samples.It.VoiceNames.English;

    /// <summary>The Japanese voice: Open JTalk labels, not a lexicon and not espeak.</summary>
    /// <remarks>
    /// Already in the catalogue. Measured CER 0.12 — the only Japanese path that
    /// is both measured and shippable. Kokoro needs Python misaki; sherpa's
    /// Kokoro bundle ships no Japanese lexicon and speaks the word "japanese"
    /// aloud; MeloTTS-Japanese has no ONNX export; VOICEVOX forbids
    /// redistributing its voices.
    /// </remarks>
    internal const string JapaneseVoice = "JSUT-VITS";

    /// <summary>
    /// Where a voice bundle copied onto this device would be, if the host knows.
    /// </summary>
    /// <remarks>
    /// Set by the Android head to the app's own external files directory, which
    /// is readable without a storage permission and writable over adb. Null
    /// everywhere else, which simply means nothing is side-loaded.
    /// </remarks>
    public static string? SideloadFolder { get; set; }

    /// <summary>
    /// Speaker to answer as. 128 reads English most clearly of the 130 on offer.
    /// </summary>
    /// <remarks>
    /// CHOSEN BY MEASUREMENT, NOT BY EAR. Every candidate read the same English
    /// sentence and the result was transcribed by the same whisper-tiny the
    /// product listens with, scored as word error rate against the reference. A
    /// voice the recogniser cannot follow is one people struggle with too, and
    /// unlike an opinion it can be re-run.
    /// <para>
    /// FIVE TAKES EACH, BECAUSE ONE IS MEANINGLESS. VITS samples noise on every
    /// run, so the same speaker saying the same sentence differs take to take —
    /// ranked on a single sample, sid 96 looked like the clear winner because one
    /// take happened to land. Averaged, it is mid-pack.
    /// </para>
    /// <code>
    ///   mean WER over 5 takes, "The capital of France is Paris, and it is
    ///   best known for the Eiffel Tower."
    ///
    ///     0.16  sid 128        0.21  sid  96
    ///     0.16  sid 130*       0.35  sid   1
    ///     0.17  sid  32        0.47  sid 129   <- the previous pin
    ///     0.17  sid  69
    ///                                    * reconstructed, see below
    /// </code>
    /// <para>
    /// 128, 32 and 69 are a statistical tie and any of them is a one-line
    /// alternative. 129 — an isiZulu voice being asked to read English — is the
    /// worst of the set at nearly three times the error rate, which is the heavy
    /// accent this was reported as.
    /// </para>
    /// <para>
    /// SPEAKER 130 IS NOT AN OPTION AND THE OLD NOTE HERE OVERSTATED IT. The
    /// bucket model's emb_g.weight is [130, 256], so 130 is out of bounds and the
    /// Gather node fails outright. It can be reconstructed — the centroid is just
    /// the mean of the rows — but the mean of embeddings scattered about the
    /// origin has norm 1.0 against the speakers' 8.2, so it is the ABSENCE of a
    /// speaker rather than an average one. Built and measured: no better than
    /// 129. Not worth a locally modified model that would have to be published.
    /// </para>
    /// </remarks>
    public static long PreferredSpeakerId { get; set; } = 128;

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
    public static string? NameForLanguage(string? languageCode)
        => LanguageGuess.InstructionNameFor(languageCode);

    /// <summary>
    /// Selects the best TTS voice the device can hold, downloads it (first run),
    /// and wires the synthesis engine. Returns null with a reason when the chain
    /// cannot be completed (no voice catalogued, or espeak-ng absent) — the
    /// caller degrades to text-only rather than crashing.
    /// </summary>
    /// <param name="languageCode">
    /// The language about to be spoken, so only that voice is loaded. Null or a
    /// non-English code loads Vits-11ZA; English loads Piper lessac instead.
    /// </param>
    public static async Task<(ItSpeaker? speaker, string status)> TryCreateAsync(
        string storageDir, Action<string>? log = null, CancellationToken ct = default,
        string? languageCode = null)
    {
        // Each step named, because everything from here to the first "voice :"
        // line was invisible and that is exactly where a Japanese turn stalls.
        Report(null, $"speaker: entered TryCreateAsync for '{languageCode ?? "default"}'");

        using var registry = new ModelRegistryService();
        var selector = new SpeechModelSelector(registry);
        Report(null, "speaker: registry open");

        var probe = DeviceProbe.Snapshot();
        Report(null, "speaker: device probed");

        // Plan, not a nullable pick — see ItListener. TTS likewise has no
        // non-model fallback (the de-Googled rule rules out the platform engine),
        // so unavailable means silent, and the plan says why in one sentence.
        // ASK FOR THE VOICE BY NAME FIRST. The selector answers "what TTS fits this
        // device", which is the right question for a phone with no chosen voice and
        // the wrong one for a product that HAS chosen. Fit alone put a Nepali voice
        // in an English assistant's mouth.
        // A side-loaded copy becomes installed BEFORE anything asks what is
        // installed, so the answer is final by the time it is used.
        await TryImportSideloadedAsync(registry, storageDir, FastVoice, log, ct)
            .ConfigureAwait(false);

        string modelId;
        string why;
        if (IsInstalled(registry, storageDir, FastVoice))
        {
            modelId = FastVoice;
            why     = "int8 — a third the size, 2.6x the speed";
        }
        else if (registry.GetLatestModel(PreferredVoice) is not null)
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

        Report(log, $"voice   : {entry.Name} ({why}) from {entry.Source}:{entry.Repo}");

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

        // The tone the speaker choice cost us. Picking sid 128 for clarity moved
        // the spectral centre from 298 Hz to 437 Hz and was heard as tinny; this
        // puts it back to 346 Hz with no measurable loss of intelligibility.
        // Reasoning and numbers in ToneShaper.
        engine.Tone = CircleAI.Voice.ToneShaper.Warm;

        log?.Invoke($"engine  : OnnxTtsEngine on {Path.GetFileName(onnx)} " +
                    $"(sid {PreferredSpeakerId}, langid {PreferredLanguageId}, " +
                    $"{(onMobile ? "out-of-process espeak" : espeak ?? "espeak on PATH")})");

        // "READY" HAS TO MEAN READY. Constructing OnnxTtsEngine opens no file and
        // reads no model — it only remembers a path — so this method returned a
        // speaker that still had its entire model load ahead of it, and the
        // caller's log dutifully reported the voice ready in about two seconds.
        // The load then happened inside the first sentence, where somebody was
        // waiting for sound. Callers start this in parallel with thinking
        // precisely so the loading overlaps something; that only works if the
        // loading actually happens here.
        // THE ENGLISH VOICE INSTEAD OF, NOT ALONGSIDE. English measured 0.00 word
        // error rate against this voice's 0.17, so when the turn is in English it
        // is simply the right model to load — and loading both put the phone into
        // the low-memory killer and cost an entire answer. See Family.
        var wanted = FamilyFor(languageCode);
        OnnxTtsEngine? alt = wanted switch
        {
            "en" => await TryLoadEnglishAsync(registry, storageDir, onMobile, espeak, log, ct)
                        .ConfigureAwait(false),
            // Japanese carries its pronunciation as lexicon.txt beside the model,
            // so it needs no phonemizer at all — see LexiconTokeniser.
            "ja" => await TryLoadLexiconVoiceAsync(registry, storageDir, JapaneseVoice, log, ct)
                        .ConfigureAwait(false),
            _ => null,
        };
        var english = alt;

        if (english is null)
        {
            // Only now is the SA session worth opening. Constructing the engine
            // above touched no file; this is the 32 MB.
            await engine.PrepareAsync(ct).ConfigureAwait(false);
        }
        else
        {
            Report(log, "voice   : SA model not loaded — this turn is English");
        }

        return (new ItSpeaker(engine, english, wanted), "ready");
    }

    /// <summary>The English voice with a real pronunciation model, or null.</summary>
    /// <remarks>
    /// PIPER lessac, chosen because it was already in the catalogue and it
    /// measured perfect — WER 0.00 across five takes against the same whisper the
    /// product listens with, ahead of a commercial engine's 0.07 and the SA
    /// voice's 0.17. It is <c>phoneme_type: "espeak"</c>, so unlike the SA voice
    /// it has an actual grapheme-to-phoneme front end, and it runs at 22 kHz
    /// rather than 16.
    /// <para>
    /// NEEDS A PHONEMIZER, AND THAT IS THE ONE THING THAT CAN STOP IT. On the
    /// desktop that is the espeak-ng binary; on a phone it is the out-of-process
    /// espeak client, because espeak-ng is GPL-3.0 and must not be linked into a
    /// permissively licensed app. Without one this returns null and English falls
    /// back to the SA voice — accented, but speaking.
    /// </para>
    /// </remarks>
    private static async Task<OnnxTtsEngine?> TryLoadEnglishAsync(
        CircleAI.Core.Models.ModelRegistryService registry, string storageDir,
        bool onMobile, string? espeak, Action<string>? log, CancellationToken ct)
    {
        try
        {
            await TryImportSideloadedAsync(registry, storageDir, EnglishVoice, log, ct)
                .ConfigureAwait(false);

            var entry = registry.GetLatestModel(EnglishVoice);
            if (entry?.BundleFiles is null || string.IsNullOrWhiteSpace(entry.Repo))
            {
                Report(log, $"english : '{EnglishVoice}' is not in the catalogue — English stays on the SA voice");
                return null;
            }

            // Only if it is already here. This is a 60 MB fetch and the SA voice
            // can speak English badly in the meantime; a first run should not
            // stall on it, and a metered connection should not be spent on it
            // without being asked.
            if (!IsInstalled(registry, storageDir, EnglishVoice))
            {
                Report(log, $"english : {EnglishVoice} not installed — English stays on the SA voice");
                return null;
            }

            var dir = Path.Combine(storageDir, EnglishVoice);
            var onnx = Directory.EnumerateFiles(dir, "*.onnx", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (onnx is null)
            {
                Report(log, $"english : no .onnx under '{dir}'");
                return null;
            }

            IPhonemizer phonemizer;
            if (onMobile)
            {
                if (MobilePhonemizerFactory is null)
                {
                    Report(log, "english : no on-device phonemizer wired — English stays on the SA voice");
                    return null;
                }
                phonemizer = MobilePhonemizerFactory("en-us");
            }
            else
            {
                if (espeak is null && !EspeakOnPath())
                {
                    Report(log, "english : espeak-ng not found — English stays on the SA voice");
                    return null;
                }
                phonemizer = new EspeakPhonemizer("en-us", espeak);
            }

            var en = new OnnxTtsEngine(onnx, phonemizer);
            await en.PrepareAsync(ct).ConfigureAwait(false);

            Report(log, $"english : {EnglishVoice} on {Path.GetFileName(onnx)} (espeak G2P, WER 0.00)");
            return en;
        }
        catch (Exception ex)
        {
            Report(log, $"english : unavailable — {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>A voice that carries its own lexicon, needing no phonemizer.</summary>
    /// <remarks>
    /// Loaded only when already installed, for the same reason the English voice
    /// is: a first run should not stall on a 122 MB fetch, and the SA voice can
    /// speak in the meantime. OnnxTtsEngine finds lexicon.txt beside the model
    /// on its own, so nothing here has to know the format.
    /// </remarks>
    private static async Task<OnnxTtsEngine?> TryLoadLexiconVoiceAsync(
        CircleAI.Core.Models.ModelRegistryService registry, string storageDir,
        string modelId, Action<string>? log, CancellationToken ct)
    {
        try
        {
            await TryImportSideloadedAsync(registry, storageDir, modelId, log, ct)
                .ConfigureAwait(false);

            if (!IsInstalled(registry, storageDir, modelId))
            {
                Report(log, $"lexicon : {modelId} not installed — falling back");
                return null;
            }

            var dir = Path.Combine(storageDir, modelId);
            var onnx = Directory.EnumerateFiles(dir, "*.onnx", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (onnx is null)
            {
                Report(log, $"lexicon : no .onnx under '{dir}'");
                return null;
            }

            // Null phonemizer deliberately: the lexicon replaces it entirely.
            var en = new OnnxTtsEngine(onnx, null);

            // A SPEAKER INDEX MEANS NOTHING ACROSS MODELS. PreferredSpeakerId is
            // 128 because that speaker was measured best in Vits-11ZA; VITS-jp has
            // 804 speakers and 128 is simply a different person. Left unset, the
            // Japanese voice came out as whoever 128 happens to be — reported from
            // the phone as a bad male voice.
            //
            // 0 is measured: on "こんにちは。フランスの首都はパリです。" it returned
            // こんにちは / フランスの / パリです, where 100, 300, 500 and 803 gave
            // progressively worse mush. Each voice carries its own index or none.
            en.SpeakerId = SpeakerFor(modelId);
            en.LanguageId = 0;
            await en.PrepareAsync(ct).ConfigureAwait(false);
            Report(log, $"lexicon : {modelId} on {Path.GetFileName(onnx)} (no phonemizer)");
            return en;
        }
        catch (Exception ex)
        {
            Report(log, $"lexicon : {modelId} unavailable — {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The speaker index measured best for a given voice.</summary>
    /// <remarks>
    /// ONE TABLE, BECAUSE THE TRAP REPEATS. Four times tonight a model's own
    /// metadata pointed at the wrong speaker: Vits-11ZA shipped 129 and 128 was
    /// better; MeloTTS-zh declared 1 and 0 was right; MeloTTS-en declared 0 and 3
    /// scored four times better; VITS-jp inherited 128 from another model
    /// entirely. A speaker id is meaningful only alongside the model it indexes.
    /// <para>
    /// Any voice added here must be swept across its speakers and measured, not
    /// taken from the metadata.
    /// </para>
    /// </remarks>
    private static long SpeakerFor(string modelId) => modelId switch
    {
        // 350 of 804, swept on TWO axes because one was not enough. Speaker 0 was
        // pinned first on intelligibility alone, from a five-point sample, and the
        // voice people actually preferred was female — an axis nothing measured.
        // Across nineteen speakers, scoring word accuracy and median pitch:
        //
        //   sid 350   CER 0.12   197 Hz    <- both
        //   sid 100   CER 0.18   238 Hz
        //   sid 750   CER 0.18   256 Hz
        //   sid   0   CER 0.29   213 Hz    <- the old pin, mid-pack
        //   sid 803   CER 0.94   155 Hz
        //
        // Pitch is an indicator of register, not a fact about the speaker; it
        // narrowed 804 candidates to nine worth listening to. The choice between
        // those nine is a person's, and 350 leads on the measurable half.
        JapaneseVoice => 350,
        _ => 0,
    };

    /// <summary>True when every file of the bundle is already on disk.</summary>
    /// <remarks>
    /// Asks the CATALOGUE what the bundle contains rather than looking for a
    /// likely-looking .onnx, so a half-finished copy — model present, sidecar
    /// missing — reads as not installed instead of being loaded and then failing
    /// somewhere less obvious.
    /// </remarks>
    private static bool IsInstalled(
        CircleAI.Core.Models.ModelRegistryService registry, string storageDir, string modelId)
    {
        var entry = registry.GetLatestModel(modelId);
        if (entry?.BundleFiles is null || entry.BundleFiles.Count == 0) return false;

        foreach (var f in entry.BundleFiles)
        {
            var path = Path.Combine(
                storageDir, modelId, f.Name.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return false;
        }
        return true;
    }

    /// <summary>Installs a side-loaded bundle, if one is sitting there.</summary>
    /// <remarks>
    /// Verified against the catalogue's published SHA-256 before it is trusted,
    /// by the importer — a voice that arrived over a cable is held to exactly the
    /// standard a downloaded one is. Silent and non-fatal when there is nothing
    /// there, which is the normal case.
    /// </remarks>
    private static async Task TryImportSideloadedAsync(
        CircleAI.Core.Models.ModelRegistryService registry,
        string storageDir, string modelId, Action<string>? log, CancellationToken ct)
    {
        var root = SideloadFolder;
        if (string.IsNullOrWhiteSpace(root)) return;

        var folder = Path.Combine(root, modelId);
        if (!Directory.Exists(folder)) return;

        try
        {
            var importer = new CircleAI.Inference.SideloadedBundleImporter(registry, storageDir);
            var result = await importer.ImportAsync(modelId, folder, ct).ConfigureAwait(false);
            Report(log, $"sideload: {modelId} — {result.Detail} ({result.Files} files verified)");
        }
        catch (Exception ex)
        {
            Report(log, $"sideload: {modelId} skipped — {ex.Message}");
        }
    }

    /// <summary>
    /// Says something the caller may or may not be listening to, and logcat is.
    /// </summary>
    /// <remarks>
    /// WHICH VOICE WAS CHOSEN WENT NOWHERE. Every explanation this class produces
    /// — the voice, why it was picked, whether a side-loaded one was accepted —
    /// went to a caller-supplied callback, and the screen that actually creates
    /// the speaker passes <c>_ => { }</c>. So the one line that would answer "is
    /// it using the fast voice or the slow one" was discarded at the only place
    /// it mattered, and the question could only be answered by reading source.
    /// </remarks>
    private static void Report(Action<string>? log, string line)
    {
        log?.Invoke(line);
        CircleAI.Voice.VoiceTrace.Write(line);
    }

    /// <summary>Synthesises <paramref name="text"/> and writes a playable WAV.</summary>
    public async Task<string> SpeakToWavAsync(string text, string wavPath, CancellationToken ct = default)
    {
        var result = await Engine.SynthesiseAsync(text, ct).ConfigureAwait(false);
        WriteWav(wavPath, result.AudioData.Span, result.SampleRate, result.Channels, result.BitsPerSample);
        return wavPath;
    }

    /// <summary>Frees both voices. Either may be absent; neither may be leaked.</summary>
    public void Dispose()
    {
        _sa.Dispose();
        _en?.Dispose();
    }

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

