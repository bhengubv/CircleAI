#nullable enable

// IPhonemizer.cs
//
// Text → phonemes, the front half of on-device TTS. Kept as a seam because the
// production path (espeak-ng) is a native dependency that is not present on
// every host or device, whereas the ONNX synthesis half is pure managed code
// that works everywhere. Separating them means the engine can be tested and run
// with a phonemizer that needs no native library, and the real one dropped in
// where it is available.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CircleAI.Voice;

/// <summary>Converts text into the phoneme symbols a voice model expects.</summary>
public interface IPhonemizer
{
    /// <summary>
    /// The phoneme symbols for <paramref name="text"/>, in order. Each element
    /// is one symbol to look up in the voice's phoneme→id map.
    /// </summary>
    IReadOnlyList<string> Phonemize(string text);
}

/// <summary>
/// Treats the input as ALREADY being phonemes — splits it into symbols by
/// Unicode codepoint. For callers that hold IPA already, for grapheme
/// (<c>phoneme_type: "text"</c>) voices where characters are the phonemes, and
/// for tests that must run without a native phonemizer.
/// </summary>
public sealed class PassthroughPhonemizer : IPhonemizer
{
    public IReadOnlyList<string> Phonemize(string text)
        => string.IsNullOrEmpty(text)
            ? Array.Empty<string>()
            : PiperVoiceConfig.SplitPhonemeString(text);
}

/// <summary>
/// Production phonemizer: shells out to <c>espeak-ng</c> for IPA. espeak-ng is
/// how Piper's <c>phoneme_type: "espeak"</c> voices were trained, so this is the
/// correct front end for them.
/// </summary>
/// <remarks>
/// Requires the <c>espeak-ng</c> binary on PATH (or an explicit path). When it
/// is absent, <see cref="Phonemize"/> throws a clear, actionable error rather
/// than silently returning nothing — a silent empty result would present as
/// "the assistant went mute," the same class of invisible failure this codebase
/// has been bitten by. On Android/iOS the espeak-ng library must be bundled and
/// this class replaced with a P/Invoke variant.
/// </remarks>
public sealed class EspeakPhonemizer : IPhonemizer
{
    private readonly string _exe;
    private readonly string _voice;

    public EspeakPhonemizer(string voice = "en-us", string? espeakExecutable = null)
    {
        _voice = voice;
        _exe = espeakExecutable ?? "espeak-ng";
    }

    public IReadOnlyList<string> Phonemize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        // --ipa=3 prints IPA with no separators; -q suppresses audio; --sep="" keeps
        // phonemes adjacent so the model's map lookup sees individual symbols.
        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add(_voice);
        psi.ArgumentList.Add("--ipa=3");

        // THE TEXT GOES IN ON STDIN, NOT AS AN ARGUMENT.
        //
        // espeak-ng.exe reads argv through the ANSI code page on Windows, so
        // Devanagari, Cyrillic, Hangul, Bengali, Sinhala and Arabic script never
        // reach it - and it exits 0 with EMPTY output rather than failing, which
        // is the silent kind. Passed as an argument, Hindi, Russian, Korean,
        // Urdu, Bengali and Sinhala all produced nothing at all; fed on stdin as
        // UTF-8, all six phonemise correctly. Latin script survives either way,
        // which is precisely why this hid - every language anyone spot-checked
        // in English or French worked.
        psi.RedirectStandardInput = true;
        psi.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        Process proc;
        try
        {
            proc = Process.Start(psi)
                   ?? throw new InvalidOperationException("espeak-ng did not start.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"espeak-ng phonemizer unavailable ('{_exe}' not found or failed to launch). " +
                "Install espeak-ng and put it on PATH, pass its full path, or supply a " +
                "different IPhonemizer. On mobile, bundle espeak-ng and use a P/Invoke phonemizer.",
                ex);
        }

        proc.StandardInput.Write(text);
        proc.StandardInput.Close();

        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        // espeak emits stress marks (ˈ ˌ), length (ː) and IPA letters — exactly
        // the symbols in Piper's phoneme_id_map. Split by codepoint.
        var cleaned = stdout.Replace("\r", "").Replace("\n", " ").Trim();
        // "(en)hello(ko)" - espeak annotates language switches when the text is
        // not in the voice's own language. They are not phonemes; left in, the
        // letters inside them get mapped and spoken aloud.
        cleaned = Regex.Replace(cleaned, @"\([^)]*\)", "").Trim();

        return PiperVoiceConfig.SplitPhonemeString(cleaned);
    }
}
