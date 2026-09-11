// DeviceConversation.cs
//
// Listen, think, answer aloud - on this phone.

using CircleAI.Voice;
using CircleAI.Languages.Translation;
using CircleAI.Assistant.Voice;

// The capture class lives in the native head's namespace; from inside
// CircleAI.Samples.App the unqualified path binds against the enclosing
// namespace and does not resolve.
using AndroidAudioCapture = global::CircleAI.Assistant.Device.AndroidAudioCapture;
using CircleAI.Memory;

namespace CircleAI.Assistant.Device;

/// <inheritdoc />
public sealed class DeviceConversation : IConversation
{
    private readonly IBrain _brain;
    private readonly IVoiceHost _voice;
    private readonly ISpokenLanguage _spoken;
    private readonly ISettings _settings;
    private readonly IMemoryService _memory;
    private readonly IRemembers _remembers;

    // MAY I LISTEN, ASKED OF THE HEAD. This was a static call into MAUI's
    // permission API, and that one static was enough to keep the whole turn
    // loop inside a MAUI application project. See IMicrophoneAccess.
    private readonly IMicrophoneAccess _microphone;

    // NO IDispatcher HERE, AND THAT IS DELIBERATE - see the recall block in
    // TurnAsync. This class is a SINGLETON and Fluxor's store is SCOPED, so a
    // dispatcher injected here would be the root scope's, not the one the screen
    // reads. Measured, not assumed: AddFluxor with no lifetime registers
    // IDispatcher, IStore and IState<T> as Scoped.

