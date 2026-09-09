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

            var mic = await MicPermission.EnsureAsync().ConfigureAwait(false);

            if (mic != PermissionStatus.Granted)
            {
                // Without it AudioRecord does not fail - it hands back silence,
                // which looks exactly like a microphone that does not work.
                updates.Report(new TurnState(TurnPhase.Idle,
                    Detail: "It needs permission to hear you."));
                return;
            }

            updates.Report(new TurnState(TurnPhase.Listening));

            // TIMED AND SAID OUT LOUD, because a turn that answers slowly and
            // wrongly is two different faults and this path reported neither. On
            // 2026-09-08 an owner said the reply was badly wrong after a long
            // wait, and the whole device log for that window held one GC line and
            // the wake heartbeat - there was no way to tell a misheard question
            // from a well-heard one answered badly.
            var clock = System.Diagnostics.Stopwatch.StartNew();

            // TOLD WHAT TO EXPECT, WHICH THIS PATH ALONE WAS NOT. Transcribe
            // passes Spoken.Current and Translate passes the side's language;
            // this decoded on auto-detect and only worked out the language
            // AFTERWARDS, from the text it had already got wrong. Both hinted
            // screens were reported working well on real material - a series, a
            // subtitled film - while this one was reported completely broken, and
            // it is the screen the wake word opens into.
            //
            // The hint is the decoder's, not the answer's: the reply language is
            // still detected from what was actually heard, a few lines down, so
            // somebody who switches language mid-conversation is still answered
            // in the language they used.
            var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
            var expect = settings.Policy == LanguagePolicy.Fixed && settings.FixedLanguage is { } fixedExpect
                ? fixedExpect
                : _spoken.Current;

            var heard = Speech(await ListenAsync(updates, ct, expect).ConfigureAwait(false));
            var listened = clock.ElapsedMilliseconds;
            Android.Util.Log.Info("CircleAI.Turn",
                $"heard in {listened} ms: \"{Short(heard)}\"");

            if (string.IsNullOrWhiteSpace(heard))
            {
                updates.Report(new TurnState(TurnPhase.Idle,
                    Detail: "I did not catch that."));
                return;
            }

            // "GOT IT - WORKING ON IT." The gap between the last word and the
            // first word back is transcription and then prefill, and on this
            // phone that is seconds. A conversation has a sound for that moment
            // and it is not silence. Played here, once there are words: before
            // this, the only sign of life was a caption on a screen the speaker
            // had already turned away from.
            try { global::CircleAI.Samples.It.Mobile.Earcon.Heard(); } catch { /* a tone is never worth a turn */ }

            // WHAT LANGUAGE THAT WAS, reported rather than chosen. A person who
            // fixed a language in Settings keeps it; otherwise every turn is
            // answered in the language it was asked in. Read from the same
            // settings the decoder hint came from, loaded once above.
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

            // THE TWO HALVES, SEPARATELY TIMED. "It took ages and said something
            // mad" is either a slow transcriber or a slow brain, and either a
            // misheard question or a well-heard one answered badly. One line that
            // shows the question, the answer, and where the seconds went tells
            // those four apart; nothing did before.
            Android.Util.Log.Info("CircleAI.Turn",
                $"answered in {clock.ElapsedMilliseconds - listened} ms "
                + $"(turn {clock.ElapsedMilliseconds} ms, lang {tag}): \"{Short(reply)}\"");

            if (string.IsNullOrWhiteSpace(reply)) return;

            updates.Report(new TurnState(TurnPhase.Speaking,
                Heard: heard, Reply: reply, Language: tag));

            try
            {
                await SayAsync(reply, tag, ct).ConfigureAwait(false);
            }
            catch (Exception speak) when (speak is not OperationCanceledException)
            {
                // IT CANNOT ANSWER ALOUD, AND SAYS SO OUT LOUD. Otherwise the
                // turn finishes by putting text on a screen nobody is looking
                // at, and a broken assistant sounds exactly like a thinking one:
                // like nothing. The reply is still on screen for whoever is.
                Android.Util.Log.Warn("CircleAI.Turn", "could not speak the reply: " + speak.Message);
                try { global::CircleAI.Samples.It.Mobile.Earcon.CannotSpeak(); } catch { }
            }

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
        IProgress<TurnState> updates, CancellationToken ct = default, string? language = null)
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
            var mic = await MicPermission.EnsureAsync().ConfigureAwait(false);

            if (mic != PermissionStatus.Granted)
            {
                updates.Report(new TurnState(TurnPhase.Idle,
                    Detail: "It needs permission to hear you."));
                return null;
            }

            updates.Report(new TurnState(TurnPhase.Listening));

            var heard = Speech(await ListenAsync(updates, ct, language).ConfigureAwait(false));

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

    /// <summary>One line of it, for the log.</summary>
    /// <remarks>
    /// TRUNCATED AND LOCAL. This writes what somebody said into logcat, which sits
    /// uneasily beside a notification promising nothing is kept — so it is worth
    /// being exact about what this is: the device's own ring buffer, which never
    /// leaves the phone and is overwritten within minutes. It is the only way to
    /// tell a misheard question from a badly answered one, and that question was
    /// unanswerable without it.
    /// <para>
    /// Newlines collapse because a multi-line reply would otherwise become a
    /// dozen log entries with no tag on the ones after the first.
    /// </para>
    /// </remarks>
    private static string Short(string? text, int max = 160)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var one = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return one.Length <= max ? one : one[..max] + "…";
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
    private async Task<string?> ListenAsync(
        IProgress<TurnState> updates, CancellationToken ct, string? language = null)
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

        // LIFTED BEFORE WHISPER SEES IT, for the same reason the wake word is.
        //
        // The gain went into ZipformerWakeWordDetector and stopped there, and
        // this path never touches that class - it opens its own microphone above
        // and hands the bytes straight to the transcriber. So the wake word
        // learned to hear across a room on 2026-09-06 while the transcriber went
        // on being fed the same near-silent waveform, and 4,7 seconds of speech
        // came back as "A-B.".
        //
        // Whole-clip rather than the streaming follower: the recording is
        // finished, so its loudest moment is already known and one multiplier
        // does the job with nothing to pump against.
        var lifted = audio.ToArray();
        var gain = SpeechGain.Normalise(lifted);
        if (gain > 1) VoiceTrace.Write($"stt: lifted the clip x{gain:0.#} before decoding");

        // SET ON EVERY PATH, ALWAYS, BECAUSE IT IS STICKY AND THE TRANSCRIBER IS
        // SHARED. Only SessionAsync ever assigned this and nothing ever cleared
        // it, so one Transcribe session left its vocabulary primed into every
        // later Tap n Talk and Translate turn for the life of the process -
        // words from a meeting biasing a question about the weather. Assigning it
        // here makes the value always the one this call actually wants, which
        // removes the leak and primes the two paths that never were.
        //
        // Free when it has not changed: the setter compares before it disposes
        // the cached processor, so the common case of turn after turn in one
        // language costs nothing.
        if (listener.Transcriber is WhisperNetTranscriber primable)
            primable.Vocabulary = SpokenVocabulary.For(language ?? _spoken.Current);


        var result = await listener.Transcriber
            .TranscribeAsync(lifted, ct, language).ConfigureAwait(false);
        return result.Text;
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public async Task<string> SessionAsync(
        IProgress<TurnState> updates, CancellationToken ct = default,
        string? language = null, double silenceMs = 5000)
    {
        if (!await _one.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
        {
            updates.Report(new TurnState(TurnPhase.Idle,
                Detail: "Still listening to the last one."));
            return "";
        }

        try
        {
            if (await MicPermission.EnsureAsync().ConfigureAwait(false) != PermissionStatus.Granted)
            {
                updates.Report(new TurnState(TurnPhase.Idle,
                    Detail: "It needs permission to hear you."));
                return "";
            }

            var listener = _listener ??= (await ItListener
                .TryCreateAsync(StorageDir, ct: ct).ConfigureAwait(false)).listener;
            if (listener is null)
            {
                updates.Report(new TurnState(TurnPhase.Idle,
                    Detail: "The ears are not on this phone yet."));
                return "";
            }

            var tag = language ?? _spoken.Current;

            // NAMES AND MONEY, WHICH IS WHAT A SMALL MODEL ACTUALLY GETS WRONG.
            // Measured on a P30 on 2026-09-07: a meeting came back
            // seventy-five words of seventy-eight exact, and the three it missed
            // were two South African names and the word "rand", heard as "rent".
            // Priming fixed all three on the same recording.
            //
            // Set per session rather than when the model was opened, because the
            // transcriber is shared and the domain is not. Asked for as a
            // capability: a transcriber that cannot be primed simply is not.
            if (listener.Transcriber is WhisperNetTranscriber primable)
                primable.Vocabulary = SpokenVocabulary.For(tag);

            // ONE MICROPHONE FOR THE WHOLE MEETING. The screen used to open and
            // close one per sentence, which flickers the microphone indicator,
            // pays the open cost every time somebody pauses, and loses whatever
            // was said in the gap between closing and reopening.
            await using var mic = new AndroidAudioCapture();
            await using var session = new SpokenSession(mic, listener.Transcriber, tag)
            {
                SilenceToEndMs = silenceMs,
            };

            // FILTERED HERE TOO, AND THIS SCREEN WAS THE ONE THAT WAS NOT.
            // Speech() strips the labels Whisper writes when it hears something
            // that is not speech - [BLANK_AUDIO] for a quiet room, [Music] for a
            // radio - and both other paths call it. A meeting transcript is
            // exactly where a pause near a television gets recorded as if
            // somebody had said "[Music]", and then read back in the closing
            // summary as though they had.
            session.Heard += (_, piece) =>
                updates.Report(new TurnState(
                    piece.Final ? TurnPhase.Idle : TurnPhase.Listening,
                    Heard: Speech(piece.All) ?? "", Language: tag));

            updates.Report(new TurnState(TurnPhase.Listening, Language: tag));
            await session.ListenAsync(ct).ConfigureAwait(false);

            // THE CLOSING PASS RUNS EVEN THOUGH THE SESSION WAS CANCELLED, which
            // is why it gets its own token. Stopping means "I have finished
            // speaking", not "throw away the accurate version" - and this is the
            // one moment in the whole session when a long decode costs nobody
            // anything, because nobody is waiting on a word.
            updates.Report(new TurnState(TurnPhase.Thinking,
                Heard: session.Text, Language: tag,
                Detail: "Reading it back…"));

            var final = Speech(
                await session.ReadAgainAsync(CancellationToken.None).ConfigureAwait(false)) ?? "";

            // WHAT THE SESSION ACTUALLY CAME AWAY WITH, which is the question the
            // owner asks when a reply is wrong: did it mishear me, or did it hear
            // me and answer badly? The live text and the closing pass are logged
            // separately because they can differ - that is the whole point of the
            // closing pass - and when they do, the difference is the diagnosis.
            Android.Util.Log.Info("CircleAI.Turn",
                $"session ({tag}) live: \"{Short(session.Text)}\"");
            Android.Util.Log.Info("CircleAI.Turn",
                $"session ({tag}) final: \"{Short(final)}\"");

            return final;
        }
        catch (OperationCanceledException)
        {
            updates.Report(new TurnState(TurnPhase.Idle));
            return "";
        }
        catch (Exception ex)
        {
            updates.Report(new TurnState(TurnPhase.Idle,
                Detail: $"{ex.GetType().Name}: {ex.Message}"));
            return "";
        }
        finally
        {
            _one.Release();
        }
    }

    public async Task<string> PrepareAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var started = Environment.TickCount64;
        var said = new List<string>();

        // THE EARS FIRST - AND NOT FOR THE REASON THIS ONCE SAID.
        //
        // It claimed to remove the eleven-second first decode. The logs had
        // already disproved that and I had not read them: every stt line splits
        // the cost, and building the processor is NOT where it goes.
        //
        //     stt: built=15 ms | decode=11648 ms
        //     stt: built=53 ms | decode=29541 ms
        //
        // Opening is milliseconds. The seconds are the decode itself - Whisper
        // on a P30 - and no warm-up will ever touch that. What this does buy is
        // the model open and the first allocation, which is real and small, and
        // saying so honestly is worth more than a gate justified by a number
        // that was never the problem.
        progress?.Report("Opening the ears");
        try
        {
            var ears = _listener ??= (await ItListener
                .TryCreateAsync(StorageDir, ct: ct).ConfigureAwait(false)).listener;
            said.Add(ears is null ? "ears: not available" : "ears: open");
        }
        catch (Exception ex) { said.Add($"ears: {ex.GetType().Name}: {ex.Message}"); }

        // THE VOICE SECOND, THROUGH THE PATH THAT ACTUALLY SPEAKS.
        //
        // THIS USED TO BUILD AN ItSpeaker AND DISPOSE IT, which warmed nothing:
        // DeviceVoiceHost.SayAsync does not use ItSpeaker at all, it calls
        // ItTtsProbe.RunCataloguedAsync. So the first spoken reply of every
        // session still logged "(INCLUDING model open)" while the warm-up
        // reported success - a warm copy of an object nobody uses, which is the
        // same mistake as warming a listener the turn does not hold.
        //
        // Synthesising to a throwaway wav rather than speaking: RunCataloguedAsync
        // writes a file and DeviceVoiceHost plays it afterwards, so calling the
        // synthesis half directly opens everything the real path opens and makes
        // no sound doing it.
        progress?.Report("Opening the voice");
        try
        {
            var wav = System.IO.Path.Combine(FileSystem.CacheDirectory, "warm.wav");
            await Task.Run(() => ItTtsProbe.RunCataloguedAsync(
                StorageDir, _spoken.Current, "ready", wav, log: null, ct: ct), ct)
                .ConfigureAwait(false);

            var opened = System.IO.File.Exists(wav);
            try { if (opened) System.IO.File.Delete(wav); } catch { }
            said.Add(opened ? "voice: open" : "voice: could not open");
        }
        catch (Exception ex) { said.Add($"voice: {ex.GetType().Name}: {ex.Message}"); }

        var report = string.Join("; ", said) + $"; {Environment.TickCount64 - started} ms";

        // SAID OUT LOUD, because a warm-up nobody can see is a warm-up nobody
        // can tell ran. The loading screen shows its steps to whoever is
        // watching the phone; this is for whoever is reading the log afterwards
        // asking why the first turn still took eleven seconds.
        Android.Util.Log.Info("CircleAI.Warm", report);
        return report;
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
