// ScreenUpWakeWord.cs
//
// The wake word, running in this process, for the phones that will not let a
// service hold the microphone.
//
// WHY THERE HAS TO BE A SECOND ONE. The resident path is a foreground service
// with a persistent notification, which is the only durable form Android offers
// - and Huawei, Xiaomi, Oppo and Vivo stop it anyway, on their own schedule,
// whatever the notification says. DeviceSetup.AllowBackgroundAsync asks the
// owner for an exemption and a great many phones simply do not grant one. On
// those handsets the resident assistant returned Failed and that was the end of
// it: the product's headline feature, absent, on a large share of exactly the
// phones this product exists for.
//
// So: when the service cannot hold the mic, this does, for as long as somebody
// is looking at the app. It is a strictly smaller promise and the screen has to
// say so - see ResidentState.ScreenOnly.
//
// ONE MICROPHONE, TWO CONSUMERS, AND THIS IS THE WHOLE DIFFICULTY. Android hands
// out AudioRecord exclusively: while this loop holds the mic, the turn that
// follows a wake CANNOT open it. The sequence has to be strictly ordered -
// release, listen, take it back - and "release" has to mean the capture is
// actually closed, not that a token was cancelled. StopAsync therefore AWAITS
// the loop. Cancelling and returning immediately looks correct, races perfectly
// on a fast machine, and drops the first second of every wake on a slow one,
// which reads to a person as "it ignored me".
//
// Ported from the other head's HandsFree, which was proven on a P30 and is the
// only reason any of the numbers in here are trusted.

using Android.Util;
using CircleAI.Device;
using CircleAI.Voice;

namespace CircleAI.Assistant.Device;

/// <summary>Listens for the wake phrase while the app is on screen.</summary>
/// <remarks>
/// AND IT IS AN <see cref="IResidentListener"/>, WHICH IS HOW THE REST OF THE
/// PRODUCT FINDS OUT IT IS RUNNING. This loop was started directly and told nobody,
/// so <c>CircleNeuronService.IsListening</c> — the one flag three other places read
/// to answer "is the microphone open" — stayed false while the microphone was open.
/// The Settings screen showed the result side by side: "Answer to its name" ticked,
/// because that reads this object, and the Waking ability offering "Turn on",
/// because that reads the flag. Same phone, same second, two answers.
/// <para>
/// Implementing the seam rather than being adapted to it, because the seam is four
/// members this class already has in all but name, and the service needs them to
/// hold the CPU wake lock and to write the microphone disclosure on the shade —
/// neither of which a loop nobody knows about can get.
/// </para>
/// </remarks>
public sealed class ScreenUpWakeWord : IAsyncDisposable, IResidentListener
{
    private const string Tag = "CircleAI.ScreenWake";

    /// <summary>
    /// A log line with dots for decimal points, whatever the phone's locale is.
    /// </summary>
    /// <remarks>
    /// Interpolation uses the current culture, so this read "peak 0,015" on a device
    /// set to af-ZA - fine to a person, and a nuisance the moment a log is pasted
    /// into anything that parses numbers.
    /// </remarks>
    private static string Invariant(FormattableString s)
        => s.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private readonly string _bundleDir;
    private readonly string? _keywordsFile;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <param name="bundleDir">Where the wake bundle was unpacked.</param>
    /// <param name="keywordsFile">
    /// The phrase list for the chosen language, or null for the bundle's own.
    /// </param>
    public ScreenUpWakeWord(string bundleDir, string? keywordsFile = null)
    {
        _bundleDir = bundleDir;
        _keywordsFile = keywordsFile;
    }

    /// <summary>Raised off the UI thread when the phrase lands.</summary>
    public event EventHandler<string>? Woke;

    /// <inheritdoc />
    /// <remarks>
    /// THE PHRASE IS NOT KNOWN UNTIL THE SPOTTER IS BUILT, which happens on the loop
    /// thread a moment after Start returns — so this reads "its name" for that moment
    /// rather than a phrase invented here. The notification is refreshed again when
    /// the listener reports it is up, by which time this is the real list.
    /// </remarks>
    public string Describe => _describe;

    private volatile string _describe = "its name";

    /// <inheritdoc />
    /// <remarks>
    /// RAISED FROM THE CONFIRMER'S VETO, which is the one near miss this loop can
    /// actually see: a phrase that matched in full and was turned down for being too
    /// quiet, too fast or too soon after the last one. Partial matches are counted
    /// inside the spotter and never surface here, so they are not claimed.
    /// </remarks>
    public event EventHandler<ResidentNearMiss>? Nearly;

    /// <summary>True while the microphone is open for the wake phrase.</summary>
    public bool IsListening => _loop is { IsCompleted: false };

