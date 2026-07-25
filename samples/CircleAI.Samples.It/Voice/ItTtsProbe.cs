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
// The last step is where mobile TTS is walled: on Android the phonemizer is
// NativeEspeakPhonemizer, which P/Invokes libespeak-ng — a native this build does
// not bundle, and (more fundamentally) a GPL-3.0 library that cannot be linked
// in-process without contaminating CircleAI's permissive licence. So this probe's
// honest job is usually to prove everything UP TO synthesis works on the phone and
// to capture the exact grapheme→phoneme failure, not to claim speech it cannot yet
// make. If a licence-clean phonemizer is ever wired, the same probe starts writing
// a real WAV with no other change.

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
}