    /// <summary>Composed from the app's one brain, one voice host and one memory.</summary>
    public DeviceConversation(
        IBrain brain, IVoiceHost voice, ISpokenLanguage spoken, ISettings settings,
        IMemoryService memory, IRemembers remembers, IMicrophoneAccess microphone)
    {
        _brain = brain;
        _voice = voice;
        _spoken = spoken;
        _settings = settings;
        _memory = memory;
        _remembers = remembers;
        _microphone = microphone;
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
        // The barge-in watcher's handles, at method scope so the finally below
        // can close its microphone on every way out of this method.
        CancellationTokenSource? bargeStop = null;
        Task? barge = null;

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

            var mic = await _microphone.GrantedAsync(ct).ConfigureAwait(false);

            if (!mic)
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
            // A VOICE RATHER THAN A TONE, WHEN THE VOICE HAS ONE READY. "One
            // moment." rendered at warm-up and played from disk costs nothing
            // now, and says "I'm here" where the tone said "beep". Not awaited:
            // the model starts thinking underneath it. See AckBank.
            _ = Task.Run(async () =>
            {
                if (!await AckBank.PlayAsync(expect, AckBank.Working, ct).ConfigureAwait(false))
                    try { global::CircleAI.Assistant.Device.Earcon.Heard(); } catch { /* a tone is never worth a turn */ }
            }, CancellationToken.None);

            // WHAT LANGUAGE THAT WAS, reported rather than chosen. A person who
            // fixed a language in Settings keeps it; otherwise every turn is
            // answered in the language it was asked in. Read from the same
            // settings the decoder hint came from, loaded once above.
            var tag = settings.Policy == LanguagePolicy.Fixed && settings.FixedLanguage is { } fixedTag
                ? fixedTag
                : LanguageGuess.Detect(heard) ?? _spoken.Current;

            // NOT WRITTEN TO MEMORY HERE ANY MORE, AND THAT IS THE POINT OF THE
            // STORE. A spoken turn ends with TurnEnded, whose effect carries the
            // exchange from the short-term cache through to long-term memory -
            // one owner, and a caller cannot half-remember by forgetting to also
            // call Learn. HeardAsync stays for TYPED input, which never becomes a
            // turn and so never reaches that action; calling it here as well
            // would record every spoken sentence twice.

            updates.Report(new TurnState(TurnPhase.Thinking, Heard: heard, Language: tag));

            // SPOKEN AS IT IS WRITTEN, ONE SENTENCE AT A TIME. This waited for
            // the whole answer, then synthesised the whole of it as one block,
            // then played it. Measured on 2026-09-09: a Redmi 12 thought for
            // 4,4 s and then rendered for 11,8 s before a sound; a P30 thought
            // for 21,7 s first. The owner heard "big gaps". Each sentence now
            // goes to the voice the moment its end is seen, and the voice works
            // through them in order while the model is still writing the next.
            // See SpokenReply for where a sentence is judged to end.
            // WHAT IT ALREADY KNOWS ABOUT THIS, BEFORE IT ANSWERS.
            //
            // THE HALF THE LOOP WAS MISSING. Every utterance has been written to
            // long-term memory for a long time; nothing ever read one back, so
            // the phone accumulated everything anybody said and could not tell
            // them their own name the following morning.
            //
            // Time-boxed, because this sits directly in front of an answer
            // somebody is waiting for: a remembered name is worth having and
            // never worth making them wait for. Swallowed for the same reason -
            // a store that could not answer must not fail a turn that otherwise
            // works.
            // SHARED WITH THE TYPED PATH, deliberately. Chat asks the same brain
            // about the same person, and a read side wired only to the
            // microphone is the "half a person" its own comment warns about.
            // Recalling owns the budget and the swallow; see Recalling.cs.
            var known = await Recalling.AboutAsync(_remembers, heard, ct).ConfigureAwait(false);

            // THE TURN OWNS THE RECALL, AND THE STORE IS NOT TOLD.
            //
            // Two things make this the right seam rather than a compromise.
            //
            // The turn cannot ask the store to fetch it: an effect is
            // fire-and-forget and the prompt below needs the answer NOW, so
            // there is nothing to await a value out of. There WAS a RecallWanted
            // action and an effect behind it; nothing ever dispatched it.
            //
            // And the turn cannot push the answer INTO the store either. This
            // class is a singleton; Fluxor's store is scoped. A dispatcher
            // resolved here belongs to the root scope, not to the scope the
            // screen reads, so the dispatch would land in a different store and
            // be just as invisible as the dead effect was - the same bug wearing
            // a different hat. If a screen ever needs these facts, they travel
            // out through TurnState like everything else the screen is told.

            // Composed in one place so the spoken and typed paths cannot drift
            // into two different preambles. See Recalling.Ask.
            var asked = Recalling.Ask(heard, known);

            // ALWAYS, INCLUDING ZERO. "Recall found nothing" and "recall never
            // ran" are different faults and this line used to print for only one
            // of them, so the log could not tell them apart.
            Android.Util.Log.Info("CircleAI.Turn", $"recalled {known.Count} for this turn");

            var reply = "";

            // INTERRUPTIBLE. Everything the voice does in this turn runs on a
            // token the person can cancel by talking over it - see BargeInAsync.
            // The turn's own token still ends it from outside.
            using var speaking = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // RENDER ONE AHEAD OF PLAY WHEN THE HOST CAN. DeviceVoiceHost can
            // render to a file and play it as two acts, so sentence two renders
            // while sentence one is heard. A host that cannot split them gets
            // one call per sentence, which still beats waiting for the paragraph.
            await using var mouth = _voice is ISpeechPipeline pipeline
                ? new SpokenReply(
                    (sentence, tok) => pipeline.RenderAsync(tag, sentence, tok),
                    (rendered, tok) => pipeline.PlayAsync(rendered, tok),
                    speaking.Token)
                : new SpokenReply((sentence, tok) => SayAsync(sentence, tag, tok), speaking.Token);

            // LISTENING ONLY WHILE THERE IS SOUND TO TALK OVER. Its microphone is
            // closed in TurnAsync's finally on EVERY exit - an early return on an
            // empty reply included - because the next turn opens its own recorder
            // and two must never overlap.
            bargeStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            barge = BargeInAsync(mouth, speaking, bargeStop.Token);

            await _brain.AskAsync(asked, fragment =>
            {
                reply += fragment;
                mouth.Push(fragment);
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
                // The sentences are already in flight; this is only the wait for
                // the last word to finish, so the mark goes still when the sound
                // stops rather than when the text did.
                await mouth.CompleteAsync().ConfigureAwait(false);

                if (speaking.IsCancellationRequested && !ct.IsCancellationRequested)
                    updates.Report(new TurnState(TurnPhase.Idle, Heard: heard, Detail: "Go on…"));
            }
            catch (Exception speak) when (speak is not OperationCanceledException)
            {
                // IT CANNOT ANSWER ALOUD, AND SAYS SO OUT LOUD. Otherwise the
                // turn finishes by putting text on a screen nobody is looking
                // at, and a broken assistant sounds exactly like a thinking one:
                // like nothing. The reply is still on screen for whoever is.
                Android.Util.Log.Warn("CircleAI.Turn", "could not speak the reply: " + speak.Message);
                try { global::CircleAI.Assistant.Device.Earcon.CannotSpeak(); } catch { }
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
            // ONE RECORDER AT A TIME. Whatever way this turn ended, the watcher's
            // microphone is closed before the next turn can open its own.
            if (bargeStop is not null)
            {
                bargeStop.Cancel();
                if (barge is not null) { try { await barge.ConfigureAwait(false); } catch { } }
                bargeStop.Dispose();
            }
            _one.Release();
        }
    }

    /// <summary>
    /// Watches for a voice while the reply plays, and cancels the reply if one
    /// starts.
    /// </summary>
    /// <remarks>
    /// Opens its own capture, so it runs only while the wake listener is stopped
    /// (it is, for the whole turn) and is stopped before the next turn opens
    /// its microphone. The interrupting words themselves are not kept: the
    /// conversation loop starts a fresh turn straight after, and that turn
    /// records what the person says next. Keeping the first second of an
    /// interruption needs a continuous buffer, which is a later step.
    /// </remarks>
    private static async Task BargeInAsync(
        SpokenReply mouth, CancellationTokenSource speaking, CancellationToken stop)
    {
        try
        {
            // NOT UNTIL IT IS ACTUALLY SPEAKING. This waited a flat 700 ms from
            // the moment the turn started talking to the model, and the model
            // takes 5 to 13 seconds on these phones - so the watcher spent the
            // whole think gap listening with nothing to interrupt, and cancelled
            // the reply on the asker's own trailing voice before a word of it
            // had been spoken. Measured on a P30 on 2026-09-09: heard at
            // 20:52:46, barge-in fired 20:52:48, the model's first token
            // 20:53:00. Waiting on Started means the microphone opens when there
            // is sound to talk over, and never otherwise.
            await mouth.Started.WaitAsync(stop).ConfigureAwait(false);

            // And a beat after that, so the first syllable of its own voice is
            // not the thing it hears.
            await Task.Delay(TimeSpan.FromMilliseconds(700), stop).ConfigureAwait(false);

            await using var mic = new AndroidAudioCapture();
            var onset = new global::CircleAI.Assistant.Device.SpeechOnset();
            if (await onset.WaitAsync(mic, stop).ConfigureAwait(false))
                speaking.Cancel();
        }
        catch (OperationCanceledException) { /* the reply finished first */ }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("CircleAI.Turn", "barge-in watcher failed: " + ex.Message);
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
            var mic = await _microphone.GrantedAsync(ct).ConfigureAwait(false);

            if (!mic)
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

    /// <summary>
    /// Takes the microphone off the wake listener for the length of a turn, and
    /// gives it back afterwards.
    /// </summary>
    /// <remarks>
    /// TWO RECORDERS WERE OPEN AT ONCE. The resident listener holds the
    /// microphone continuously and nothing ever released it, so a turn opened a
    /// SECOND AudioRecord on top of it. Measured on a P30 on 2026-09-09: the wake
    /// heartbeat went on printing every five seconds all the way through a turn
    /// that was supposed to own the microphone, and the same build on a Redmi 12
    /// produced the same nonsense — so it was never one phone's audio stack.
    ///
    /// <para>
    /// AND THE WAKE FIRES BEFORE THE PHRASE IS FINISHED. The gate is on
    /// probability, not on completing the keyword: that turn woke on
    /// <c>3/8 tokens p=0,477</c>, roughly after "Hey Cir…", and the turn opened
    /// its microphone nine milliseconds later. What it recorded was the REST OF
    /// THE WAKE PHRASE — "…cle AI" came back as "Placeculeeai." — and the silence
    /// after it ended the turn before the actual question was ever spoken.
    /// </para>
    /// <para>
    /// So the settle is not politeness, it is the difference between recording
    /// the question and recording the phrase that asked for it. It runs only when
    /// the resident listener was actually holding the microphone, which is
    /// exactly the woken case; pressing the button on a phone that is not
    /// listening costs nothing.
    /// </para>
    /// <para>
    /// Restoring is in a finally by construction. A turn that threw and left the
    /// wake word off would be silent until the app was restarted, and nothing
    /// would say why.
    /// </para>
    /// </remarks>
    private static async Task<IAsyncDisposable> MicrophoneAloneAsync(CancellationToken ct)
    {
        // A HAND-BACK STILL PENDING MEANS THIS IS THE NEXT TURN OF THE SAME
        // CONVERSATION. The wake listener is already stopped and the microphone
        // is already ours: cancel the hand-back, skip the settle, and go. This
        // is what stops every turn after the first paying a stop, a 700 ms
        // wait and a restart - a second of dead air per exchange.
        var pending = Interlocked.Exchange(ref _handBack, null);
        if (pending is not null)
        {
            pending.Cancel();
            pending.Dispose();
            return new GiveItBack();
        }

        if (!global::CircleAI.Device.CircleNeuronService.IsListening) return NotHeld.Instance;

        await global::CircleAI.Device.CircleNeuronService.StopListeningAsync(ct).ConfigureAwait(false);

        // Long enough for the tail of a wake phrase that fired part-way through,
        // and for the "Yes?" not to be recorded as the question.
        try { await Task.Delay(TimeSpan.FromMilliseconds(700), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* give it back anyway, below */ }

        return new GiveItBack();
    }

    /// <summary>The hand-back waiting to happen, or null.</summary>
    private static CancellationTokenSource? _handBack;

    /// <summary>
    /// How long after a turn the microphone stays ours before the wake listener
    /// gets it back.
    /// </summary>
    /// <remarks>
    /// The conversation loop starts its next turn immediately, so a small grace
    /// bridges turns; anything long is a window in which the wake word is off
    /// for no reason. Three seconds covers the loop's own overhead and a person
    /// drawing breath.
    /// </remarks>
    private static readonly TimeSpan HandBackGrace = TimeSpan.FromSeconds(3);

    /// <summary>Nothing was taken, so nothing is given back.</summary>
    private sealed class NotHeld : IAsyncDisposable
    {
        public static readonly NotHeld Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Starts the wake listener again once the conversation is over.</summary>
    private sealed class GiveItBack : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            // DEFERRED, NOT IMMEDIATE. If another turn arrives inside the grace
            // it cancels this and keeps the microphone; if none does, the wake
            // word comes back on its own. Not the turn's token: the turn ending
            // - including by cancellation - is precisely when the listener has
            // to be able to return.
            var cts = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref _handBack, cts);
            previous?.Cancel();
            previous?.Dispose();

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(HandBackGrace, cts.Token).ConfigureAwait(false);
                    if (Interlocked.CompareExchange(ref _handBack, null, cts) != cts) return;
                    await global::CircleAI.Device.CircleNeuronService
                        .StartListeningAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* the next turn took it */ }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("CircleAI.Turn",
                        "could not resume the wake word after the conversation: " + ex.Message);
                }
            }, CancellationToken.None);

            return ValueTask.CompletedTask;
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

    // THE THREE RULES THAT DECIDE WHETHER IT ANSWERS AT ALL now live in
    // Contracts, where a test can reach them - this head only compiles for
    // Android, so while they sat here nothing could pin them. See Heard.
    private static string? Speech(string? heard) => Heard.Speech(heard);


    private async Task<string?> ListenAsync(
        IProgress<TurnState> updates, CancellationToken ct, string? language = null)
    {
        var turn = new global::CircleAI.Assistant.Device.VoiceTurn();
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

        // ONE OWNER, AND THE TAIL OF THE WAKE PHRASE LET GO OF. See
        // MicrophoneAloneAsync: without this the wake listener is still recording
        // while this opens a second microphone, and what this captures is the end
        // of "Hey Circle AI" rather than the question after it.
        await using var alone = await MicrophoneAloneAsync(ct).ConfigureAwait(false);

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
        var listener = _listener ??= (await CircleAIListener.TryCreateAsync(StorageDir).ConfigureAwait(false)).listener;
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
            if (!await _microphone.GrantedAsync(ct).ConfigureAwait(false))
            {
                updates.Report(new TurnState(TurnPhase.Idle,
                    Detail: "It needs permission to hear you."));
                return "";
            }

            var listener = _listener ??= (await CircleAIListener
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

            // And not shared with the wake listener either - a meeting recorded
            // alongside a second open recorder is the same fault as a turn.
            await using var alone = await MicrophoneAloneAsync(ct).ConfigureAwait(false);

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
            var ears = _listener ??= (await CircleAIListener
                .TryCreateAsync(StorageDir, ct: ct).ConfigureAwait(false)).listener;
            said.Add(ears is null ? "ears: not available" : "ears: open");
        }
        catch (Exception ex) { said.Add($"ears: {ex.GetType().Name}: {ex.Message}"); }

        // THE VOICE SECOND, THROUGH THE PATH THAT ACTUALLY SPEAKS.
        //
        // THIS USED TO BUILD AN CircleAISpeaker AND DISPOSE IT, which warmed nothing:
        // DeviceVoiceHost.SayAsync does not use CircleAISpeaker at all, it calls
        // CircleAITtsProbe.RunCataloguedAsync. So the first spoken reply of every
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
            // ANDROID'S OWN CACHE DIRECTORY, NOT MAUI'S WRAPPER OVER IT. This was
            // FileSystem.CacheDirectory, and it was the ONLY thing in the entire
            // turn loop that needed MAUI - one path, holding the whole class
            // inside a MAUI app. On Android the two are the same directory;
            // MAUI's FileSystem.CacheDirectory returns exactly this. So the
            // dependency bought nothing and cost the turn loop its portability:
            // the native head is plain .NET Android and could not have used it.
            var cache = global::Android.App.Application.Context.CacheDir?.AbsolutePath
                        ?? System.IO.Path.GetTempPath();
            var wav = System.IO.Path.Combine(cache, "warm.wav");
            await Task.Run(() => CircleAITtsProbe.RunCataloguedAsync(
                StorageDir, _spoken.Current, "ready", wav, log: null, ct: ct), ct)
                .ConfigureAwait(false);

            var opened = System.IO.File.Exists(wav);
            try { if (opened) System.IO.File.Delete(wav); } catch { }
            said.Add(opened ? "voice: open" : "voice: could not open");
        }
        catch (Exception ex) { said.Add($"voice: {ex.GetType().Name}: {ex.Message}"); }

        // THE SMALL SPOKEN THINGS, RENDERED NOW SO THEY COST NOTHING LATER.
        progress?.Report("Learning to say yes");
        try
        {
            var acks = await AckBank.PrepareAsync(StorageDir, _spoken.Current, ct).ConfigureAwait(false);
            said.Add($"acks: {acks} ready");
        }
        catch (Exception ex) { said.Add($"acks: {ex.GetType().Name}: {ex.Message}"); }

        var report = string.Join("; ", said) + $"; {Environment.TickCount64 - started} ms";

        // SAID OUT LOUD, because a warm-up nobody can see is a warm-up nobody
        // can tell ran. The loading screen shows its steps to whoever is
        // watching the phone; this is for whoever is reading the log afterwards
        // asking why the first turn still took eleven seconds.
        Android.Util.Log.Info("CircleAI.Warm", report);
        return report;
    }

    private CircleAIListener? _listener;

    private static string StorageDir => ModelStore.Path;

    /// <inheritdoc />
    public async Task SayAsync(
        string text, string? languageTag = null, CancellationToken ct = default)
        => await _voice.SayAsync(languageTag ?? _spoken.Current, text, null, ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <inheritdoc />
    /// <remarks>
    /// NO MICROPHONE IS OPENED AND NO PERMISSION IS ASKED FOR. A file is not
    /// capture, so this does not take <c>_one</c> and does not go through
    /// MicrophoneAloneAsync - a transcription of a recording can run while the
    /// wake word is still listening, and making it wait behind a live turn would
    /// be a lock held for forty minutes.
    /// </remarks>
    public async Task<Transcript> TranscribeFileAsync(
        string path,
        string? language = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var listener = _listener ??= (await CircleAIListener
            .TryCreateAsync(StorageDir, ct: ct).ConfigureAwait(false)).listener;

        // NOTHING RATHER THAN A THROW, matching how every other capability on
        // this surface declines: a phone without the speech models is a phone
        // that cannot do this yet, which is a sentence for a screen to show, not
        // an exception for it to catch.
        if (listener is null) return Transcript.Nothing;

        var tag = language ?? _spoken.Current;
        if (listener.Transcriber is WhisperNetTranscriber primable)
            primable.Vocabulary = SpokenVocabulary.For(tag);

        var result = await listener
            .TranscribeFileAsync(path, AndroidAudioDecoder.Instance, tag, progress, ct)
            .ConfigureAwait(false);

        return new Transcript(
            result.Text,
            [.. result.Timed.Select(s => new TranscriptLine(s.Text, s.Start, s.End, s.Speaker))],
            result.LanguageCode,
            result.Confidence);
    }

    /// <inheritdoc />
    /// <remarks>
    /// STRAIGHT TO THE ENGINE, NOT THROUGH A PROMPT BUILT HERE. The whole point
    /// of closing this gap was to stop the screen and the engine being two
    /// owners of one prompt; writing the prompt in this class again would just
    /// move the second owner one layer down.
    /// <para>
    /// The engine is built per call rather than held. It is a few bytes wrapping
    /// a reference to the brain - no model, no native state - and holding one
    /// would mean deciding what happens to it when the brain idle-unloads.
    /// </para>
    /// </remarks>
    public async Task<string> TranslateAsync(
        string text, string fromTag, string toTag, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // FULLY QUALIFIED BECAUSE THE NAME IS TAKEN. CircleAI.Assistant has its
        // own TranslationRequest - the parser for "how do you say X in Zulu",
        // a spoken INTENT rather than a translation job - and this namespace
        // sits inside CircleAI.Assistant, so the unqualified name resolves to
        // that one. Both names are right for what they describe; only one of
        // them can be the short one here.
        // THE FULLER TABLE, NOT THE ENGINE'S DEFAULT. SampleLanguages lists the
        // seventy-five languages this app actually offers; CircleAI.Languages'
        // KnownLanguages lists twenty, and does not include Japanese - which
        // this app has a whole Open JTalk prosody stack for. Passing the right
        // one is the difference between "from English to Japanese" and "from
        // English to ja" for most of the catalogue.
        var engine = new LlmTranslationEngine(
            new BrainAsGenerator(_brain),
            tag => SampleLanguages.Find(tag)?.Name ?? tag);
        var result = await engine.TranslateAsync(
            new CircleAI.Languages.Translation.TranslationRequest(
                text, fromTag, toTag, TranslationMode.Conversational), ct)
            .ConfigureAwait(false);

        return result.TranslatedText;
    }

    /// <inheritdoc />
    public string AsSubtitles(Transcript transcript, SubtitleFormat format = SubtitleFormat.SubRip)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        // Back across the boundary. CircleAI.Assistant cannot see
        // TranscriptSegment and CircleAI.Voice owns the format, so the mapping
        // happens here - which is the whole job of this class.
        var segments = transcript.Lines
            .Select(l => new TranscriptSegment(l.Text, l.Start, l.End) { Speaker = l.Speaker })
            .ToList();

        return format == SubtitleFormat.WebVtt
            ? Subtitles.ToVtt(segments)
            : Subtitles.ToSrt(segments);
    }

    public Task<string> SeeAsync(
        string question, byte[] image, Action<string>? token = null, CancellationToken ct = default)
        => _brain.SeeAsync(question, image, token, ct);
}
