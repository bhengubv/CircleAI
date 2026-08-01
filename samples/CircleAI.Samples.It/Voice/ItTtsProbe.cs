#nullable enable

// ItTtsProbe.cs
//
// NON-INTERACTIVE on-device proof for the TTS ladder (#56). The Talk loop drives
// the same voice, but needs a live microphone and a human saying "hey b" — no
// clean artefact to pull over adb. This runs the synthesis half by itself and
// leaves a WAV (on success) or a precise error (on failure) in the app's files
// dir, so the result is a file, not a screen scrape.
//
// It goes exactly as far as the device allows and reports where it stopped:
//
//   SpeechModelSelector.PlanFor(device, Tts)   → best Piper voice this phone holds
//     → ModelDownloadService.EnsureBundleAsync  → fetch + SHA-verify (~113 MB)
//     → OnnxTtsEngine.EnsureSession             → ONNX Runtime LOADS the voice
//     → IPhonemizer.Phonemize                   → text → IPA  ← the last step
//     → waveform → WAV
//
// On Android the last step (text → IPA) is served OUT-OF-PROCESS by the separate
// espeak G2P app (com.bhengubv.espeakng): espeak-ng is GPL-3.0, so CircleAI never
// links it — it calls that app across a process boundary. When the app is installed
// this probe writes a real WAV; when it is absent OutOfProcessEspeakPhonemizer
// throws a clear reason and the probe reports synthesis blocked (text-only) rather
// than crashing. Either outcome lands in the pulled report.

using System.Diagnostics;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using CircleAI.Voice;

namespace CircleAI.Samples.It.Voice;

/// <summary>Runs on-device TTS synthesis once and returns a pull-able report.</summary>
public static class ItTtsProbe
{
    /// <summary>A fixed phrase — a pangram, so a real synthesis exercises every letter.</summary>
    public const string Phrase = "The quick brown fox jumps over the lazy dog.";

    /// <summary>
    /// Selects + downloads the best-fit voice, loads it through ONNX Runtime, and
    /// tries to synthesise <see cref="Phrase"/> to <paramref name="wavPath"/>.
    /// Returns the report text (the caller also writes it to a .txt). A WAV is
    /// written only when synthesis genuinely succeeds — otherwise the report says
    /// exactly which stage failed, with the on-device exception verbatim.
    /// </summary>
    public static async Task<string> RunAsync(
        string storageDir, string wavPath, Action<string>? log = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // select → download → build engine. TryCreateAsync returns (null, reason)
        // rather than throwing when the chain cannot complete (no voice catalogued,
        // or — on desktop — espeak-ng missing), so a dead phonemizer never presents
        // as a crash.
        var (speaker, status) = await ItSpeaker.TryCreateAsync(storageDir, log, ct).ConfigureAwait(false);
        if (speaker is null)
            return $"voice unavailable before synthesis: {status}\n" +
                   $"(select/download/engine stage) after {sw.Elapsed:mm\\:ss}\n";

        using (speaker)
        {
            try
            {
                await speaker.SpeakToWavAsync(Phrase, wavPath, ct).ConfigureAwait(false);
                var len = new FileInfo(wavPath).Length;
                log?.Invoke($"synthesised {len:N0} bytes");
                return
                    "SYNTHESIS OK — every stage ran on the device.\n" +
                    $"select + download + ONNX-Runtime load + grapheme→phoneme + waveform.\n" +
                    $"wrote {len:N0} bytes to {Path.GetFileName(wavPath)} for \"{Phrase}\"\n" +
                    $"elapsed {sw.Elapsed:mm\\:ss}\n";
            }
            catch (Exception ex)
            {
                // Reaching the catch means TryCreateAsync succeeded AND
                // OnnxTtsEngine.EnsureSession() succeeded — the session load runs
                // before phonemization inside SynthesiseCore, so ONNX Runtime has
                // already loaded the Piper voice on this phone. The failure is
                // therefore isolated to the LAST step, grapheme→phoneme. Record it
                // verbatim: on mobile this is the libespeak-ng DllNotFound, the
                // honest wall for on-device TTS.
                log?.Invoke("synthesis blocked at grapheme→phoneme");
                return
                    "select + download + ONNX-Runtime load: OK — the voice model loaded on the device.\n" +
                    "synthesis: BLOCKED at the last step (grapheme→phoneme).\n" +
                    $"elapsed to the wall: {sw.Elapsed:mm\\:ss}\n\n" +
                    "on-device exception (verbatim):\n" +
                    ex + "\n";
            }
        }
    }