    /// <inheritdoc />
    /// <remarks>Synchronous underneath: starting is handing a loop to the thread pool.</remarks>
    public Task StartAsync(CancellationToken ct = default)
    {
        Start();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>The token is accepted for the seam; stopping is cancelling the loop.</remarks>
    public Task StopAsync(CancellationToken ct) => StopAsync();

    /// <summary>Opens the microphone. Does nothing if already listening.</summary>
    public void Start()
    {
        if (IsListening) return;

        var cts = new CancellationTokenSource();
        _cts = cts;
        _loop = Task.Run(() => ListenAsync(cts.Token));
    }

    /// <summary>Closes the microphone and waits until it is genuinely closed.</summary>
    /// <remarks>
    /// THE AWAIT IS THE POINT - see the note at the top of this file. The
    /// caller's very next act is usually to open the mic for the spoken turn,
    /// and that will fail or come up empty if this has only asked the loop to
    /// stop.
    /// </remarks>
    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;
        _cts = null;
        _loop = null;

        if (cts is null) return;

        try { cts.Cancel(); } catch (ObjectDisposedException) { return; }

        if (loop is not null)
        {
            // Never let a stuck capture wedge the app: the turn matters more than
            // a tidy shutdown, and the process reclaims the mic either way.
            try { await Task.WhenAny(loop, Task.Delay(2000)).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn(Tag, "wake loop did not stop cleanly: " + ex); }
        }

        cts.Dispose();
    }

