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
    public static async Task<string> RunLocalAsync(
        string modelOnnxPath, string wavPath, string phrase, Action<string>? log = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (!File.Exists(modelOnnxPath))
            return $"no sideloaded model at {modelOnnxPath}\n";

        // The model's own espeak voice, read from the config — so the phonemizer
        // speaks the right language rather than defaulting to English.
        string voice = "en-us";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(modelOnnxPath + ".json"));
            if (doc.RootElement.TryGetProperty("espeak", out var es) && es.TryGetProperty("voice", out var v))
                voice = v.GetString() ?? "en-us";
        }
        catch { /* keep en-us */ }
        log?.Invoke($"voice-under-test: {Path.GetFileName(modelOnnxPath)}  espeak='{voice}'");

        var phonemizer = ItSpeaker.MobilePhonemizerFactory?.Invoke(voice);
        if (phonemizer is null)
            return "on-device phonemizer not wired (ItSpeaker.MobilePhonemizerFactory)\n";

        try
        {
            using var engine = new OnnxTtsEngine(modelOnnxPath, phonemizer);
            var result = await engine.SynthesiseAsync(phrase, ct).ConfigureAwait(false);
            if (result.AudioData.Length == 0)
                return $"engine LOADED on device but produced 0 audio bytes " +
                       $"(phoneme-map miss for voice '{voice}'?) — model ran, phonemes didn't map.\n" +
                       $"elapsed {sw.Elapsed:mm\\:ss}\n";
            WriteWav(wavPath, result.AudioData.Span, result.SampleRate, result.Channels, result.BitsPerSample);
            var len = new FileInfo(wavPath).Length;
            log?.Invoke($"synthesised {len:N0} bytes @ {result.SampleRate} Hz");
            return
                "SYNTHESIS OK — the sideloaded voice ran on the device.\n" +
                $"model : {Path.GetFileName(modelOnnxPath)}\nespeak: {voice}\n" +
                $"wrote {len:N0} bytes ({result.SampleRate} Hz) to {Path.GetFileName(wavPath)} for \"{phrase}\"\n" +
                $"elapsed {sw.Elapsed:mm\\:ss}\n";
        }
        catch (Exception ex)
        {
            return $"sideloaded voice '{voice}' FAILED on device after {sw.Elapsed:mm\\:ss}:\n{ex}\n";
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
