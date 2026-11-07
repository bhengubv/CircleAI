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
using System.Linq;
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

    /// <summary>The ears that work while somebody is talking, by the folder they are in; empty is none.</summary>
    public static string LiveEars { get; set; } = "tiny-streaming-en";

    /// <summary>Threads the live ears work on.</summary>
    /// <remarks>
    /// FOUR, MEASURED ON THE P30 ON 2026-11-06: twelve turns averaged 2,54 s on four and 2,70 s on two. The
    /// last words are what everybody is waiting for, and the brain stands aside while they are written.
    /// </remarks>
    private const int LiveThreads = 4;

    private readonly object _liveGate = new();
    private MoonshineTranscriber? _live;
    private string? _liveOpened;

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

        // SWITCHED OFF MEANS OFF (Step 4): building the listener fetches the ears when they are
        // missing, and the person said not to.
        if (CircleAI.Core.ServicePreferences.Current?.AbilitiesOff.Contains(ListeningAbility) == true)
            throw new InvalidOperationException("Listening is switched off. Turn it on to use it again.");

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

    /// <inheritdoc />
    public LiveHearing? BeginHearing(string? language)
    {
        var tag = language ?? _spoken?.Current ?? "en";
        if (!tag.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return null;
        if (CircleAI.Core.ServicePreferences.Current?.AbilitiesOff.Contains(ListeningAbility) == true) return null;

        CircleAI.Inference.Managed.Runtime.BrainHold.Open(Path.Combine(AppPaths.Cache, "brain-hold"));

        try { return OpenLiveEars() is { } ears ? new LiveHearing(ears) : null; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Android.Util.Log.Warn(Tag, "the live ears could not start: " + ex.GetType().Name + " " + ex.Message);
            return null;
        }
    }

    // The ears named by LiveEars, open and kept; null when they are not on this phone.
    private MoonshineTranscriber? OpenLiveEars()
    {
        lock (_liveGate)
        {
            if (_live is not null && _liveOpened == LiveEars) return _live;

            var old = _live;
            _live = null;
            if (old is not null) old.DisposeAsync().AsTask().GetAwaiter().GetResult();

            var folders = new[]
            {
                ModelStore.Path,
                Path.Combine(Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath ?? ModelStore.Path, "models"),
            };
            var folder = LiveEars.Length == 0
                ? null
                : folders.Select(f => Path.Combine(f, LiveEars)).FirstOrDefault(Directory.Exists);
            if (folder is null) return null;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            _live = new MoonshineTranscriber(folder, threads: LiveThreads);
            _liveOpened = LiveEars;
            Android.Util.Log.Info(Tag, $"live ears open: {LiveEars} in {clock.ElapsedMilliseconds} ms");
            return _live;
        }
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

        // SWITCHED OFF MEANS OFF (Step 4): rendering fetches a missing voice, and the person said not to.
        if (CircleAI.Core.ServicePreferences.Current?.AbilitiesOff.Contains(TalkingAbility) == true)
            throw new InvalidOperationException("Talking is switched off. Turn it on to hear it again.");

        var tag = language ?? _spoken?.Current ?? "en";

        // HOUSEKEEPING BEFORE PULLING MODELS: a voice that is not on the phone yet is
        // downloaded by the render below. Nothing to wait for once start-up is done, and
        // never more than the brain's own 30 s: somebody is waiting to hear this.
        await CircleAI.Inference.ModelSetup.WaitForHousekeepingAsync(
            CircleAI.Device.CircleNeuronService.HousekeepingDone, _ => { }, ct,
            atMost: TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        // THE VOICE HAS THE BIG CORES WHILE IT RENDERS (BrainHold): somebody is listening, and the
        // brain, which writes twice as fast as anybody speaks, has the time to give.
        CircleAI.Inference.Managed.Runtime.BrainHold.Open(Path.Combine(AppPaths.Cache, "brain-hold"));
        CircleAI.Inference.Managed.Runtime.BrainHold.For(TimeSpan.FromSeconds(3));

        // RENDER, DO NOT PLAY. DeviceVoiceHost.SayAsync would put this out of THIS
        // app's loudspeaker, which is the wrong one: the person is looking at the
        // client, and the client is the process that knows whether its screen is on
        // and when to duck. RenderAsync is the half that stops at the file.
        string? wav;
        try { wav = await _voice.RenderAsync(tag, text, ct).ConfigureAwait(false); }
        finally { CircleAI.Inference.Managed.Runtime.BrainHold.Release(); }
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

            // AND THIS PROCESS STOPS LISTENING FOR AS LONG AS IT WILL PLAY. The
            // client plays this in ITS process, so its player marks its own
            // OutLoud and the wake loop here never hears of it - measured on the
            // P30 on 2026-10-16, the wake loop transcribed a whole answer back.
            // The length is exact: the format is fixed, so bytes are seconds.
            OutLoud.For(TimeSpan.FromSeconds(
                pcm.Length / (double)(LinkAudioFormat.SampleRate * LinkAudioFormat.Channels
                                      * (LinkAudioFormat.BitsPerSample / 8))));

            return pcm;
        }
        finally
        {
            // A fresh file per call, so nothing else is reading it.
            try { File.Delete(wav); } catch { /* a temp file that outlives us is not a failure */ }
        }
    }

    /// <summary>
    /// Builds the recogniser and warms it on half a second of silence, before anybody speaks
    /// (Step 4: the ears primed before they are ready). Only when the ears are already on the
    /// phone - the fetcher brings them, in its own order - and not when the person switched
    /// listening off. Never throws.
    /// </summary>
    /// <returns>The ears' model id when primed; null, with why, when not.</returns>
    /// <remarks>
    /// THIS USED TO WAIT FOR THE FIRST SENTENCE ("loaded lazily and kept"): building a recogniser
    /// is seconds on a P30, and the person who paid for it was the one speaking. The owner's
    /// framework primes every model before it says ready, the ears included, so the first audio
    /// call finds them warm.
    /// </remarks>
    public async Task<(string? ModelId, string Why)> PrimeAsync(CancellationToken ct = default)
    {
        try
        {
            if (CircleAI.Core.ServicePreferences.Current?.AbilitiesOff.Contains(ListeningAbility) == true)
                return (null, "listening is switched off");

            // THE LIVE EARS WALK THE SAME PATH ONCE, so the first question is not the one to pay for it.
            var live = await Task.Run(() =>
            {
                if (OpenLiveEars() is not { } ears) return false;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                using var hearing = ears.Begin();
                hearing.Hear(new byte[LinkAudioFormat.BytesPerSecond]);
                hearing.Finish();
                Android.Util.Log.Info(Tag, $"live ears warmed in {clock.ElapsedMilliseconds} ms");
                return true;
            }, ct).ConfigureAwait(false);

            // AND THEN THEY ARE THE EARS: a second recogniser held ready on a phone of this size is what
            // the phone kills the service for (2026-11-07, four times, free memory near 400 MB). What the
            // live ears do not hear still gets Whisper, opened when it is wanted.
            if (live) return (LiveEars, "primed");

            var earsId = EarsOnThePhone();
            if (earsId is null) return (null, "the ears are not on the phone yet");

            var listener = await ListenerAsync(ct).ConfigureAwait(false);
            if (listener is null) return (null, _listenerStatus);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            // MORE THAN A SECOND, OR IT PROVES NOTHING: whisper.cpp hands back anything shorter without
            // running a thing, so "warmed" on half a second left the first real question to pay.
            var silence = new byte[LinkAudioFormat.BytesPerSecond * 6 / 5];
            _ = await listener.Transcriber.TranscribeAsync(silence, ct, "en").ConfigureAwait(false);
            Android.Util.Log.Info(Tag, $"ears primed: {earsId} warmed in {clock.ElapsedMilliseconds} ms");
            return (earsId, "primed");
        }
        catch (OperationCanceledException) { return (null, "cancelled"); }
        catch (Exception ex)
        {
            Android.Util.Log.Warn(Tag, "the ears could not be primed: " + ex.Message);
            return (null, ex.Message);
        }
    }

    /// <summary>The ability whose switch governs the ears.</summary>
    private const string ListeningAbility = "Listening";

    /// <summary>The ability whose switch governs the voice.</summary>
    private const string TalkingAbility = "Talking";

    // The ears the selector would build the recogniser from, when they are whole on the phone.
    private static string? EarsOnThePhone()
    {
        using var registry = new CircleAI.Core.Models.ModelRegistryService();
        var plan = new CircleAI.Inference.SpeechModelSelector(registry)
            .PlanFor(CircleAI.Core.DeviceProbe.Snapshot(), CircleAI.Core.ModelModality.Asr);
        if (!plan.IsAvailable || plan.Model is null) return null;
        using var loader = new CircleAI.Inference.BundleModelLoader(ModelStore.Path, registry);
        return loader.ModelPresent(plan.Model.ModelId) ? plan.Model.ModelId : null;
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

            // HOUSEKEEPING BEFORE PULLING MODELS: building the listener downloads the ears
            // when they are not here yet. At most 30 s, as above: somebody is speaking.
            await CircleAI.Inference.ModelSetup.WaitForHousekeepingAsync(
                CircleAI.Device.CircleNeuronService.HousekeepingDone, _ => { }, ct,
                atMost: TimeSpan.FromSeconds(30)).ConfigureAwait(false);

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
        if (_live is not null) await _live.DisposeAsync().ConfigureAwait(false);
        _live = null;
        _gate.Dispose();
    }
}
