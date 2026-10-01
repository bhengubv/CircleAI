// ServiceSpeech.cs
//
// The recogniser and the voice, answered over the cross-app link.
//
// THIS IS THE HALF THAT WAS MISSING, AND ITS ABSENCE WAS SILENT. The audio
// transaction has been on the wire since it was added: LinkIpc.TransactAudio, the
// codec, the client end, the 800 KB budget. CircleNeuronLinkService.Speech was
// never assigned, so every transcribe and every speak came back "speech not
// available" - and nobody saw it, because the one client still had its own voice
// stack and never asked.
//
// WHY IT BELONGS HERE AND NOWHERE ELSE. A recogniser is a Whisper model and a
// voice is a VITS graph: hundreds of megabytes each, thirteen to twenty-three
// seconds to build a session on a P30. Fifteen apps on a 3 GB phone cannot each
// hold a copy, which is the entire argument for the split. The client keeps the
// microphone - free, and it is the app somebody is looking at, so it knows when
// the screen went off - and sends the bytes here.
//
// LOADED LAZILY AND KEPT. Building the listener downloads and opens the ASR model;
// doing that per call would put the whole load on the first sentence of every
// conversation. Doing it at process start would pay it even for a client that only
// ever chats. So: on the first audio call, then held.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant.Voice;   // CircleAIListener - the shared ASR model
using CircleAI.Linking;
using CircleAI.Voice;

namespace CircleAI.Assistant.Device;

/// <summary>Speech for the link: transcribe what a client recorded, speak what it asks.</summary>
public sealed class ServiceSpeech : ILinkSpeech, IAsyncDisposable
{
    private const string Tag = "CircleAI.LinkSpeech";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DeviceVoiceHost _voice = new();
    private readonly ISpokenLanguage? _spoken;

    private CircleAIListener? _listener;
    private string _listenerStatus = "the recogniser has not been started yet";

    /// <param name="spoken">
    /// The device's own spoken language, used when a caller does not name one. Null
    /// falls back to English - a client that says nothing gets a sensible default
    /// rather than a refusal.
    /// </param>
    public ServiceSpeech(ISpokenLanguage? spoken = null) => _spoken = spoken;

    /// <inheritdoc />
    public async Task<string> TranscribeAsync(byte[] pcm16, string? language,
                                              CancellationToken ct = default)
    {
        if (pcm16 is null || pcm16.Length == 0) return string.Empty;

        var listener = await ListenerAsync(ct).ConfigureAwait(false);
        if (listener is null) throw new InvalidOperationException(_listenerStatus);

        var tag = language ?? _spoken?.Current;

        // LIFT A QUIET CLIP FIRST, exactly as the in-process path does. A phone held
        // at arm's length produces audio well below what the model was trained on,
        // and the difference between a normalised clip and a raw one is the
        // difference between a transcript and an empty string.
        var audio = pcm16;
        var gain = SpeechGain.Normalise(audio);
        if (gain > 1) VoiceTrace.Write($"link stt: lifted the clip x{gain:0.#} before decoding");

        // TELL IT THE LANGUAGE WHENEVER WE HAVE ONE. Detection from a few seconds on
        // a small model leans towards English, which is how Japanese speech came to
        // be written down as English on a screen that had the language on its own
        // button.
        if (listener.Transcriber is WhisperNetTranscriber primable)
            primable.Vocabulary = SpokenVocabulary.For(tag ?? "en");

        var result = await listener.Transcriber
            .TranscribeAsync(audio, ct, tag).ConfigureAwait(false);

        return Heard(result.Text);
    }

