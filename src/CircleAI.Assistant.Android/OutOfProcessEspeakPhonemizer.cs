#nullable enable
#if IT_VOICE_ANDROID

// OutOfProcessEspeakPhonemizer.cs
//
// CircleAI's mobile G2P: text -> phonemes, obtained from a SEPARATE app
// (com.bhengubv.espeakng) across a process boundary. espeak-ng is GPL-3.0 and must
// never be linked into CircleAI's permissive APK, so instead of P/Invoking it in
// process we ask the espeak G2P service — the same isolation the DOOM APK uses.
//
// The service returns the RAW IPA string espeak emits; we split it exactly the way
// the old in-process NativeEspeakPhonemizer did (PiperVoiceConfig.SplitPhonemeString),
// so the Piper voice receives byte-identical phonemes and loses no quality.

using Android.Content;
using Android.OS;
using CircleAI.Voice;

namespace CircleAI.Samples.It.Mobile;

/// <summary>
/// <see cref="IPhonemizer"/> backed by the out-of-process espeak G2P service. If the
/// service app is not installed, <see cref="Phonemize"/> throws a clear, actionable
/// error — the caller degrades to text-only rather than crashing.
/// </summary>
public sealed class OutOfProcessEspeakPhonemizer : IPhonemizer
{
    private const string ProviderUri    = "content://com.bhengubv.espeakng.g2p";
    private const string MethodPhonemize = "phonemize";

    private readonly Context _context;
    private readonly string _voice;

    public OutOfProcessEspeakPhonemizer(Context context, string voice = "en-us")
    {
        _context = context;
        _voice = voice;
    }

    public IReadOnlyList<string> Phonemize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        var uri = Android.Net.Uri.Parse(ProviderUri)!;
        var extras = new Bundle();
        extras.PutString("voice", _voice);

        Bundle? reply;
        try
        {
            reply = _context.ContentResolver!.Call(uri, MethodPhonemize, text, extras);
        }
        catch (System.Exception ex)
        {
            throw new InvalidOperationException(
                "espeak G2P service unreachable. Install the eSpeak NG G2P app " +
                "(com.bhengubv.espeakng) — CircleAI keeps espeak out-of-process on purpose.", ex);
        }

        if (reply is null)
            throw new InvalidOperationException(
                "espeak G2P service not installed (com.bhengubv.espeakng). Install it to enable " +
                "on-device TTS; without it CircleAI stays text-only.");

        var err = reply.GetString("error");
        if (!string.IsNullOrEmpty(err))
            throw new InvalidOperationException("espeak G2P service reported: " + err);

        var ipa = reply.GetString("ipa") ?? string.Empty;
        // Identical split to the former in-process path, so Piper gets the same symbols.
        return PiperVoiceConfig.SplitPhonemeString(ipa);
    }
}

#endif
