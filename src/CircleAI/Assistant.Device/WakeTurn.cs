// WakeTurn.cs
//
// What happens after it hears its name.
//
// IT HEARD ITS NAME AND INCREMENTED AN INTEGER. The detector fired, the counter went
// up, and that was the whole of it: no tone, no microphone, no question, no answer.
// Every part had been built and nothing joined them, which is this repo's named
// first defect sitting on the headline feature. Measured on a P30 on 2026-10-01 - a
// person said "Hey B", the log read HEARD "HEY B" p=0.3029, and the phone did
// nothing at all.
//
// HERE RATHER THAN DeviceConversation, AND NOT FOR CONVENIENCE. That class is the
// app's turn loop and takes seven dependencies, two of which - ISpokenLanguage and
// ISettings - have no implementation anywhere in src/: the only ones in the tree sit
// inside the sample app, which the service cannot reference and which by the
// thin-client rule should not be holding them. A screen-off turn in the service does
// not need a dispatcher or a settings screen; it needs the microphone, the
// recogniser, the brain and the voice, and all four are already wired in this
// process.
//
// ONE MICROPHONE, AND THE WAKE LOOP IS HOLDING IT. Android hands out AudioRecord
// exclusively, so the sequence is strictly ordered: stop listening, wait for the
// capture to actually close, take it, give it back in a finally. ScreenUpWakeWord's
// own header spells out why the stop has to be awaited rather than cancelled -
// cancelling races perfectly on a fast machine and drops the first second of every
// question on a slow one, which reads to a person as "it ignored me".

using System.Diagnostics;
using Android.Util;
using CircleAI.Device;
using CircleAI.Linking;
using CircleAI.Voice;

namespace CircleAI.Assistant.Device;

/// <summary>Runs one spoken exchange, screen off, in the service's own process.</summary>
public static class WakeTurn
{
    private const string Tag = "CircleAI.WakeTurn";

    /// <summary>The recogniser and the voice. Set by the host at process start.</summary>
    /// <remarks>
    /// The SAME ServiceSpeech the link hands to clients, deliberately: one recogniser
    /// and one voice per device, so a spoken turn and a linked app cannot end up on
    /// different models or different languages.
    /// </remarks>
    public static ILinkSpeech? Speech { get; set; }

    /// <summary>How long a question may run before the turn gives up on it.</summary>
    /// <remarks>
    /// A CEILING, NOT A TARGET. The detector ends the turn on silence; this only
    /// catches the case where silence never comes - a television in the room, a
    /// pocket - and it has to exist, because the alternative is a microphone held
    /// open until the process dies.
    /// </remarks>
    private static readonly TimeSpan Longest = TimeSpan.FromSeconds(15);

    /// <summary>How long it waits for somebody to START, having said hello.</summary>
    /// <remarks>
    /// SEPARATE FROM <see cref="Longest"/>, WHICH IS A DIFFERENT QUESTION. That one
    /// bounds a question already under way; this one bounds the silence before any
    /// question at all, and the two must not share a number. Fifteen seconds of
    /// nothing is not patience, it is a phone that has stopped responding - measured
    /// on a P30 on 2026-10-03, where exactly that silence read as a dead turn.
    ///
    /// SIX SECONDS, between Siri (which closes at about three) and Alexa (about
    /// five), and longer than both because this one has just spoken and the person
    /// has to hear it finish first.
    /// </remarks>
    private static readonly TimeSpan FirstWord = TimeSpan.FromSeconds(6);


    /// <summary>One turn at a time; a second wake while one is running is ignored.</summary>
    private static readonly SemaphoreSlim One = new(1, 1);