    /// <summary>Listens by transcribing and looking for the phrase in the text.</summary>
    /// <remarks>
    /// THREE TESTED PIECES AND A LOOP. SpeechGate decides what is worth
    /// decoding, StreamingTranscriber decodes it, PhraseInSpeech decides what
    /// counts as the phrase - all three assert on both framework legs with no
    /// device. What is here is only the joining, because this file is
    /// net10.0-android and nothing in tests/ can reach it.
    /// </remarks>
    private async Task ListenByTranscribingAsync(string modelDir, CancellationToken ct)
    {
        using var asr = new StreamingTranscriber(modelDir);
        var gate = new SpeechGate();

        // THE PHRASES THE OWNER CHOSE, not a constant. The whole point of this
        // path is that they are arbitrary - read from the same file the
        // keyword spotter uses so the two paths answer to the same names.
        var phrases = WakePhrases();
        Log.Info(Tag, $"listening (transcribing) for: {string.Join(" | ", phrases)}");
        if (asr.EncoderIsQuantized)
            Log.Warn(Tag, "running the INT8 recogniser - transcripts will be rougher than float");

        _describe = phrases.Count > 0 ? phrases[0] : "its name";
        CircleNeuronService.RefreshNotification();

        asr.Grew += (_, text) =>
        {
            foreach (var phrase in phrases)
            {
                // FORGIVING, BECAUSE THE RECOGNISER GUESSES AT VOWELS.
                //
                // Measured on the P30 at 04:05 on 2026-10-12, the first time
                // this path ran on the phone: it transcribed the owner saying
                // his phrase as "ANY TIME NOW HAY B SIGNING OUT". The phrase is
                // RIGHT THERE and an exact match rejects it, because the model
                // wrote "hay" where he said "hey" - a vowel in a three-letter
                // word, with no context to constrain it.
                //
                // NearlySaid allows an edit per word in proportion to that
                // word's length, so "hay" matches "hey" and a one-letter word
                // still has to be exact. Its own tests pin both halves,
                // including that "hay bob" and "hay bill" - which this same
                // recogniser also produced - do NOT match.
                // ANYWHERE INSIDE THIS BURST, not only at its end.
                //
                // NearlySaid matches at the tail, which was right while the
                // transcript grew without bound - "hey b what is the weather"
                // contains the phrase long after the moment to wake has passed.
                // The loop now endpoints, so `text` is ONE utterance of a few
                // seconds and is forgotten at the next silence; inside that
                // window there is no moment to miss.
                //
                // MEASURED ON THE OWNER'S VOICE: "Hey Circle AI" came back as
                // "HAY CIRCLED AY HAY CIRCLE A HEY CIRCLE AI" - the phrase in
                // there twice over, where a tail rule depends on him stopping
                // on exactly the right word.
                if (!PhraseInSpeech.SaidAnywhere(text, phrase)) continue;

                // THE PHRASE, AND THE SENTENCE IT WAS IN ONLY IF SOMEBODY ASKED. The
                // phrase is ours - it is in the keyword file - and naming it is the
                // whole point of the line. The transcript around it is whatever was
                // being said in the room, and it was going to logcat on every wake.
                Log.Info(Tag, CircleAI.Voice.VoiceTrace.KeepsSpeech
                    ? $"HEARD \"{phrase}\" in \"{Tail(text)}\""
                    : $"HEARD \"{phrase}\"");
                // FORGOTTEN THE MOMENT IT FIRES. Otherwise the phrase stays in
                // the transcript and every later frame matches it again.
                asr.Reset();
                Woke?.Invoke(this, phrase);
                return;
            }
        };

        // THE LOOP THAT IS FAILING COULD NOT RECORD ITS OWN MICROPHONE.
        //
        // CaptureDump was wired into the keyword-spotter loop only, so on a
        // phone carrying the ASR bundle - which is every phone that reaches
        // this method - nothing had ever captured what this path is fed. Four
        // hours of this feature's diagnosis then ran against "wake-capture.wav"
        // and "heyb.wav": the first came off the OTHER loop, and the second is
        // the phone's own recorder app, whose ninetieth-percentile speech
        // second reads 0,0611 where this capture path reads 0,0023. Twelve to
        // twenty-nine decibels apart, same room, same voice - and every
        // threshold in this feature was calibrated against the louder one.
        //
        // Its own filename, so the two paths cannot overwrite each other's
        // evidence. Off unless the marker file is present, because it records a
        // microphone; the external files directory is deliberate, so the owner
        // can see it, copy it off and delete it without adb.
        using var dump = CircleAI.Voice.CaptureDump.IfAsked(
            global::Android.App.Application.Context
                .GetExternalFilesDir(null)?.AbsolutePath ?? string.Empty,
            "transcribing-wake.wav",
            line => Log.Warn(Tag, line));

        // WHAT THIS ROOM ACTUALLY SAYS, kept on the phone - WHEN SOMEBODY HAS
        // ASKED FOR IT. Scoring a candidate wake phrase needs a corpus of the
        // speech it has to ignore, and logcat cannot hold one - see UtteranceLog,
        // which is now behind the same marker file as the audio dump, for the
        // same reason: it is a transcript of somebody's home and it was being
        // written with nothing said and nothing asked.
        var said = new CircleAI.Voice.UtteranceLog(
            global::Android.App.Application.Context
                .GetExternalFilesDir(null)?.AbsolutePath);
        if (said.Path is not null) Log.Info(Tag, $"utterances kept at {said.Path}");

        await using var mic = new AndroidAudioCapture();
        var pcm = new float[1600];
        var chunks = 0L;
        var skippedSpeaking = 0L;
        var utterances = 0L;
        var deaf = false;

        // The counters as of the last report, so the report can state the rate
        // over THAT HALF MINUTE rather than over the life of the process - see
        // where they are used.
        var wasDecoded = 0L;
        var wasSkipped = 0L;
        var told = System.Diagnostics.Stopwatch.StartNew();

        await foreach (var chunk in mic.CaptureAsync(ct).ConfigureAwait(false))
        {
            var samples = chunk.Length / 2;
            if (samples > pcm.Length) pcm = new float[samples];
            var span = chunk.Span;
            for (var i = 0; i < samples; i++)
                pcm[i] = (short)(span[i * 2] | (span[i * 2 + 1] << 8)) / 32768f;

            chunks++;

            // EVERYTHING THE MICROPHONE DELIVERS, BEFORE THE GATE JUDGES IT.
            // Kept ahead of the gate on purpose: the question this recording
            // exists to answer is whether a voice arrives at all, and a dump
            // fed from behind the gate can only ever contain what the gate
            // already let through.
            dump?.Keep(pcm.AsSpan(0, samples));

            // IT DOES NOT LISTEN TO ITSELF.
            //
            // MEASURED: the wake loop's own capture, pulled off this phone on
            // 2026-10-12, transcribed as "I AM USING THE SMALLER BRAIN THE BIG
            // ONE STOPPED THIS PHONE SO I LEFT IT ALONE". That is the phone's
            // own text-to-speech, out of its own speaker, into its own
            // microphone, read back perfectly. The assistant says its own name
            // often enough that this is a self-wake waiting to happen, and it
            // spends exactly the battery the gate was added to save.
            //
            // THE STREAM RESTARTS AFTERWARDS, not just skips. Whatever the
            // recogniser had half-decoded when the assistant started talking is
            // describing audio that is now minutes stale, and the reverberant
            // tail of a sentence is itself the length of a wake phrase.
            if (OutLoud.Now)
            {
                if (!deaf) { deaf = true; asr.Reset(); }
                skippedSpeaking++;
                continue;
            }

            if (deaf) { deaf = false; asr.Reset(); }

            // THE GATE IS WHAT MAKES A 20M MODEL AFFORDABLE. A room is silent
            // almost all of the time, and this model is six times the one it
            // replaces.
            //
            // AND EVERY BURST IS A FRESH STREAM, WHICH IS NOT A DETAIL.
            //
            // A transducer carries encoder state from chunk to chunk and that
            // state assumes the audio is CONTINUOUS. Gating the input hands it
            // a cut: the silence between two bursts never reaches it, so it
            // decodes the second burst as though it followed the first without
            // a pause, and the state is wrong from then on.
            //
            // Measured on the P30 at 04:15 on 2026-10-12, the first run with
            // the gate: six minutes of a person talking produced the transcript
            // "TABY A B". The same model on a recorded file, ungated, read the
            // same voice legibly - "hey circle a", "hay bob", "hey alexe hey
            // alexa". The model was not worse on the phone; it was being fed
            // audio with the gaps cut out of it.
            //
            // So the stream restarts when the gate opens. A wake phrase lives
            // entirely inside one burst, so nothing is lost by forgetting what
            // came before it - and the hangover is what keeps a burst from
            // being cut in half.
            //
            // THE GATE REPORTS ITS OWN EDGE rather than this loop inferring it
            // from two reads of Open. The gate also cuts a stretch that runs
            // too long, and a caller comparing before-and-after would miss a
            // close and a re-open inside one block.
            if (gate.Wants(pcm.AsSpan(0, samples)))
            {
                if (gate.Opened) asr.Reset();
                asr.AcceptWaveform(pcm.AsSpan(0, samples));
            }

            // NOT "else if". A STRETCH CAN END ON A BLOCK THAT WAS DELIVERED.
            //
            // The gate cuts a stretch that has run too long AFTER passing the
            // block that reached the limit, so on that block Wants returns TRUE
            // and Closed is also true. Written as an else-if, this branch never
            // runs for a cut stretch at all - and the cut ones are the LONG
            // ones, so exactly the utterances most likely to contain a phrase
            // were the ones never committed and never logged.
            //
            // Measured on the owner's voice, 2026-10-12: of seven utterances
            // only three were reported, and the missing four included "HAY
            // CIRCLED AY HAY CIRCLE A HEY CIRCLE AI" - the wake phrase, said
            // clearly, thrown away by an else.
            if (gate.Closed)
            {
                // THE UTTERANCE IS OVER, SO ASK WHAT IT WAS - AND WRITE IT DOWN.
                //
                // TWO THINGS HAPPEN HERE AND BOTH WERE MISSING.
                //
                // The first is a FIX. A transducer holds its last token until
                // something tells it the audio has stopped; while blocks keep
                // arriving it is always waiting for more context. The loop fed
                // it and never once asked it to finish, so the final token of
                // every phrase stayed uncommitted - and "Hey B" without its "B"
                // matches nothing. That is not a guess about this product, it
                // is its history: the keyword spotter this replaced reached two
                // of three tokens 1,796 times and completed zero.
                //
                // The second is the LOG. Until now this loop printed a snapshot
                // every thirty seconds showing only whatever the transcript
                // happened to hold at that instant, so a phrase spoken twenty
                // seconds earlier was already overwritten - and this phone's
                // logcat buffer rolls about every four minutes on top of that.
                // Asked "did he say it and we missed it, or did he not say it",
                // the log could not tell the difference. That is the same
                // defect as a level that hides a dead feature: a line that
                // cannot distinguish two causes is not a line.
                //
                // So every utterance is reported as it ends, with what it was
                // decoded to. A quiet room produces about one a minute.
                asr.Flush();

                var heard = asr.Text.Trim();
                if (heard.Length > 0)
                {
                    utterances++;

                    // EVERY UTTERANCE, WORD FOR WORD, INTO LOGCAT - which is what this
                    // was, on any phone that had the streaming recogniser on it, with
                    // nothing said and nothing asked. The count and the length are what
                    // the line is read for ("is it decoding anything at all"); the words
                    // need the deliberate act (CaptureDump's marker) that the audio dump
                    // beside them has always needed.
                    Log.Info(Tag, CircleAI.Voice.VoiceTrace.KeepsSpeech
                        ? Invariant($"heard #{utterances} ({gate.LastStretch.TotalSeconds:F1} s): \"{heard}\"")
                        : Invariant($"heard #{utterances} ({gate.LastStretch.TotalSeconds:F1} s): {heard.Length} chars"));

                    // AND KEPT WHERE LOGCAT CANNOT ROLL IT AWAY. The line above
                    // survives about four minutes on this phone. The service
                    // ran four days and decoded 765 utterances; thirty-six of
                    // them survived anywhere, and that was not enough to settle
                    // whether a candidate wake phrase fires on ordinary speech.
                    said.Add(heard, gate.LastStretch, DateTimeOffset.Now);
                }

                asr.Reset();
            }

            // WHAT IT COST AND WHAT IT HEARD, periodically. A gate that quietly
            // passes everything has bought nothing, and a transcriber that
            // quietly produces nothing looks exactly like a quiet room - which
            // is the failure this whole change came out of.
            //
            // AND THE LEVELS IT IS WORKING AT, which the first version of this
            // line did not print. It reported "decoded 87 %" for half an hour
            // and that number alone cannot say WHY: a gate open too often looks
            // identical whether the room is loud or the threshold is wrong. The
            // floor is what the gate learnt the room to be, the opening is what
            // it is therefore demanding, and cut counts the stretches that ran
            // so long they had to be broken up - the signature of a room
            // beating the gate rather than a person talking to it.
            if (told.Elapsed >= TimeSpan.FromSeconds(30))
            {
                told.Restart();
                // ONE INTERPOLATED STRING, not three concatenated. Invariant
                // takes a FormattableString and concatenation collapses to a
                // plain string, so splitting this for readability stops it
                // compiling - and on this box the default culture writes
                // "0,0040", which is what Invariant is here to prevent.
                // SKIPPED-WHILE-SPEAKING IS A COUNT AND SO IS WHAT IT SPOKE.
                // "0 skipped" could mean the assistant never spoke or could
                // mean this is not wired, and those two read identically -
                // which is how it went unnoticed in the first place. Printing
                // both numbers makes the difference visible.
                //
                // AND THE DUTY CYCLE IS FOR THIS HALF MINUTE, NOT FOR EVER.
                // SpeechGate.DutyCycle is a lifetime ratio, and on 2026-10-12
                // it sat at a steady "35 %" for several reports across a
                // stretch where the app had taken the microphone and the
                // service was being handed silence. A lifetime average cannot
                // move fast enough to show anything going wrong, which is the
                // one job this number has.
                var dDecoded = gate.Decoded - wasDecoded;
                var dSkipped = gate.Skipped - wasSkipped;
                wasDecoded = gate.Decoded;
                wasSkipped = gate.Skipped;
                var window = dDecoded + dSkipped == 0 ? 0 : dDecoded / (double)(dDecoded + dSkipped);

                Log.Info(Tag, Invariant(
                    $"transcribing: {chunks} chunks, decoded {window:P0} of the last 30 s ({gate.DutyCycle:P0} all told) | room {gate.Floor:F4} loudest {gate.Loudest:F4} opens at {gate.Opening:F4} | {gate.Cut} cut | {utterances} utterances | {skippedSpeaking} skipped while it spoke ({OutLoud.Spoken} sentences)"));
            }
        }
    }

