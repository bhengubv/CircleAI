// DeviceConversation.cs
//
// Listen, think, answer aloud - on this phone.

using CircleAI.Voice;
using CircleAI.Samples.It.Voice;

// The capture class lives in the native head's namespace; from inside
// CircleAI.Samples.It.App the unqualified path binds against the enclosing
// namespace and does not resolve.
using AndroidAudioCapture = global::CircleAI.Samples.It.Mobile.AndroidAudioCapture;

namespace CircleAI.Samples.It.App.Services;

/// <inheritdoc />
public sealed class DeviceConversation : IConversation
{
    private readonly IBrain _brain;
    private readonly IVoiceHost _voice;
    private readonly ISpokenLanguage _spoken;
    private readonly ISettings _settings;

    /// <summary>Composed from the app's one brain and one voice host.</summary>
    public DeviceConversation(
        IBrain brain, IVoiceHost voice, ISpokenLanguage spoken, ISettings settings)
    {
        _brain = brain;
        _voice = voice;
        _spoken = spoken;
        _settings = settings;
    }

    // One turn at a time. Two overlapping turns share a microphone and a speaker,
    // and the result is neither of them.
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <inheritdoc />
    public Task<BrainState> StateAsync(CancellationToken ct = default) => _brain.StateAsync(ct);

    /// <inheritdoc />
    public async Task TurnAsync(IProgress<TurnState> updates, CancellationToken ct = default)
    {
        if (!await _one.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
            return;

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

        ReadOnlyMemory<byte> audio;
        await using (var mic = new AndroidAudioCapture())
            audio = await turn.ListenAsync(mic, ct).ConfigureAwait(false);

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