    /// <summary>What was actually said, or nothing.</summary>
    /// <remarks>
    /// WHISPER HAS WORDS FOR SILENCE AND THEY ARE NOT SILENCE. A quiet room comes
    /// back as "[BLANK_AUDIO]", and other no-speech markers in brackets turn up the
    /// same way. Passed through, they are a question: on the P30 a silent 25-second
    /// recording produced "[BLANK_AUDIO]" in the person's own chat bubble and the
    /// model gamely set about answering it.
    /// <para>
    /// EMPTY IS THE HONEST ANSWER and the callers already handle it - an empty
    /// transcript is a RESULT, a quiet room, and ServiceConversation drops the turn
    /// rather than showing an error. Doing this here rather than in one client means
    /// every app on the link gets it.
    /// </para>
    /// </remarks>
    private static string Heard(string? text)
    {
        var said = text?.Trim() ?? string.Empty;
        if (said.Length == 0) return string.Empty;

        // A transcript that is ONLY a bracketed marker is the recogniser telling us it
        // heard nothing worth writing down. Brackets inside real speech are untouched.
        if (said.StartsWith('[') && said.EndsWith(']') && !said.AsSpan(1, said.Length - 2).Contains(']'))
        {
            VoiceTrace.Write($"link stt: no speech ({said})");
            return string.Empty;
        }

        return said;
    }

    /// <inheritdoc />
    public async Task<byte[]> SpeakAsync(string text, string? language,
                                         CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var tag = language ?? _spoken?.Current ?? "en";

        // RENDER, DO NOT PLAY. DeviceVoiceHost.SayAsync would put this out of THIS
        // app's loudspeaker, which is the wrong one: the person is looking at the
        // client, and the client is the process that knows whether its screen is on
        // and when to duck. RenderAsync is the half that stops at the file.
        var wav = await _voice.RenderAsync(tag, text, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(wav) || !File.Exists(wav))
            throw new InvalidOperationException(
                $"the device voice for '{tag}' could not synthesise that");

        try
        {
            // RESAMPLED TO THE WIRE'S FORMAT, NOT THE VOICE'S. A VITS voice renders
            // at 22.05 or 24 kHz; LinkAudioFormat is 16 kHz mono 16-bit, and the
            // client plays exactly what the format says. Sending the voice's native
            // rate and hoping is how audio comes out chipmunked.
            var samples = WavIo.ReadMono(wav, LinkAudioFormat.SampleRate);
            var pcm = WavIo.ToPcm16(samples);

            // ONE TRANSACTION, AND THE LIMIT IS KNOWN BEFORE WE SPEAK. The binder
            // budget is about a megabyte and is shared across the whole process, so
            // an oversized reply does not fail politely - it throws
            // TransactionTooLargeException on some UNRELATED call. Refuse here, in
            // seconds the caller can act on.
            if (!LinkAudio.Fits(pcm.Length, out var refusal))
                throw new InvalidOperationException(refusal);

            return pcm;
        }
        finally
        {
            // A fresh file per call, so nothing else is reading it.
            try { File.Delete(wav); } catch { /* a temp file that outlives us is not a failure */ }
        }
    }

    /// <summary>The ASR listener, built once and kept.</summary>
    /// <remarks>
    /// A FAILED BUILD IS REMEMBERED AS A SENTENCE, not as null. TryCreateAsync
    /// returns the reason it could not select or download a model - "no ASR model
    /// fits this device", a failed download - and that sentence is what the client
    /// should see. Returning a bare null would turn every one of those into the same
    /// unhelpful "speech not available".
    /// </remarks>
    private async Task<CircleAIListener?> ListenerAsync(CancellationToken ct)
    {
        if (_listener is not null) return _listener;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_listener is not null) return _listener;

            var (listener, status) = await CircleAIListener
                .TryCreateAsync(ModelStore.Path, log: line => Android.Util.Log.Info(Tag, line), ct: ct)
                .ConfigureAwait(false);

            _listenerStatus = status;
            _listener = listener;

            Android.Util.Log.Info(Tag, listener is null
                ? "recogniser unavailable: " + status
                : "recogniser ready: " + status);

            return _listener;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_listener is not null) await _listener.DisposeAsync().ConfigureAwait(false);
        _listener = null;
        _gate.Dispose();
    }
}