    /// <summary>The tail of a transcript, for a log line.</summary>
    private static string Tail(string? text)
    {
        var t = (text ?? string.Empty).Trim();
        return t.Length <= 60 ? t : "..." + t[^60..];
    }

    /// <summary>The phrases this phone answers to, from the owner's own file.</summary>
    /// <remarks>
    /// READ FROM THE KEYWORD FILE so both paths answer to the same names. The
    /// file is sherpa's format - BPE tokens, then optional :boost, #threshold
    /// and @phrase - and the @label is the text a person actually typed, which
    /// is the only part a transcript match can use.
    /// </remarks>
    private System.Collections.Generic.List<string> WakePhrases()
    {
        var found = new System.Collections.Generic.List<string>();
        try
        {
            if (_keywordsFile is null || !File.Exists(_keywordsFile)) return found;

            foreach (var line in File.ReadLines(_keywordsFile))
            {
                var at = line.IndexOf('@');
                if (at < 0) continue;
                var label = line[(at + 1)..].Trim().Replace('_', ' ');
                if (label.Length > 0) found.Add(label);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, "could not read the wake phrases: " + ex.Message);
        }

        return found;
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        try
        {
            // THE TRANSCRIBING PATH, WHEN A MODEL FOR IT IS ON THE PHONE.
            //
            // The keyword spotter below is 3.3 MILLION parameters, which is the
            // whole of sherpa-onnx's published keyword shelf - there is no
            // larger one to move to. Measured on the owner's own voice on
            // 2026-10-12 it found his phrase four times out of roughly twenty
            // spoken close to the microphone, and never once at normal holding
            // distance.
            //
            // A 20M streaming recogniser plus a phrase match does the same job
            // open-vocabulary, which is the requirement that rules out every
            // fixed-phrase engine: Africa has roughly two thousand languages
            // and one trained model per phrase does not scale. On that same
            // recording it woke on "hey circle" three times, "hey alexa" once
            // and "hey b" once, from ONE model with no training, and refused a
            // phrase that was never spoken.
            //
            // BOTH PATHS STAY, and the model's presence chooses. A phone
            // without the ASR bundle keeps exactly the behaviour it has, and
            // the two can be compared on the same device on the same day -
            // which is the comparison that should have been run before any of
            // this was chosen.
            var asrDir = TranscribingWake.BundleDir();
            if (asrDir is not null)
            {
                await ListenByTranscribingAsync(asrDir, ct).ConfigureAwait(false);
                return;
            }

            using var kws = new ConfirmedKeywordSpotter(
                new ZipformerKwsSpotter(_bundleDir, _keywordsFile));

            Log.Info(Tag, $"listening for: {string.Join(" | ", kws.Keywords)}");

            // AND WHETHER IT CAN POSSIBLY HEAR THEM.
            //
            // The spotter writes its own line through VoiceTrace, but that is a
            // different logcat tag and this is the line everybody greps.
            //
            // AND IT USED TO BLAME THE ENCODER FOR SOMETHING THE ENCODER DID NOT DO.
            //
            // It read: "running the INT8 encoder - keyword tokens score ~10x lower on
            // it and the phrase may never complete", over a comment that attributed
            // the 2026-10-11 P30 session - the phrase reaching two of its three tokens
            // 1,796 times and completing zero - to the quantized encoder.
            //
            // The stalls were real. The attribution was not.
            // EncoderPrecisionOnRealAudioTests ran the controlled A/B through this
            // exact decoder on upstream's own audio:
            //
            //   0.wav  FLOAT fired 1 (4/4 p=0,7412)   INT8 fired 1 (4/4 p=0,5656)
            //   1.wav  FLOAT fired 2 (5/5 p=0,6263)   INT8 fired 2 (5/5 p=0,5352)
            //
            // int8 found every keyword float did, with margin over the 0,20 gate. What
            // the 1,796 stalls actually were is in CapturePolicy's header: this capture
            // hands the spotter median per-second RMS 0,0014 where the phone's own
            // recorder app gets 0,0446 in the same room, and our loudest second is
            // below what an empty room measures. The phrase stalled because the audio
            // was not there, not because the encoder was quantized.
            //
            // The warning stays, because a precision the caller did not ask for is
            // worth knowing. It no longer predicts a failure it was not the cause of.
            if (kws.EncoderIsQuantized)
                Log.Warn(Tag, "running the INT8 encoder - measured at p=0,54-0,57 on upstream's "
                            + "own audio against float's 0,63-0,74, so it clears the 0,20 gate "
                            + "with less headroom for a quiet or distant voice. This bundle ships "
                            + "no float encoder. NOT on its own a reason the phrase cannot fire.");

            // A PHRASE THAT CAN NEVER FIRE SAYS SO, ONCE, LOUDLY.
            //
            // ShadowedKeywords is computed by the spotter and nothing on this path
            // has ever read it. A shadowed phrase is swallowed by a shorter one
            // that is a prefix of it, so the graph can never reach its end state -
            // and the symptom is indistinguishable from a quiet room: the line
            // above prints the phrase, the microphone reports healthy, and nothing
            // ever happens. ZipformerWakeWordDetector warns about this and is dead
            // code on this head, so the warning reached nobody.
            foreach (var (phrase, by) in kws.ShadowedKeywords)
                Log.Warn(Tag, $"\"{phrase}\" can NEVER fire - \"{by}\" is a prefix of it");

            // WHAT THE SHADE SAYS IT IS LISTENING FOR. The notification is written
            // from Describe, and until the spotter existed there was no phrase to
            // put in it - so the disclosure named the phrase only by luck of timing.
            _describe = kws.Keywords.Count > 0 ? kws.Keywords[0] : "its name";
            CircleNeuronService.RefreshNotification();

            kws.Woke += (_, d) =>
            {
                Log.Info(Tag, $"HEARD \"{d.Phrase}\" p={d.Probability:F4} @{d.AtFrame}");
                Woke?.Invoke(this, d.Phrase);
            };
            kws.Rejected += (_, r) =>
            {
                Log.Info(Tag, $"VETOED \"{r.Detection.Phrase}\" — {r.Reason}");

                // A COMPLETE MATCH, REFUSED - which is what ResidentNearMiss.Refused
                // is for, and the only thing that distinguishes "stand closer" from
                // "say it again more clearly" on a screen watching the listener.
                var tokens = kws.TokenCountOf(r.Detection.Phrase);
                Nearly?.Invoke(this, new ResidentNearMiss(
                    r.Detection.Phrase, tokens, tokens, r.Detection.Probability, r.Reason));
            };

            // AND, IF ASKED, A COPY OF EXACTLY WHAT THE SPOTTER IS GIVEN.
            //
            // Off unless a marker file is present, because it records a
            // microphone. The external files directory is used deliberately: the
            // owner can see it, copy it off and delete it without adb, which
            // internal storage on a release build does not allow.
            using var dump = CircleAI.Voice.CaptureDump.IfAsked(
                global::Android.App.Application.Context
                    .GetExternalFilesDir(null)?.AbsolutePath ?? string.Empty,
                "wake-capture.wav",
                line => Log.Warn(Tag, line));

            await using var mic = new AndroidAudioCapture();
            var pcm = new float[1600];

            // A HEARTBEAT, BECAUSE SILENCE HAS TWO CAUSES AND THEY LOOK THE SAME.
            // Nothing is logged unless the phrase fires or the confirmer vetoes
            // it, so a phone that hears nothing and a phone that is not listening
            // at all produce identical logs - which is exactly the position the
            // other head was in when the wake word stopped responding and every
            // structural check said it was fine.
            var chunks = 0L;
            var since = 0L;
            var peak = 0f;
            var sum = 0.0;

            // HOW OFTEN THIS SAYS SO. The decision lives in CircleAI.Assistant
            // because this library is net10.0-android and no test project can
            // reference one of those - the exact wall OPEN-GAPS records for
            // ModelChoice, where "the one rule that decides what a person is
            // offered was the one rule nothing asserted".
            var watch = new CircleAI.Assistant.MicWatch();

            // AND HOW CLOSE THE MODEL CAME, WHICH IS THE HALF THIS LOOP WAS STILL
            // MISSING.
            //
            // The heartbeat above answers "is the microphone alive". It cannot
            // answer "is the model hearing anything", and those are different
            // questions with the same silence. Measured on a P30 on 2026-10-11: a
            // person said "Hey B" several times, the microphone reported peak
            // 0.453 against a 0.065 quiet baseline - so the audio unquestionably
            // arrived - and the entire capture contained no score, no near miss
            // and no detection. Nothing in 157,000 lines of logcat could say
            // whether the spotter reached 2 of 3 tokens at p=0,19 or scored
            // nothing at all, and those two call for opposite actions: lower the
            // gate, or fix the phrase.
            //
            // THE NUMBER WAS ALREADY BEING COMPUTED. ConfirmedKeywordSpotter keeps
            // the deepest sighting in _best on every frame; TakeBestProgress is
            // the only way to read it and its sole caller in the repository is
            // ZipformerWakeWordDetector, which is dead code on this head. So the
            // evidence existed, was never read, and was overwritten every frame -
            // the repository's signature defect, on the headline feature.
            //
            // ON ITS OWN CLOCK, DELIBERATELY. Riding the heartbeat's switch would
            // inherit MicWatch's backoff - 5 s, 30 s, 2 min, then ten minutes for
            // ever - so a phrase tried six times in a minute would print once. A
            // near miss is news every time it happens.
            var told = System.Diagnostics.Stopwatch.StartNew();
            var quiet = CircleAI.Assistant.MicWatch.NearMissGap;

            // THE LOUDEST THING IN THIS WINDOW, kept separately from the
            // heartbeat's peak because the two windows close on different clocks
            // and sharing one would hand each the other's silence.
            var loudest = 0f;

            await foreach (var chunk in mic.CaptureAsync(ct).ConfigureAwait(false))
            {
                // PCM16 little-endian to float in [-1, 1]. NOT scaled to the
                // int16 range: KaldiFbank takes normalised samples, and
                // multiplying here is exactly the bug that made the wake word
                // deaf for a day.
                var samples = chunk.Length / 2;
                if (samples > pcm.Length) pcm = new float[samples];
                var span = chunk.Span;
                for (var i = 0; i < samples; i++)
                    pcm[i] = (short)(span[i * 2] | (span[i * 2 + 1] << 8)) / 32768f;

                for (var i = 0; i < samples; i++)
                {
                    var a = Math.Abs(pcm[i]);
                    if (a > peak) peak = a;
                    if (a > loudest) loudest = a;
                    sum += a;
                }

                chunks++;
                since += samples;

                // NO GAIN HERE, AND THAT IS A MEASUREMENT RATHER THAN AN OVERSIGHT.
                //
                // SpeechGain was wired in at this line on 2026-10-11 because its
                // header documents 8 of 8 tokens at 5 cm against 1 of 8 at arm's
                // length, and because its only caller was dead code. It was then
                // taken out again, on evidence:
                //
                // A 55,6 s recording of the owner saying "Hey B!" at normal
                // distance (peak 0,449) was run through the real decoder in the
                // device's exact configuration - int8 encoder, two-stage
                // confirmer, the shipped keyword. It fired 8 times. Scaled DOWN
                // to peak 0,200, the band the wake loop was reporting while it
                // failed, it still fired 8 times. With SpeechGain applied on top,
                // 9. Level is not what is stopping this phrase.
                //
                // And the gain is not free: SpeechGain's own remarks record a
                // clearly spoken phrase arriving at peak 0,719 while the follower
                // still carried x1,8, hard-limiting to a square-topped waveform
                // the spotter scored at 1 token of 8. Adding a known clipping
                // risk to buy a measured nothing is a bad trade.
                //
                // See HeyBOnRealVoiceTests. If the capture path turns out to be
                // delivering something quieter or dirtier than the recorder app
                // does, the answer is to fix the capture, not to amplify it
                // afterwards.
                // THE SAME SPAN THE SPOTTER GETS, on the same line, so the file
                // cannot drift from what was actually scored.
                dump?.Keep(pcm.AsSpan(0, samples));

                kws.AcceptWaveform(pcm.AsSpan(0, samples));

                // HOW CLOSE IT CAME, BEFORE THE HEARTBEAT'S OWN SWITCH, so the
                // near miss is reported on every window rather than on MicWatch's
                // backoff. Reading clears it, so nothing is counted twice.
                //
                // THREE OUTCOMES, AND ALL THREE SAY SOMETHING. The first version of
                // this printed only the near miss, and its comment claimed a silent
                // window "is itself the answer" - which is the very defect this
                // line exists to remove, wearing a different hat. A blank log would
                // have meant "nobody spoke" OR "the model matched nothing" OR "the
                // diagnostic is broken", and the whole reason for adding it was
                // that those three were indistinguishable. A silent zero is not a
                // quiet room; the fix is a count, not a level.
                if (told.Elapsed >= quiet)
                {
                    told.Restart();
                    var spoke = loudest;
                    loudest = 0f;

                    // EVERY PHRASE, NOT THE BEST ONE. They all start with the same
                    // word, so they all sit at one matched token and a single-best
                    // report names whichever it saw first - which on this device
                    // meant "Hey Circle AI" was never printed once while "Hey B"
                    // was printed hundreds of times, and the obvious question
                    // ("is the long one progressing?") had no answer in the log.
                    var seen = kws.TakeBestPerPhrase();

                    foreach (var near in seen)
                    {
                        // ONE INTERPOLATED LITERAL, NOT TWO CONCATENATED. Invariant
                        // takes a FormattableString and `$"a" + $"b"` is already a
                        // string by then - which matters here and nowhere else in
                        // this file, because these are the only decimals it prints
                        // and this machine writes them with a comma.
                        Log.Info(Tag, Invariant(
                            $"nearly \"{near.Phrase}\" {near.Matched}/{near.Total} tokens p={near.MeanProbability:F4} (needs {ZipformerKwsSpotter.MeasuredThreshold:F2})"));

                        // AND THE SCREEN HEARS ABOUT IT TOO. ResidentNearMiss used
                        // to be raised only for a COMPLETE match the confirmer
                        // vetoed - the rarest case - so a listener watching this
                        // was told nothing on the common one.
                        Nearly?.Invoke(this, new ResidentNearMiss(
                            near.Phrase, near.Matched, near.Total, near.MeanProbability, null));
                    }

                    if (seen.Count == 0 && spoke >= CircleAI.Assistant.MicWatch.SpeechPeak)
                    {
                        // SOMEBODY SPOKE AND NOT ONE TOKEN MATCHED, which is a
                        // different fault from a near miss and needs a different
                        // answer: a near miss says lower the gate, this says the
                        // model is not hearing the phrase at all.
                        //
                        // The spotter computes nothing when no token matches - the
                        // progress block is guarded on Level > 0 - so without this
                        // branch there is no number to print and the window goes
                        // quiet, which is exactly the hole being closed.
                        Log.Info(Tag, Invariant(
                            $"speech at peak {spoke:F3} and NOT ONE token of \"{_describe}\" matched"));
                    }
                }

                // SILENCE IS THE NEWS, NOT THE HEARTBEAT. A dead microphone
                // delivers zeros; a quiet room delivers a peak around 0.015 with a
                // mean near 0.0018, measured on a P30 on 2026-10-04 - two orders of
                // magnitude apart, so the two cases the remark above cares about are
                // distinguishable from the signal itself and do not need a line
                // every five seconds to tell them apart.
                var mean = since > 0 ? sum / since : 0.0;
                switch (watch.Observe(samples, peak))
                {
                    case CircleAI.Assistant.MicSay.Nothing:
                        continue;

                    case CircleAI.Assistant.MicSay.WentSilent:
                        Log.Warn(Tag, Invariant(
                            $"mic SILENT: {chunks} chunks and no signal at all - peak {peak:F3}"));
                        break;

                    case CircleAI.Assistant.MicSay.StillSilent:
                        Log.Warn(Tag, Invariant(
                            $"mic STILL silent: {chunks} chunks, nothing arriving"));
                        break;

                    case CircleAI.Assistant.MicSay.CameBack:
                        Log.Info(Tag, Invariant(
                            $"mic back: {chunks} chunks, peak {peak:F3}, mean {mean:F4}"));
                        break;

                    default:
                        Log.Info(Tag, Invariant(
                            $"mic alive: {chunks} chunks, peak {peak:F3}, mean {mean:F4}"));
                        break;
                }

                since = 0; peak = 0f; sum = 0;
            }
        }
        catch (OperationCanceledException)
        {
            // Ordinary stop.
        }
        catch (Exception ex)
        {
            // A wake word that cannot start must not take the app down with it;
            // the circle still works by tap.
            Log.Error(Tag, "wake loop failed: " + ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