    /// <summary>
    /// Proves a SIDELOADED voice (any language) on the phone: loads
    /// <paramref name="modelOnnxPath"/> through ONNX Runtime and phonemises with the
    /// espeak voice named in its own <c>.onnx.json</c> (e.g. <c>lfn</c> for a kasanoma
    /// African voice) — not the hardcoded en-us. This is how a catalogued African
    /// voice is shown to actually synthesise on-device, per-language.
    /// </summary>
    /// <summary>
    /// ToucanTTS language ids, read out of the checkpoint during export. These four
    /// SA languages have no ready-made voice anywhere — ToucanTTS is the only
    /// permissive model that speaks them.
    /// </summary>
    private static readonly Dictionary<string, long> ToucanLanguageIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zul"] = 7215, ["nso"] = 4491, ["ssw"] = 5611, ["ven"] = 6348
    };

    /// <summary>
    /// Prove the SPLIT ToucanTTS pipeline on the phone: our own
    /// <see cref="NchltPhonemizer"/> → articulatory features → stage A → length
    /// regulation in C# → stage B → vocoder. Three ONNX graphs, ~480 MB total, and
    /// not one line of Python or espeak in the path.
    /// </summary>
    // Sessions are expensive to build and cheap to keep. Rebuilding them per
    // utterance was paying the entire model-load cost every time somebody spoke.
    private static ToucanOnnxTtsEngine? _cachedToucan;
    private static string _cachedToucanKey = "";
    // How many language runs the last utterance was cut into. Reported, because
    // an unswitched English word is audible to a listener and invisible in a log.
    private static int _lastMixedSpans;
    private static OnnxTtsEngine? _cachedSingle;
    private static string _cachedSingleKey = "";
    private static Task? _preload;

    /// <summary>
    /// Build the ToucanTTS sessions in the background so the first spoken line does
    /// not wait for them.
    /// </summary>
    /// <remarks>
    /// Loading these three graphs off the phone's storage takes minutes and no
    /// format or quantisation change moved that materially — it is I/O and
    /// allocation, not compute. Synthesis itself is ~4 s once they are resident, so
    /// the fix is to start loading when the app opens rather than when the user
    /// speaks. Fire-and-forget: a failure here must not break startup, and the
    /// normal path will surface the real error if it recurs.
    /// </remarks>
    /// <summary>
    /// Cache key for a ToucanTTS asset directory, including the identity of the
    /// stage-A file. Assets are sideloaded into a fixed directory, so keying on
    /// directory+language alone would keep a stale engine after they are replaced.
    /// </summary>
    private static string ToucanCacheKey(string assetDir, string language)
        => OnnxSessionFactory.ModelIdentity(
               OnnxSessionFactory.PickModelFile(assetDir, "toucan_stage_a"))
           + "|" + language;

    public static void PreloadToucan(string assetDir, string nchltDataDir, string language)
    {
        if (_preload is not null) return;
        _preload = Task.Run(() =>
        {
            try
            {
                if (!ToucanLanguageIds.TryGetValue(language, out var langId)) return;
                var key = ToucanCacheKey(assetDir, language);
                if (_cachedToucan is not null && _cachedToucanKey == key) return;

                var phonemizer = NchltPhonemizer.ForLanguage(nchltDataDir, language);
                var engine = ToucanOnnxTtsEngine.FromDirectory(assetDir, language, langId, phonemizer);
                // Sessions are created lazily, so synthesise once to force the load.
                engine.SynthesiseAsync("a").GetAwaiter().GetResult();
                _cachedToucan = engine;
                _cachedToucanKey = key;
            }
            catch { /* startup must not fail because a voice could not warm */ }
        });
    }

    public static async Task<string> RunToucanAsync(
        string assetDir, string nchltDataDir, string language, string wavPath, string phrase,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (!ToucanLanguageIds.TryGetValue(language, out var langId))
            return $"no ToucanTTS language id for '{language}'\n";

        try
        {
            log?.Invoke($"toucan: {language} (id {langId}) — NchltPhonemizer + 3-stage ONNX");
            var phonemizer = NchltPhonemizer.ForLanguage(nchltDataDir, language);
            var phones = phonemizer.Phonemize(phrase);
            log?.Invoke($"phones: {string.Join(' ', phones)} ({phones.Count})");

            var key = ToucanCacheKey(assetDir, language);
            if (_cachedToucan is null || _cachedToucanKey != key)
            {
                _cachedToucan?.Dispose();
                _cachedToucan = ToucanOnnxTtsEngine.FromDirectory(assetDir, language, langId, phonemizer);
                _cachedToucanKey = key;
                log?.Invoke("engine: cold (building sessions)");
            }
            else
            {
                log?.Invoke("engine: WARM (sessions reused)");
            }

            var engine = _cachedToucan;
            var result = await engine.SynthesiseAsync(phrase, ct).ConfigureAwait(false);

            if (result.AudioData.Length == 0)
                return $"ToucanTTS loaded on device but produced 0 bytes (skipped {engine.LastSkippedPhoneCount} phones)\n" +
                       $"elapsed {sw.Elapsed:mm\\:ss}\n";

            WriteWav(wavPath, result.AudioData.Span, result.SampleRate, result.Channels, result.BitsPerSample);
            var len = new FileInfo(wavPath).Length;
            return
                "SYNTHESIS OK — ToucanTTS ran on the device.\n" +
                $"lang  : {language} (ToucanTTS id {langId})\n" +
                $"g2p   : NchltPhonemizer (ours, CC-BY) — no espeak, no Python\n" +
                $"frames: {engine.LastFrameCount}  skippedPhones: {engine.LastSkippedPhoneCount}\n" +
                $"timing: {engine.LastTimingSummary}\n" +
                $"wrote {len:N0} bytes ({result.SampleRate} Hz) for \"{phrase}\"\n" +
                $"elapsed {sw.Elapsed:mm\\:ss}\n";
        }
        catch (Exception ex)
        {
            return $"ToucanTTS '{language}' FAILED on device after {sw.Elapsed:mm\\:ss}:\n{ex}\n";
        }
    }

    /// <param name="langIdOverride">
    /// Language to speak, when the caller is walking several in one session rather
    /// than reading <c>langid.txt</c>. Speaking eleven languages by restarting the
    /// app between each one keeps killing a warm engine — and, from the phone in
    /// somebody's hand, looks exactly like a crash loop. Passing the id lets one
    /// live session say them all.
    /// </param>
    /// <summary>
    /// Proves the CATALOGUE end to end for one language: select a voice by tag,
    /// download it from wherever the catalogue says it lives, then speak it.
    /// </summary>
    /// <remarks>
    /// <see cref="RunLocalAsync"/> proves a voice already sitting on the phone,
    /// and <see cref="RunAsync"/> proves the default (English) voice. Neither
    /// answers the question that matters once the voices have been rehoused:
    /// can a phone that has never seen this language fetch it from OUR bucket,
    /// verify the bytes, and speak? Sideloading over adb would prove the engine
    /// while quietly skipping the download — which is the part under test.
    /// </remarks>
    public static async Task<string> RunCataloguedAsync(
        string storageDir, string langTag, string phrase, string wavPath,
        Action<string>? log = null, CancellationToken ct = default,
        long? speakerOverride = null, long? langIdOverrideIn = null, long? foreignSpeakerId = null)
    {
        var sw = Stopwatch.StartNew();
        using var registry = new ModelRegistryService();
        // Through the interface: the language-aware PlanFor is a default interface
        // method, so the concrete type only exposes the language-blind overload —
        // which would silently return the English voice and prove nothing.
        ISpeechModelSelector selector = new SpeechModelSelector(registry);

        var plan = selector.PlanFor(DeviceProbe.Snapshot(), ModelModality.Tts, langTag);
        if (!plan.IsAvailable || plan.Model is null)
            return $"no voice for '{langTag}': {plan.Reason}\n";

        var entry = registry.GetLatestModel(plan.Model.ModelId);
        if (entry?.BundleFiles is null || string.IsNullOrWhiteSpace(entry.Repo))
            return $"'{plan.Model.ModelId}' is not a downloadable bundle\n";

        log?.Invoke($"voice : {entry.Name} for '{langTag}'");
        log?.Invoke($"from  : {entry.Source}:{entry.Repo}");

        var specs = new List<BundleFileSpec>(entry.BundleFiles.Count);
        foreach (var f in entry.BundleFiles)
            specs.Add(new BundleFileSpec(f.Name, f.Sha256, f.SizeBytes));

        string dir;
        try
        {
            using var downloads = new ModelDownloadService(storageDir);
            var lastPct = -1;
            var progress = new Progress<DownloadProgress>(p =>
            {
                var pct = (int)(p.Ratio * 100);
                var notable = p.Phase is not DownloadPhase.Downloading;
                if (!notable && pct < lastPct + 20) return;
                if (!notable) lastPct = pct;
                log?.Invoke("  " + p.Describe());
            });
            dir = await downloads.EnsureBundleAsync(entry.Name, entry.Repo!, entry.Source, specs, progress, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The download is the thing under test, so a failure here is reported
            // verbatim rather than folded into a generic "voice unavailable".
            return $"DOWNLOAD FAILED for {entry.Name} from {entry.Source}:{entry.Repo}\n" +
                   $"after {sw.Elapsed:mm\\:ss}\n\n{ex}\n";
        }

        var onnx = Directory.EnumerateFiles(dir, "*.onnx", SearchOption.AllDirectories).FirstOrDefault();
        if (onnx is null) return $"downloaded, but no .onnx under '{dir}'\n";
        log?.Invoke($"downloaded: {new FileInfo(onnx).Length:N0} bytes in {sw.Elapsed:mm\\:ss}");

        var langId = langIdOverrideIn ?? ResolveLanguageId(dir, langTag);
        if (langId is not null) log?.Invoke($"language id {langId} for '{langTag}'");

        // Hand off to the local path: it already reads the model's own config to
        // decide grapheme / lexicon / Ge'ez / espeak, so nothing is duplicated.
        // The id for English in THIS model, so an embedded English word can be
        // switched to rather than read under the surrounding language.
        var englishId = langId is null ? null : ResolveLanguageId(dir, "en");
        var spoken = await RunLocalAsync(onnx, wavPath, phrase, log, ct, langId, speakerOverride, englishId,
                                         foreignSpeakerId, langTagForRespell: langTag).ConfigureAwait(false);

        // The language id is in the report because its absence is undetectable
        // from the audio unless you speak the language: a multi-lingual model
        // with no id set synthesises perfectly, in the wrong language.
        return $"CATALOGUE → DEVICE, language '{langTag}'\n" +
               $"source   : {entry.Source}:{entry.Repo}\n" +
               $"voice id : {(langId is null ? "single-language model (no id needed)" : langId.ToString())}\n" +
               $"total    : {sw.Elapsed:mm\\:ss} including download\n\n{spoken}";
    }

    /// <summary>
    /// Which language a multi-lingual model should speak, from the map it ships
    /// with. Null when the model serves one language and the input does not exist.
    /// </summary>
    /// <remarks>
    /// A model that speaks eleven languages does not choose between them by which
    /// file you loaded — it takes a language id per utterance, and id 0 is simply
    /// whichever language happened to be first. For the South African VITS voice
    /// that is Afrikaans, a Germanic language, so leaving the id unset made
    /// isiZulu, isiXhosa, Sesotho, Setswana, Tshivenda, siSwati, isiNdebele,
    /// Sepedi and Xitsonga all come out with Afrikaans phonetics. It synthesised
    /// cleanly and reported success every time; nothing but a speaker's ear could
    /// tell. The sideloaded path had a langid.txt beside the model and so was
    /// right by accident — a downloaded bundle has no such file.
    /// </remarks>
    static long? ResolveLanguageId(string modelDir, string langTag)
    {
        var mapPath = Directory.EnumerateFiles(modelDir, "language_ids.json", SearchOption.AllDirectories)
                               .FirstOrDefault();
        if (mapPath is null) return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(mapPath));

            // The catalogue tags languages in ISO 639-1 where one exists; these
            // model maps are keyed by 639-3. Try the tag as-is first, so a model
            // keyed the same way as the catalogue needs no table at all.
            foreach (var key in new[] { langTag, ThreeLetter(langTag) })
            {
                if (key is not null &&
                    doc.RootElement.TryGetProperty(key, out var v) &&
                    v.TryGetInt64(out var id))
                    return id;
            }
        }
        catch { /* a model that ships an unreadable map is treated as single-language */ }
        return null;
    }

    static string? ThreeLetter(string tag) => tag switch
    {
        "af" => "afr", "en" => "eng", "nr" => "nbl", "st" => "sot", "ss" => "ssw",
        "tn" => "tsn", "ts" => "tso", "ve" => "ven", "xh" => "xho", "zu" => "zul",
        _    => null,   // nso and friends already ARE the 639-3 form
    };

    /// <param name="speakerOverride">
    /// Which of the model's voices to speak with. The South African model holds
    /// 130 speakers named only <c>speaker_0</c>…<c>speaker_129</c> — it publishes
    /// no mapping from speaker to language, so which of them sounds native in a
    /// given language cannot be looked up, only heard. Left null, speaker 0 is
    /// used for all eleven languages, which is one person's accent applied to ten
    /// languages that are not theirs.
    /// </param>
    public static async Task<string> RunLocalAsync(
        string modelOnnxPath, string wavPath, string phrase, Action<string>? log = null, CancellationToken ct = default,
        long? langIdOverride = null, long? speakerOverride = null, long? foreignLangId = null,
        long? foreignSpeakerId = null, float? noiseW = null, int sentencesPerUtterance = 1,
        int leadInPads = 0, int leadInSilenceMs = 0, int tailSilenceMs = 0,
        float cadenceRatio = 1.20f, string langTagForRespell = "",
        IPhonemizer? englishPhonemizer = null, float syllableFullness = 1.18f,
        CircleAI.Voice.PersonalRespellings? personal = null)
    {
        var sw = Stopwatch.StartNew();
        if (!File.Exists(modelOnnxPath))
            return $"no sideloaded model at {modelOnnxPath}\n";

        // The model's own espeak voice, read from the config — so the phonemizer
        // speaks the right language rather than defaulting to English. A
        // "text" phoneme_type (MMS and other character-driven VITS voices) means
        // the GRAPHEMES are the tokens: no espeak, no G2P, nothing out-of-process.
        // That is what lets African MMS voices run on a phone where the espeak
        // wall stopped the Piper-style ones.
        string voice = "en-us";
        var graphemeVoice = false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(modelOnnxPath + ".json"));
            if (doc.RootElement.TryGetProperty("espeak", out var es) && es.TryGetProperty("voice", out var v))
                voice = v.GetString() ?? "en-us";
            if (doc.RootElement.TryGetProperty("phoneme_type", out var pt) &&
                string.Equals(pt.GetString(), "text", StringComparison.OrdinalIgnoreCase))
                graphemeVoice = true;
        }
        catch { /* keep en-us */ }
        log?.Invoke(graphemeVoice
            ? $"voice-under-test: {Path.GetFileName(modelOnnxPath)}  grapheme-driven (no espeak needed)"
            : $"voice-under-test: {Path.GetFileName(modelOnnxPath)}  espeak='{voice}'");

        // A lexicon.txt beside the model means the script does not encode sound and
        // the mapping ships as data: Chinese characters carry meaning, not
        // pronunciation, so neither graphemes nor espeak can read them. The usual
        // answer is a Python G2P (pypinyin, jieba) which cannot run here; the
        // sherpa builds ship the whole table instead — 195k entries for Mandarin,
        // 14k for Cantonese — and a table is something the phone can do.
        var lexiconPath = Path.Combine(Path.GetDirectoryName(modelOnnxPath)!, "lexicon.txt");
        var lexicalVoice = File.Exists(lexiconPath);

        // Ethiopic text against a Latin-only vocabulary means the voice was shipped
        // with is_uroman=true: MMS's Amharic and Tigrinya hold 28 and 27 LATIN
        // letters and have never seen an Ethiopic codepoint. Measured on the P30,
        // Amharic lost 43 distinct characters that way. Decide from the two things
        // we can actually see — the script of the text and the contents of the
        // vocabulary — rather than trusting a flag someone has to remember to set.
        var voiceConfig = PiperVoiceConfig.TryLoadForModel(modelOnnxPath);
        var needsRomanising = GeezRomanizer.IsEthiopic(phrase)
                              && voiceConfig is { HasPhonemeMap: true }
                              && !voiceConfig.HasEthiopic;

        IPhonemizer? phonemizer;
        if (needsRomanising)
        {
            var geez = new GeezPhonemizer();
            log?.Invoke($"voice-under-test: {Path.GetFileName(modelOnnxPath)}  Ethiopic → Latin (uroman-style)");
            phonemizer = geez;
        }
        else if (lexicalVoice)
        {
            var lex = LexiconPhonemizer.Load(lexiconPath);
            log?.Invoke($"voice-under-test: {Path.GetFileName(modelOnnxPath)}  lexicon-driven ({lex.EntryCount:N0} entries)");
            phonemizer = lex;
        }
        else
        {
            phonemizer = graphemeVoice
                ? new PassthroughPhonemizer()
                : ItSpeaker.MobilePhonemizerFactory?.Invoke(voice);
        }
        if (phonemizer is null)
            return "on-device phonemizer not wired (ItSpeaker.MobilePhonemizerFactory)\n";

        // A multi-lingual voice (the 11-language SA VITS) picks its language from a
        // langid input, not from which file was loaded. Without this it would
        // silently synthesise every language as language 0.
        long langIdValue = 0, speakerIdValue = 0;
        var langIdFile = Path.Combine(Path.GetDirectoryName(modelOnnxPath)!, "langid.txt");
        if (File.Exists(langIdFile))
        {
            var parts = File.ReadAllText(langIdFile).Trim().Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length > 0) long.TryParse(parts[0], out langIdValue);
            if (parts.Length > 1) long.TryParse(parts[1], out speakerIdValue);
        }

        // An explicit id beats the file, so one warm session can walk several
        // languages without a script rewriting langid.txt and restarting the app
        // between each one.
        if (langIdOverride.HasValue) langIdValue = langIdOverride.Value;
        if (speakerOverride.HasValue) speakerIdValue = speakerOverride.Value;
        log?.Invoke($"langid: {langIdValue}  speaker: {speakerIdValue}");

        try
        {
            // Cached for the same reason the ToucanTTS engine is: building the
            // session is the expensive part, and rebuilding it per utterance was
            // paying the whole model load every time somebody spoke.
            // Key on the FILE, not the path. Voices are sideloaded by overwriting
            // one filename, so a path-only key would keep serving the previous
            // language's session after the model underneath had been replaced.
            var key = OnnxSessionFactory.ModelIdentity(modelOnnxPath)
                      + "|" + (needsRomanising ? "geez" : lexicalVoice ? "lex" : graphemeVoice ? "text" : voice);
            if (_cachedSingle is null || _cachedSingleKey != key)
            {
                _cachedSingle?.Dispose();
                _cachedSingle = new OnnxTtsEngine(modelOnnxPath, phonemizer);
                _cachedSingleKey = key;
                log?.Invoke("engine: cold (building session)");
            }
            else
            {
                log?.Invoke("engine: WARM (session reused)");
            }

            var engine = _cachedSingle;
            // Language/speaker are per-utterance on a multi-lingual voice, so they
            // are set on every call rather than baked in when the engine is built.
            engine.LanguageId = langIdValue;
            engine.SpeakerId = speakerIdValue;

            // Speak sentence by sentence. These voices have no token for a full
            // stop, so without this a paragraph arrives as one unbroken run — and
            // on a phone the whole paragraph must render before any of it plays.
            // The three candidate cures for the dragged first syllable, each
            // switchable so they can be compared by ear rather than argued about.
            if (noiseW is { } nw) engine.NoiseWOverride = nw;
            engine.LeadInPads = leadInPads;
            using var phrased = new PhrasedTtsEngine(engine)
            {
                SentencesPerUtterance = sentencesPerUtterance,
                LeadInSilenceMs = leadInSilenceMs,
                TailSilenceMs = tailSilenceMs,
            };
            if (noiseW is not null || leadInPads > 0 || sentencesPerUtterance > 1)
            {
                var nwText = noiseW?.ToString("0.00") ?? "default";
                log?.Invoke($"tuning: noise_w={nwText} group={sentencesPerUtterance} leadpads={leadInPads}");
            }

            // CODE-SWITCHING. "Igama lami ngu-CircleAI" is isiZulu carrying an
            // English name, and read wholly under the isiZulu id the name comes out
            // mangled — the listener hears the machine fail at a word they know.
            // A multi-lingual model takes one language id per call, so each run of
            // text is synthesised under its own id and the audio joined. Only done
            // when the model HAS a second id to switch to and the text actually
            // mixes; otherwise the single-language path below is untouched.
            // Split when there is ANY way to treat a borrowed word differently:
            // a language to switch to, OR a host language we can respell into.
            //
            // Gating this on the language id alone was a real bug and a silent one.
            // Respelling lives inside this loop, so asking for respelling WITHOUT a
            // switch target produced no split, no respelling, and audio that looked
            // plausible — the word was simply read as written, which is exactly the
            // symptom respelling exists to fix. Nothing failed; it just never ran.
            var canRespell = !string.IsNullOrWhiteSpace(langTagForRespell);
            // The same chain the live voice uses — this person's own spelling, then
            // the language's settled one, then a derivation. Held here rather than
            // rebuilt per span so the probe and the conversation cannot drift apart.
            var respeller = new CircleAI.Voice.Respeller
            {
                HostLanguage      = langTagForRespell,
                Personal          = personal,
                EnglishPhonemizer = englishPhonemizer,
                Log               = log,
            };
            var spans = foreignLangId is null && !canRespell
                ? System.Array.Empty<CircleAI.Voice.LanguageSpan>()
                : CircleAI.Voice.LanguageSpanSplitter.Split(phrase);

            TtsSynthesisResult result;
            if (spans.Count > 1)
            {
                log?.Invoke($"spans: {spans.Count}  respell-host: {(canRespell ? langTagForRespell : "none")}  switch-to: {foreignLangId?.ToString() ?? "none"}");
                _lastMixedSpans = spans.Count;
                var pcm = new List<byte>();
                int rate = 0, channels = 1, bits = 16;
                foreach (var span in spans)
                {
                    if (string.IsNullOrWhiteSpace(span.Text)) continue;

                    // RESPELL FIRST — this decides everything below it.
                    //
                    // The model is grapheme-driven: phoneme_type "text", a
                    // vocabulary of 141 plain letters, no phoneme inventory. The
                    // letters ARE the tokens, so the language id can change accent
                    // and tempo but never what a letter sounds like. "WiFi" under
                    // any language id is read with isiZulu letter values.
                    //
                    // isiZulu already solved this centuries before we tried to
                    // synthesise it: borrowings are respelt — ikhompiyutha,
                    // i-inthanethi, isiteshi. Where we have such a spelling the word
                    // becomes a HOST-language word, spoken by the host voice at host
                    // cadence, and there is no switch left to make seamless.
                    // Settled spellings first, then derive one for anything else.
                    var word = span.Text.Trim();
                    // THIS PERSON'S OWN spelling first, then what the language has
                    // settled, then a derivation — one chain, shared with the live
                    // voice so what the ear learns is what the mouth says.
                    var respelt = span.IsForeign ? respeller.For(word) : null;

                    var spoken = respelt
                        ?? (span.IsForeign
                            // No respelling: fall back to switching language, and at
                            // least split the compound so it is pronounceable.
                            ? CircleAI.Voice.LanguageSpanSplitter.ToSpokenForm(span.Text)
                            : span.Text);

                    // Respelt words are no longer foreign to the voice, so every
                    // foreign-span adjustment stands down for them.
                    var treatAsForeign = span.IsForeign && respelt is null && foreignLangId is not null;
                    if (respelt is not null)
                        log?.Invoke($"respelt \"{span.Text.Trim()}\" as \"{respelt}\"");

                    engine.LanguageId = treatAsForeign ? foreignLangId!.Value : langIdValue;

                    // Speaker switches with the language. Every speaker in this
                    // model recorded exactly one language, so asking an isiZulu
                    // voice for English gets an extrapolation that is audibly not
                    // English — measured: the same words under id 1 and id 10 differ
                    // 19% in duration, so the switch works; the voice has no English.
                    engine.SpeakerId = treatAsForeign && foreignSpeakerId is { } fs
                        ? fs
                        : speakerIdValue;

                    // CADENCE. A bilingual speaker does not change gear mid-sentence;
                    // a borrowed word takes the rhythm of the language it lands in.
                    // Measured here: identical English text runs 3.06 s under the
                    // English id and 3.66 s under isiZulu — 1.20x. Unstretched, an
                    // English word inside isiZulu arrives 20% too fast and reads as
                    // foreign even when pronounced correctly.
                    //
                    // noise_scale is damped too: a speaker with no evidence for the
                    // language is uncertain about the WAVEFORM, and that uncertainty
                    // is heard as breath — speaking through a gust of wind.
                    var words = spoken.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                    var isShort = treatAsForeign && words <= 3;

                    // A RESPELT word needs its syllables given full value.
                    //
                    // isiZulu is syllable-timed: every syllable gets roughly equal
                    // weight. English is stress-timed and swallows the unstressed
                    // ones. Respelling adds syllables — "SMS" becomes e-se-me-se,
                    // four where the English had one beat — and the model, predicting
                    // duration from a word it has never seen, compresses them back
                    // toward the shorter shape. The result is a word whose syllables
                    // are all present in the spelling and not all audible.
                    //
                    // Stretching the span restores the even, unhurried delivery the
                    // language actually uses. It applies only to respelt spans: the
                    // surrounding speech is already native and needs nothing.
                    engine.LengthScaleOverride = treatAsForeign
                        ? cadenceRatio * (isShort ? 1.05f : 1.0f)
                        : respelt is not null ? syllableFullness
                        : null;
                    engine.NoiseWOverride      = isShort ? 0.35f : null;
                    engine.NoiseScaleOverride  = treatAsForeign ? 0.40f : null;

                    var part = await phrased.SynthesiseAsync(spoken, ct).ConfigureAwait(false);
                    pcm.AddRange(part.AudioData.ToArray());
                    rate = part.SampleRate; channels = part.Channels; bits = part.BitsPerSample;
                }
                engine.LanguageId = langIdValue;
                engine.SpeakerId = speakerIdValue;
                engine.LengthScaleOverride = null;
                engine.NoiseWOverride = null;
                engine.NoiseScaleOverride = null;
                result = new TtsSynthesisResult(pcm.ToArray(), rate, channels, bits);
            }
            else
            {
                _lastMixedSpans = 0;
                result = await phrased.SynthesiseAsync(phrase, ct).ConfigureAwait(false);
            }
            if (result.AudioData.Length == 0)
                return $"engine LOADED on device but produced 0 audio bytes " +
                       $"(phoneme-map miss for voice '{voice}'?) — model ran, phonemes didn't map.\n" +
                       $"elapsed {sw.Elapsed:mm\\:ss}\n";
            WriteWav(wavPath, result.AudioData.Span, result.SampleRate, result.Channels, result.BitsPerSample);
            var len = new FileInfo(wavPath).Length;
            log?.Invoke($"synthesised {len:N0} bytes @ {result.SampleRate} Hz");

            // A dropped LETTER is lost speech and must be visible from the device
            // report; dropped punctuation is expected for these voices and is what
            // the phrasing pass compensates for.
            var lost = phrased.LastSkippedSymbols.Where(s => s.Length > 0 && char.IsLetterOrDigit(s[0])).ToList();

            // Approximations were invisible here, and that hid a real defect: Thai
            // rendered 4.3 s of a 15 s paragraph because every vowel sign had been
            // folded off its consonant and filed as "approximate". The report said
            // nothing. A compromise the listener will hear must appear in the report
            // the listener's engineer reads.
            var approx = phrased.LastApproximatedSymbols;

            return
                "SYNTHESIS OK — the sideloaded voice ran on the device.\n" +
                $"model : {Path.GetFileName(modelOnnxPath)}\n" +
                (lexicalVoice
                    ? "g2p   : lexicon (no espeak, no python)\n"
                    : needsRomanising
                    ? "g2p   : Ethiopic -> Latin (uroman-style), then grapheme\n"
                    : graphemeVoice ? "g2p   : grapheme (no espeak)\n" : $"espeak: {voice}\n") +
                $"phrase: {phrased.LastSegmentCount} sentence segment(s)\n" +
                (lost.Count > 0 ? $"LOST  : voice has no token for {string.Join(" ", lost)}\n" : "") +
                (approx.Count > 0 ? $"APPROX: spoken via a near-equivalent, not the true sound: {string.Join(" ", approx)}\n" : "") +
                $"wrote {len:N0} bytes ({result.SampleRate} Hz) to {Path.GetFileName(wavPath)} for \"{phrase}\"\n" +
                $"elapsed {sw.Elapsed:mm\\:ss}\n";
        }
        catch (Exception ex)
        {
            // Concise on screen; the full exception goes to a file beside the
            // voice. Dumping it verbatim fills a phone screen with runtime frames
            // and reads like a crash even when the failure was handled cleanly.
            DeviceDiagnostics.WriteDetail(
                Path.GetDirectoryName(modelOnnxPath)!, $"sideloaded voice '{voice}'", ex);
            return $"sideloaded voice '{voice}' FAILED on device after {sw.Elapsed:mm\\:ss}\n"
                 + DeviceDiagnostics.Summarise(ex);
        }
    }

    private static void WriteWav(string path, ReadOnlySpan<byte> pcm, int sampleRate, int channels, int bits)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        int byteRate = sampleRate * channels * bits / 8;
        short blockAlign = (short)(channels * bits / 8);
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + pcm.Length);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16); w.Write((short)1); w.Write((short)channels);
        w.Write(sampleRate); w.Write(byteRate); w.Write(blockAlign); w.Write((short)bits);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(pcm.Length);
        w.Write(pcm);
    }
}
