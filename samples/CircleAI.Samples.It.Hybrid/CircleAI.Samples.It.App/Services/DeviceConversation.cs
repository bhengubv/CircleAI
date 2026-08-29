// DeviceConversation.cs
//
// Listen, think, answer aloud - on this phone.

using CircleAI.Voice;
using CircleAI.Samples.It.Voice;

// The capture class lives in the native head's namespace; from inside
// CircleAI.Samples.It.App the unqualified path binds against the enclosing
// namespace and does not resolve.
using AndroidAudioCapture = global::CircleAI.Samples.It.Mobile.AndroidAudioCapture;
using CircleAI.Memory;

namespace CircleAI.Samples.It.App.Services;

/// <inheritdoc />
public sealed class DeviceConversation : IConversation
{
    private readonly IBrain _brain;
    private readonly IVoiceHost _voice;
    private readonly ISpokenLanguage _spoken;
    private readonly ISettings _settings;
    private readonly IMemoryService _memory;

    /// <summary>Composed from the app's one brain, one voice host and one memory.</summary>
    public DeviceConversation(
        IBrain brain, IVoiceHost voice, ISpokenLanguage spoken, ISettings settings,
        IMemoryService memory)
    {
        _brain = brain;
        _voice = voice;
        _spoken = spoken;
        _settings = settings;
        _memory = memory;
    }

    // One turn at a time. Two overlapping turns share a microphone and a speaker,
    // and the result is neither of them.
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <inheritdoc />
    public Task<BrainState> StateAsync(CancellationToken ct = default) => _brain.StateAsync(ct);

    /// <inheritdoc />
    public Task HeardAsync(string said, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(said)) return Task.CompletedTask;