    /// <summary>Hear the question, answer it, say the answer.</summary>
    /// <param name="phrase">What was heard, for the log.</param>
    /// <param name="ct">Cancelled when the service is going away.</param>
    public static async Task RunAsync(string phrase, CancellationToken ct = default)
    {
        if (!await One.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
        {
            Log.Info(Tag, "already in a turn - ignoring this wake");
            return;
        }

        var took = false;

        try
        {
            Log.Info(Tag, "woke on \"" + phrase + "\" - taking the microphone");

            // THE TONE FIRST, AND BEFORE THE MICROPHONE CHANGES HANDS. It is the only
            // signal a person gets with the screen off that they were heard at all,
            // and it has to arrive while they are still deciding whether to carry on.
            try { Earcon.Woke(); } catch { /* a tone is never worth the turn */ }

            // AND THE BRAIN STARTS ON THE NAME, NOT ON THE QUESTION.
            //
            // THE CHEAPEST SECONDS IN THE WHOLE TURN, AND THEY WERE BEING THROWN
            // AWAY. A cold load is 13 to 23 s on the P30; the greeting, the
            // microphone, the person speaking and Whisper transcribing are another
            // 4 to 8 - and all of that used to happen with the brain untouched,
            // because nothing asked it to start until the question was in hand.
            // Starting here runs the load UNDERNEATH the part of the turn that was
            // going to happen anyway, so a phone that would have answered at 20 s
            // answers at 13, and a warm one is unaffected.
            //
            // Fire-and-forget by nature - Start only posts an intent - and
            // swallowed, because ColdTurn asks again and waits properly later.
            // This is an optimisation, never the mechanism.
            try
            {
                CircleNeuronService.Start(global::Android.App.Application.Context);
                if (CircleNeuronService.Brain is { } brain)
                    _ = brain.EnsureReadyAsync(CancellationToken.None).ContinueWith(
                        t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex) { Log.Warn(Tag, "could not start the brain early: " + ex.Message); }

            took = true;
            await CircleNeuronService.StopListeningAsync(ct).ConfigureAwait(false);

            // The tail of the wake phrase is still in the air. Without this it lands
            // at the front of the question and the recogniser reads "Hey B what is".
            try { await Task.Delay(TimeSpan.FromMilliseconds(700), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            // AND THEN IT ANSWERS, BEFORE IT EXPECTS ANYTHING.
            //
            // THE TONE WAS NOT ENOUGH, measured on a P30 on 2026-10-03. A person said
            // "Hey B", the spotter heard it at p=0.4068, the beep played for 345 ms -
            // and they waited, because that is what every assistant they have used
            // does: you say the name, it answers, THEN you ask. This turn meanwhile
            // opened the microphone and sat there. Fifteen seconds later it had
            // collected 10,240 bytes - 0.32 seconds of speech - timed out, and said
            // nothing at all. Nobody was at fault; the order was.
            //
            // BEFORE THE MICROPHONE OPENS, not after, for two reasons: the person has
            // to hear it before they start talking, and speaking into a microphone we
            // are holding would put our own voice at the front of their question.
            //
            // NEVER FATAL. A greeting that failed to synthesise must not cost somebody
            // the turn - the tone already told them something happened, and a question
            // answered without a hello beats no answer.
            try
            {
                // FROM THE WARMED COPY - see WarmAsync for the seven seconds this
                // used to cost on the first wake after the service started.
                await GreetAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Warn(Tag, "could not greet: " + ex.Message); }

            var question = await HearAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(question))
            {
                Log.Info(Tag, "nothing was said");

                // IT SAID HELLO AND THEN WENT QUIET, which is the same defect the
                // greeting exists to fix, one step later: the person is left with no
                // idea whether it is still listening, gave up, or broke.
                try { await SayAsync(AssistantPersona.NothingHeard, ct).ConfigureAwait(false); }
                catch (Exception ex) { Log.Warn(Tag, "could not say so: " + ex.Message); }

                return;
            }

            // NEVER THE WORDS THEMSELVES. What a person says to their phone is not a
            // log line; its length is enough to tell a question from nothing.
            Log.Info(Tag, "heard: " + question!.Length + " chars");

            // AND IF ALL THEY SAID WAS THE NAME AGAIN, IT HAS ALREADY ANSWERED. The
            // greeting went out before the microphone opened; saying it twice is
            // better than putting "Hey B" to a language model for thirty seconds.
            if (CircleAI.Assistant.Opener.IsNothingButHello(question))
            {
                Log.Info(Tag, "that was a greeting, not a question");
                return;
            }

            try { Earcon.Heard(); } catch { /* as above */ }

            // SPOKEN AS IT IS WRITTEN, ONE SENTENCE AT A TIME - AND THIS PATH WAS
            // THE ONE THAT NEVER GOT IT.
            //
            // SpokenReply was built in September for exactly this complaint ("big
            // gaps between speech input and Circle AI response") and wired into the
            // app's turn loop only. The screen-off turn - the one somebody uses
            // without picking the phone up - still generated the WHOLE answer, then
            // synthesised the WHOLE of it, then played it: measured on a P30 at 21,7
            // s of thinking followed by a render before any sound. The first
            // sentence now costs its own render instead of the paragraph's, and the
            // voice works through the rest while the model is still writing.
            //
            // AND THE HOLDING LINE GOES THROUGH THE SAME MOUTH, which is the only
            // arrangement in which it cannot talk over the answer: one queue, one
            // reader, strict order. Two players on one speaker is worse than
            // silence.
            await using var mouth = new SpokenReply(
                (sentence, tok) => SayAsync(PlainReply.Clean(sentence), tok), ct);

            var answer = await AskAsync(question!, mouth, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(answer))
                Log.Info(Tag, "answer: " + answer!.Length + " chars");

            // The sentences are already in flight; this waits for the last word to
            // finish rather than for the text to.
            await mouth.CompleteAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The service is stopping. The finally still gives the microphone back.
        }
        catch (Exception ex)
        {
            // A FAILED TURN MUST NOT TAKE THE LISTENER WITH IT. Whatever went wrong,
            // the phone has to go back to answering to its name - otherwise one bad
            // question leaves it deaf until somebody opens an app and switches it on.
            Log.Error(Tag, "turn failed: " + ex);
        }
        finally
        {
            if (took)
            {
                try
                {
                    await CircleNeuronService.StartListeningAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error(Tag, "could not resume listening: " + ex.Message);
                }
            }

            One.Release();
        }
    }

    /// <summary>Record until they stop talking, and hand back what was said.</summary>
    /// <remarks>
    /// THE DETECTOR DECIDES WHEN THE TURN ENDS, not a timer. A fixed window either
    /// cuts somebody off mid-sentence or makes everybody wait out the remainder of
    /// it; EnergyVadDetector's thresholds were tuned on this phone and are the same
    /// ones the app's spoken turn uses.
    /// </remarks>
    private static async Task<string?> HearAsync(CancellationToken ct)
    {
        var speech = Speech;
        if (speech is null)
        {
            Log.Warn(Tag, "no recogniser wired - cannot hear a question");
            return null;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Longest);

        var spoken = new List<byte>();
        var clock = Stopwatch.StartNew();

        // THE QUESTION IS NOW CAPTURED BY THE GATE THE WAKE WORD USES, AND KEPT
        // WHOLE. Both halves of that were wrong, and they were found from the one
        // wake that fired in four days of continuous listening on the P30:
        //
        //     06:43:50  HEARD "Hey B" in "HAY B"
        //     06:44:15  captured 20480 bytes in 15180 ms
        //     06:44:17  stt: 0,6 s audio ... 0 chars
        //     06:44:17  nothing was said
        //
        // Fifteen seconds of listening kept 0,64 seconds of audio. The wake had
        // worked, the owner asked his question, and 96 per cent of it was thrown
        // away before a recogniser ever saw it.
        //
        // THE FIRST HALF: A THRESHOLD FROM THE WRONG INSTRUMENT. This used
        // EnergyVadDetector at its default 0,02 RMS - a number taken from a
        // close-talking microphone. Through this phone's capture path the
        // owner's voice reads 0,002 to 0,016, so almost every frame of the
        // question scored as silence and only a plosive or two cleared the bar.
        // It is the same defect SpeechGate had at 0,004, living in a second
        // owner; one fact with two owners ends up with two answers, and here
        // the second answer was "nobody spoke".
        //
        // THE SECOND HALF: KEEPING FRAMES INSTEAD OF A QUESTION. Even with the
        // right threshold, adding only the frames that cleared it chops the
        // utterance into pieces - the quiet syllables, the consonants and the
        // gaps between words are exactly the frames a level test rejects, and a
        // recogniser needs all of them. And the loop ended the question at the
        // first gap it saw, which with a 300 ms silence count is the space
        // between two words.
        //
        // So the gate decides only WHEN the question starts and ends, and
        // everything between those two moments is kept, plus a moment before
        // the start so the first syllable is not clipped. The gate's own
        // hangover is a full second, so a pause to think is not an ending.
        try
        {
            await using var mic = new AndroidAudioCapture();
            // LearnsWhileOpen = false: a fresh gate that learned from the question
            // would take the question as the room and shut mid-sentence. See it.
            var gate = new CircleAI.Voice.SpeechGate { MostOpen = Longest, LearnsWhileOpen = false };

            // The last few blocks before the gate opened. A gate opens on the
            // block that clears its bar, and the attack of the first word is
            // quieter than its middle - without these it is clipped every time.
            var before = new Queue<byte[]>();
            const int BeforeBlocks = 3;     // ~300 ms at the capture's 100 ms blocks

            var started = false;
            var pcm = new float[1600];

            await foreach (var chunk in mic.CaptureAsync(deadline.Token).ConfigureAwait(false))
            {
                // THE ACKNOWLEDGEMENT IS STILL IN THE ROOM. This listens straight
                // after the phone has spoken, and the tail of its own sentence
                // reaches the microphone off the walls - which this gate would
                // otherwise open on, and call the question.
                if (CircleAI.Voice.OutLoud.Now) continue;

                var bytes = chunk.ToArray();
                var samples = bytes.Length / 2;
                if (samples > pcm.Length) pcm = new float[samples];
                for (var i = 0; i < samples; i++)
                    pcm[i] = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8)) / 32768f;

                if (gate.Wants(pcm.AsSpan(0, samples)))
                {
                    if (!started)
                    {
                        started = true;
                        foreach (var b in before) spoken.AddRange(b);
                        before.Clear();
                    }
                    spoken.AddRange(bytes);
                }
                else if (!started)
                {
                    before.Enqueue(bytes);
                    while (before.Count > BeforeBlocks) before.Dequeue();
                }

                // The gate closing after something was said is the end of the
                // question. Not "else if": a stretch that reaches Longest ends
                // on a block that WAS kept, and that is still an ending.
                if (started && gate.Closed) break;

                // AND SILENCE THAT NEVER BECOMES ANYTHING IS ITS OWN ANSWER. Without
                // this the loop runs the full Longest deadline on a room where nobody
                // spoke, which is fifteen seconds of a phone doing nothing visible
                // after it has just said hello.
                if (!started && clock.Elapsed > FirstWord) break;
            }
        }
        catch (OperationCanceledException)
        {
            // Deadline or shutdown. Whatever was said up to here is still worth asking.
        }

        Log.Info(Tag, "captured " + spoken.Count + " bytes in " + clock.ElapsedMilliseconds + " ms");
        if (spoken.Count == 0) return null;

        return await speech.TranscribeAsync(spoken.ToArray(), null, ct).ConfigureAwait(false);
    }

    /// <summary>Put the question to the brain, starting and waiting for it if need be.</summary>
    /// <remarks>
    /// THIS METHOD IS WHY "VOICE STILL DOESN'T RESPOND TO ME". It used to read
    /// <c>if (node is null || !node.IsReady) { Log.Warn("nothing to ask"); return
    /// null; }</c> - and the caller returns on a null answer, so on any phone where
    /// EMUI had killed the service the whole turn ended without a sound. The person
    /// said "Hey B", heard a hello and a beep, asked their question, and got
    /// nothing. Measured on a P30 on 2026-10-10.
    /// <para>
    /// The link path had been fixed for exactly this a day earlier and this one was
    /// not, because the rule was a private static on an Android Service that no test
    /// leg could see. <see cref="ColdTurn"/> now owns it for both, and whether a
    /// turn can end in silence is asserted on net9 and net10.
    /// </para>
    /// </remarks>
    private static async Task<string?> AskAsync(
        string question, SpokenReply mouth, CancellationToken ct)
    {
        // THROUGH THE QUEUE, LIKE EVERY OTHER QUESTION, when the host has one. The wake
        // turn used to ask the in-process node directly and contend for its lock with
        // every app's question, with no record and no priority. Now it is an ask in the
        // Assistant lane - first in line - and it is spoken as it is written.
        if (CircleNeuronService.Asks is { } front)
            return await AskThroughQueueAsync(front, question, mouth, ct).ConfigureAwait(false);

        var result = await ColdTurn.AnswerAsync(
            ready:   () => CircleNeuronService.Node?.IsReady == true,
            start:   () => CircleNeuronService.Start(global::Android.App.Application.Context),
            ask:     token => AskTheNodeAsync(question, mouth, token),

            // ENQUEUED, NOT SPOKEN. Handing the line to the same mouth is what
            // makes an overlap impossible; the newline is what makes it a complete
            // sentence, so it is taken straight away instead of waiting in the
            // buffer to be glued to the front of the answer.
            say:     (line, _) => { mouth.Push(line + "\n"); return Task.CompletedTask; },

            // THE FIRST SOUND, NOT THE LAST WORD. Without this the filler would be
            // decided on whether the whole answer had arrived, and could be queued
            // behind sentences already being spoken.
            started: mouth.Started,

            givenUp: () => CircleNeuronService.State == CircleNeuronService.ServiceState.Failed,
            log:     line => Log.Info(Tag, "the brain is " + line),
            ct:      ct).ConfigureAwait(false);

        // ALREADY IN THE QUEUE, BOTH OF THEM. ColdTurn has the mouth, so the
        // holding line and the excuse are in front of or instead of the answer by
        // the time this returns - and an empty answer is now correct rather than
        // silent, because something was said either way.
        if (result.Excuse is not null) Log.Warn(Tag, "said instead: " + result.Excuse);

        return result.Answer;
    }

    /// <summary>Who a spoken question is from, as the queue knows it.</summary>
    public const string CallerKey = "internal:assistant";

    private static string _session = Guid.NewGuid().ToString("N");
    private static DateTimeOffset _lastSpoken = DateTimeOffset.MinValue;

    /// <summary>
    /// The conversation a spoken question belongs to: the same one for five minutes, so
    /// "Hey B, and how far is that?" follows on from the question before it.
    /// </summary>
    internal static string SpokenSession()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastSpoken > TimeSpan.FromMinutes(5)) _session = Guid.NewGuid().ToString("N");
        _lastSpoken = now;
        return _session;
    }

    private static async Task<string?> AskThroughQueueAsync(
        CircleAI.Assistant.Requests.IAskFront front, string question, SpokenReply mouth, CancellationToken ct)
    {
        var admission = front.Submit(new CircleAI.Assistant.Requests.AskSubmission(
            CallerKey: CallerKey,
            ClientRequestId: Guid.NewGuid().ToString("N"),
            SessionId: SpokenSession(),
            Lane: CircleAI.Assistant.Requests.AskLane.Assistant,
            Message: question,
            Question: question,
            Spoken: true));

        if (!admission.Accepted || admission.Record is null)
        {
            mouth.Push((admission.Refusal ?? AssistantPersona.CouldNotStart) + "\n");
            return null;
        }

        var id = admission.Record.Id;
        long seq = admission.Record.Seq;
        var pushed = string.Empty;
        var held = false;
        var waiting = Stopwatch.StartNew();

        while (!ct.IsCancellationRequested)
        {
            var ask = await front.WaitForChangeAsync(CallerKey, id, seq, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            if (ask is null) return null;
            seq = ask.Seq;

            // SPEAK WHAT IS NEW. The record carries the answer so far; the mouth cleans
            // and speaks it a sentence at a time, as it did when this streamed itself.
            if (ask.Text.Length > pushed.Length && ask.Text.StartsWith(pushed, StringComparison.Ordinal))
            {
                mouth.Push(ask.Text[pushed.Length..]);
                pushed = ask.Text;
            }

            // A HOLDING LINE, ONCE, when the brain is still loading and nothing has been
            // said - the same courtesy ColdTurn gave, from the record's own state.
            if (!held && pushed.Length == 0 && waiting.Elapsed > TimeSpan.FromSeconds(4)
                && ask.State is CircleAI.Assistant.Requests.AskState.WaitingForModel)
            {
                mouth.Push(AssistantPersona.NotUpYet + "\n");
                held = true;
            }

            if (!ask.IsFinal) continue;

            if (ask.State == CircleAI.Assistant.Requests.AskState.Succeeded)
            {
                // THE FINAL TEXT IS CLEANED, THE PARTIAL WAS NOT. Whatever of it has not
                // been spoken yet is the part after what was pushed - in either form.
                var cleanPushed = PlainReply.Clean(pushed);
                if (pushed.Length == 0) mouth.Push(ask.Text);
                else if (ask.Text.StartsWith(pushed, StringComparison.Ordinal)) mouth.Push(ask.Text[pushed.Length..]);
                else if (cleanPushed.Length > 0 && ask.Text.StartsWith(cleanPushed, StringComparison.Ordinal))
                    mouth.Push(ask.Text[cleanPushed.Length..]);
                mouth.Push("\n");
                return ask.Text;
            }

            if (pushed.Length == 0)
                mouth.Push((ask.Sentence ?? CircleAI.Assistant.Requests.AskSentences.CouldNotAnswer) + "\n");
            Log.Warn(Tag, $"spoken ask {id} ended {ask.State} ({ask.Kind})");
            return null;
        }

        try { front.Cancel(CallerKey, id); } catch { /* going away */ }
        return null;
    }

    /// <summary>Stream one answer out of the resident node, speaking as it goes.</summary>
    private static async Task<string?> AskTheNodeAsync(
        string question, SpokenReply mouth, CancellationToken ct)
    {
        var node = CircleNeuronService.Node;
        if (node is null) return null;

        var answer = new System.Text.StringBuilder();
        await foreach (var chunk in node
                           .StreamAsync(new[] { new CircleAI.Hosting.Chat.ChatTurn("user", question) })
                           .ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            answer.Append(chunk);

            // PUSHED RAW, AND CLEANED A SENTENCE AT A TIME WHERE IT IS SPOKEN.
            //
            // NOT PER FRAGMENT. PlainReply.Clean returns "" for anything blank, so
            // the single-space chunk a model emits between words would vanish and
            // "Hello world" would be said as "Helloworld"; and its code-fence state
            // does not survive between calls, so a fence split across two fragments
            // corrupts both. A sentence is the smallest unit that is safe to clean,
            // and it is exactly the unit the voice is handed.
            mouth.Push(chunk);
        }

        // The same cleaning the link does, for the same reason and more so: this one
        // is only ever spoken, so an asterisk is a syllable.
        return PlainReply.Clean(answer.ToString());
    }

    /// <summary>Say it out loud.</summary>
    /// <remarks>
    /// THE SAME VOICE THE LINK SERVES, so a spoken answer and an answer read aloud in
    /// an app sound like the same assistant rather than two of them.
    /// </remarks>
    /// <summary>The greeting, already rendered, so a wake never waits for a voice to load.</summary>
    private static byte[]? _greeting;

    /// <summary>
    /// Renders the greeting before anybody says the wake word.
    /// </summary>
    /// <remarks>
    /// SEVEN SECONDS OF NOTHING, MEASURED ON THE ONE WAKE THAT FIRED IN FOUR DAYS.
    /// <para>
    ///     06:43:50  HEARD "Hey B" in "HAY B"
    ///     06:43:59  tts: 3 chars -> 626 ms audio in 4438 ms (INCLUDING model open)
    ///     06:43:59  rendered 3 chars -> 626 ms audio in 7066 ms
    /// </para>
    /// <para>
    /// A person said its name and waited seven seconds to hear "Hi." - long
    /// enough to conclude it had not heard, and long enough that whatever they
    /// said next went into a microphone that was not yet listening for it. Four
    /// of those seconds were the voice model opening for the first time: the
    /// very next sentence, twenty-one characters, took 2,7 s with the model
    /// already open.
    /// </para>
    /// <para>
    /// AckBank exists for exactly this and its header says so - "synthesising
    /// 'Yes?' at wake time would cost the same seconds it exists to cover" - but
    /// it renders through CircleAITtsProbe rather than the voice this turn speaks
    /// with, into a cache directory that belongs to whichever app process called
    /// it. Wiring it here would buy the speed with a greeting in a different
    /// voice from the answer, which its own remark warns against. So the turn
    /// warms ITS OWN voice and keeps its own greeting: rendering it once opens
    /// the model, which the engine cache then keeps open for every answer after.
    /// </para>
    /// <para>
    /// NEVER FATAL AND NEVER AWAITED BY STARTUP. A voice that cannot warm leaves
    /// the turn exactly as it was - greeting synthesised live - rather than
    /// holding up a service that has a microphone to open.
    /// </para>
    /// </remarks>
    public static async Task WarmAsync(CancellationToken ct = default)
    {
        var speech = Speech;
        if (speech is null) return;

        try
        {
            var clock = Stopwatch.StartNew();
            var pcm = await speech.SpeakAsync(AssistantPersona.Greeting, null, ct).ConfigureAwait(false);
            if (pcm.Length == 0)
            {
                Log.Warn(Tag, "greeting could not be warmed: the voice produced nothing");
                return;
            }

            _greeting = pcm;
            Log.Info(Tag, $"greeting warmed in {clock.ElapsedMilliseconds} ms - a wake will not wait for the voice to load");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn(Tag, "greeting could not be warmed: " + ex.Message); }
    }

    /// <summary>Says hello, from the warmed copy when there is one.</summary>
    private static async Task GreetAsync(CancellationToken ct)
    {
        var warm = _greeting;
        if (warm is not { Length: > 0 })
        {
            await SayAsync(AssistantPersona.Greeting, ct).ConfigureAwait(false);
            return;
        }

        await using var player = new AndroidAudioPlayer();
        await player.PlayAsync(
            warm, LinkAudioFormat.SampleRate, LinkAudioFormat.Channels, LinkAudioFormat.BitsPerSample, ct)
            .ConfigureAwait(false);
    }

    private static async Task SayAsync(string answer, CancellationToken ct)
    {
        var speech = Speech;
        if (speech is null) return;

        var pcm = await speech.SpeakAsync(answer, null, ct).ConfigureAwait(false);
        if (pcm.Length == 0)
        {
            Log.Warn(Tag, "the voice produced nothing");
            return;
        }

        await using var player = new AndroidAudioPlayer();
        // LinkAudioFormat, because that is what ServiceSpeech hands back - the same
        // 16 kHz mono 16-bit the link carries, so the spoken answer and a linked
        // app's are literally the same bytes at the same rate.
        await player.PlayAsync(
            pcm, LinkAudioFormat.SampleRate, LinkAudioFormat.Channels, LinkAudioFormat.BitsPerSample, ct)
            .ConfigureAwait(false);
    }
}
