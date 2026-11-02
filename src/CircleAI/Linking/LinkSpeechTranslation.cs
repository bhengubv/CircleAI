// LinkSpeechTranslation.cs
//
// SPOKEN WORDS IN, SPOKEN WORDS OUT IN ANOTHER LANGUAGE: THE EARS, THE BRAIN, THE VOICE.
//
// Every piece already lived in the service: the recogniser and the voice behind ILinkSpeech
// (ServiceSpeech), the brain behind the ask queue, and the translation engine that sits on a
// brain. An app that wanted a spoken translation made three round trips over the link - and the
// middle one did not exist, so the app's interpreter could hear and speak and not translate. This
// is the one request that runs all three where the models are, kept portable so the order and
// the failures are tested on a desktop.

using System;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Languages.Translation;

namespace CircleAI.Linking;

/// <summary>Transcribe, translate, speak: the audio link's <see cref="LinkAudioVerb.Translate"/>.</summary>
public static class LinkSpeechTranslation
{
    /// <summary>
    /// What <paramref name="request"/>'s audio says, in its target language, written and spoken.
    /// </summary>
    /// <param name="speech">The ears and the voice.</param>
    /// <param name="engine">The translation engine, on the brain the device chose.</param>
    /// <param name="request">The audio, the language it is in (or null), and the target.</param>
    /// <param name="log">Where a text-free line goes - never what was said.</param>
    /// <param name="ct">Cancels it.</param>
    /// <remarks>
    /// <para>
    /// SILENCE IS A RESULT, as it is for Transcribe: a quiet room comes back with nothing heard
    /// and nothing to say, never as an error that tells somebody the service is broken.
    /// </para>
    /// <para>
    /// A VOICE THAT CANNOT SAY IT DOES NOT LOSE THE TRANSLATION. No voice for the language, or
    /// more speech than one transaction carries: the words come back without the audio, as the
    /// interpreter screen already does - a mute phone still shows the words.
    /// </para>
    /// </remarks>
    public static async Task<LinkAudioReply> TranslateAsync(
        ILinkSpeech speech, ITranslationEngine engine, LinkAudioRequest request, Action<string>? log = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Audio is not { Length: > 0 }) return LinkAudioReply.Failure("No audio was sent.");
        if (string.IsNullOrWhiteSpace(request.Target)) return LinkAudioReply.Failure("There was no language to translate into.");

        var heard = (await speech.TranscribeAsync(request.Audio, request.Language, ct).ConfigureAwait(false))?.Trim() ?? string.Empty;
        if (heard.Length == 0)
        {
            log?.Invoke("translate: nothing was heard");
            return LinkAudioReply.Translated(string.Empty, string.Empty, Array.Empty<byte>());
        }

        var result = await engine.TranslateAsync(
            new TranslationRequest(heard, request.Language ?? string.Empty, request.Target!, TranslationMode.Conversational), ct)
            .ConfigureAwait(false);
        var said = result.TranslatedText?.Trim() ?? string.Empty;
        if (said.Length == 0) return LinkAudioReply.Failure("That came back empty. Try saying it again.");

        byte[] audio;
        try
        {
            audio = await speech.SpeakAsync(said, request.Target, ct).ConfigureAwait(false) ?? Array.Empty<byte>();
            if (!LinkAudio.Fits(audio.Length, out _)) audio = Array.Empty<byte>();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log?.Invoke($"translate: the voice could not say it ({ex.GetType().Name}); the words go back alone");
            audio = Array.Empty<byte>();
        }

        log?.Invoke($"translate: {heard.Length} chars heard, {said.Length} chars back, {audio.Length / (double)LinkAudioFormat.BytesPerSecond:0.#} s spoken");
        return LinkAudioReply.Translated(heard, said, audio);
    }
}