        // NOT AWAITED. Reading what was said takes about twenty milliseconds on
        // a P30 and an answer should not wait for any of it. It cannot throw
        // out of here either - a memory that could take a conversation down
        // with it would deserve to be turned off.
        _ = Task.Run(async () =>
        {
            try { await _memory.LearnAsync(said, ct: CancellationToken.None).ConfigureAwait(false); }
            catch { /* a memory is never worth an answer */ }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task TurnAsync(IProgress<TurnState> updates, CancellationToken ct = default)
    {
        if (!await _one.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
        {
            // SAY SO. This returned in silence, which is indistinguishable from a
            // button that does nothing - and it is the ONE path in this method
            // that reported nothing at all, so it is what a dead-looking
            // microphone button turns out to be every time.
            updates.Report(new TurnState(TurnPhase.Idle,
                Detail: "Still listening to the last one."));
            return;
        }

        try
        {
            var state = await _brain.StateAsync(ct).ConfigureAwait(false);
            if (!state.Ready)
            {
                updates.Report(new TurnState(TurnPhase.Idle, Detail: state.Detail));
                return;
            }

            var mic = await Permissions.CheckStatusAsync<Permissions.Microphone>()
                .ConfigureAwait(false);
            if (mic != PermissionStatus.Granted)
                mic = await Permissions.RequestAsync<Permissions.Microphone>()
                    .ConfigureAwait(false);

            if (mic != PermissionStatus.Granted)
            {
                // Without it AudioRecord does not fail - it hands back silence,
                // which looks exactly like a microphone that does not work.
                updates.Report(new TurnState(TurnPhase.Idle,
                    Detail: "It needs permission to hear you."));
                return;
            }

            updates.Report(new TurnState(TurnPhase.Listening));

            var heard = await ListenAsync(updates, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(heard))
            {
                updates.Report(new TurnState(TurnPhase.Idle,
                    Detail: "I did not catch that."));
                return;
            }

            // WHAT LANGUAGE THAT WAS, reported rather than chosen. A person who
            // fixed a language in Settings keeps it; otherwise every turn is
            // answered in the language it was asked in.
            var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
            var tag = settings.Policy == LanguagePolicy.Fixed && settings.FixedLanguage is { } fixedTag
                ? fixedTag
                : LanguageGuess.Detect(heard) ?? _spoken.Current;

            // Spoken words go the same way typed ones do. See HeardAsync.
            await HeardAsync(heard, ct).ConfigureAwait(false);

            updates.Report(new TurnState(TurnPhase.Thinking, Heard: heard, Language: tag));

            var reply = "";
            await _brain.AskAsync(heard, fragment =>
            {
                reply += fragment;
                updates.Report(new TurnState(TurnPhase.Thinking,
                    Heard: heard, Reply: reply, Language: tag));
            }, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(reply)) return;

            updates.Report(new TurnState(TurnPhase.Speaking,
                Heard: heard, Reply: reply, Language: tag));
            await SayAsync(reply, tag, ct).ConfigureAwait(false);

            updates.Report(new TurnState(TurnPhase.Idle,
                Heard: heard, Reply: reply, Language: tag));
        }
        catch (OperationCanceledException)
        {
            updates.Report(new TurnState(TurnPhase.Idle));
        }
        catch (Exception ex)
        {
            updates.Report(new TurnState(TurnPhase.Idle,
                Detail: $"{ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            _one.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> DictateAsync(
        IProgress<TurnState> updates, CancellationToken ct = default)
    {
        if (!await _one.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
        {
            updates.Report(new TurnState(TurnPhase.Idle,
                Detail: "Still listening to the last one."));
            return null;
        }

        try
        {
            // NO BRAIN CHECK. This is the whole point of the method: writing down
            // what somebody said needs the ears, not the answering model, and the
            // screen that uses it was demanding - and naming - the wrong one.
            var mic = await Permissions.CheckStatusAsync<Permissions.Microphone>()
                .ConfigureAwait(false);
            if (mic != PermissionStatus.Granted)
                mic = await Permissions.RequestAsync<Permissions.Microphone>()
                    .ConfigureAwait(false);

            if (mic != PermissionStatus.Granted)
            {
                updates.Report(new TurnState(TurnPhase.Idle,
                    Detail: "It needs permission to hear you."));
                return null;
            }

            updates.Report(new TurnState(TurnPhase.Listening));

            var heard = Speech(await ListenAsync(updates, ct).ConfigureAwait(false));

            updates.Report(heard is null
                ? new TurnState(TurnPhase.Idle, Detail: "I did not catch that.")
                : new TurnState(TurnPhase.Idle, Heard: heard));

            return heard;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// What was actually SAID, or null when the transcriber only heard noise.
    /// </summary>
    /// <remarks>
    /// WHISPER LABELS NON-SPEECH RATHER THAN RETURNING NOTHING. A quiet room
    /// comes back as "[BLANK_AUDIO]", a radio in the background as "[Music]", and
    /// those are not empty strings - so they sailed through an
    /// IsNullOrWhiteSpace check and straight into whatever asked for the text.
    /// Pressing "Say it" next to a television would have written [Music] onto
    /// somebody's CV as the kind of work they are looking for.
    /// <para>
    /// Only WHOLE bracketed tokens go. Somebody saying "forklift (code 14)" keeps
    /// their brackets; what is removed is the transcriber talking about the audio
    /// instead of transcribing it. If nothing survives, nothing was said.
    /// </para>
    /// </remarks>
    private static string? Speech(string? heard)
    {
        if (string.IsNullOrWhiteSpace(heard)) return null;

        var stripped = System.Text.RegularExpressions.Regex.Replace(
            heard, @"[\[(][^\])]*[\])]", " ").Trim();

        // Punctuation on its own is not speech either: silence often comes back
        // as a lone full stop once the tag is gone.
        var hasWords = stripped.Any(char.IsLetterOrDigit);
        return hasWords ? System.Text.RegularExpressions.Regex.Replace(stripped, @"\s+", " ") : null;
    }

    /// <summary>
    /// Open the microphone until the speaker stops, and transcribe what they said.
    /// </summary>
    /// <remarks>
    /// END OF SPEECH IS A SILENCE, NOT A TIMER. Cutting somebody off after a fixed
    /// number of seconds truncates the slow, the elderly and anybody thinking - the
    /// people this is most for. The thresholds are VoiceTurn's own: speech is 3x
    /// the noise floor or an absolute 0.02, and 1.4 seconds of quiet ends the turn.
    /// </remarks>
    private async Task<string?> ListenAsync(IProgress<TurnState> updates, CancellationToken ct)
    {
        var turn = new global::CircleAI.Samples.It.Mobile.VoiceTurn();
        turn.Level += (_, level) => updates.Report(new TurnState(TurnPhase.Listening, level));

        // A HARD CEILING ON THE WHOLE LISTEN.
        //
        // VoiceTurn ends on silence and has its own no-speech and maximum-length
        // timeouts - but it evaluates both INSIDE the loop that reads microphone
        // chunks, so a microphone that yields nothing at all never reaches them.
        // That is not hypothetical: without RECORD_AUDIO, AudioRecord does not
        // throw, and a capture that never produces a frame leaves the turn waiting
        // for a speaker who is not being recorded.
        //
        // A turn stuck there holds the one-turn semaphore for the life of the
        // process, so every later press of the button returns instantly and
        // silently. One hang and the microphone is dead until the app restarts.
        ReadOnlyMemory<byte> audio;
        using var cap = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cap.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            await using var mic = new AndroidAudioCapture();
            audio = await turn.ListenAsync(mic, cap.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The ceiling, not the caller. Said as a fact about the microphone
            // rather than as an error, because that is what it is.
            updates.Report(new TurnState(TurnPhase.Idle,
                Detail: "The microphone did not send anything."));
            return null;
        }

        // Nobody spoke. Empty, not an error.
        if (audio.Length == 0) return null;

        // THE MICROPHONE IS CLOSED BEFORE TRANSCRIBING. Whisper on a P30 takes
        // seconds, and holding AudioRecord open through it keeps the mic light on
        // and the radio busy for the whole of a turn nobody is speaking into.
        var listener = _listener ??= (await ItListener.TryCreateAsync(StorageDir).ConfigureAwait(false)).listener;
        if (listener is null) return null;

        var result = await listener.Transcriber.TranscribeAsync(audio, ct).ConfigureAwait(false);
        return result.Text;
    }

    private ItListener? _listener;

    private static string StorageDir => ModelStore.Path;

    /// <inheritdoc />
    public async Task SayAsync(
        string text, string? languageTag = null, CancellationToken ct = default)
        => await _voice.SayAsync(languageTag ?? _spoken.Current, text, null, ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<string> SeeAsync(
        string question, byte[] image, Action<string>? token = null, CancellationToken ct = default)
        => _brain.SeeAsync(question, image, token, ct);
}
