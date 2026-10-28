#if IT_VOICE_ANDROID
#nullable enable

// AndroidAudio.cs
//
// The microphone and the speaker. VoiceLoop needs a real IAudioCapture and
// IAudioPlayer; only the Null implementations existed, so the hands-free loop
// had no ears and no mouth on a phone.
//
// AudioRecord  -> PCM16 16 kHz mono (exactly AudioFormat.Pcm16Mono16k, which is
//                 what IVoiceTranscriber and the wake detector expect).
// AudioTrack   -> plays the PCM the TTS engine produces, at ITS sample rate
//                 (Piper is 22050, not 16000 — never assume they match).
//
// Needs RECORD_AUDIO permission at runtime; MainActivity requests it.

using System.Runtime.CompilerServices;
using Android.Media;
using CircleAI.Voice;

// BOTH namespaces declare an AudioFormat, and this file genuinely needs both:
// Android.Media.AudioFormat (the AudioTrack builder) and CircleAI.Voice.AudioFormat
// (the IAudioCapture.Format contract). Unqualified it is CS0104 — alias the
// CircleAI one so the Android SDK type keeps its familiar bare name.
using VoiceFormat = CircleAI.Voice.AudioFormat;

namespace CircleAI.Assistant.Device;

/// <summary>Microphone capture as PCM16 16 kHz mono.</summary>
public sealed class AndroidAudioCapture : IAudioCapture
{
    private const int SampleRate = 16_000;
    private AudioRecord? _record;

    public VoiceFormat Format { get; } = VoiceFormat.Pcm16Mono16k;

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var min = AudioRecord.GetMinBufferSize(SampleRate, ChannelIn.Mono, Encoding.Pcm16bit);
        if (min <= 0) min = SampleRate; // conservative fallback
        var bufferSize = min * 2;

        // VOICE_RECOGNITION, NOT MIC — and this is worth the paragraph, because
        // MEASURED it is the difference between a wake word and a decoration.
        //
        // AudioSource.Mic hands back one raw capsule with no processing. Played
        // through a room at desk distance, "Hey B" was heard ONE time in TEN at
        // full volume and never once at any attenuation — while the very same
        // audio fed from a file scored 0.39 and fired every time. Every number
        // measured on files was, in that sense, meaningless.
        //
        // VoiceRecognition is the source Android tunes for exactly this job: on a
        // multi-microphone phone it engages the array and its far-field
        // processing, and unlike VoiceCommunication it does not apply the
        // aggressive comms-grade suppression that eats the consonants a keyword
        // spotter needs. It is one constant, and it is the single highest-value
        // line in this file.
        // SWITCHABLE SO IT CAN BE MEASURED, AND THE DEFAULT IS UNCHANGED.
        //
        // Measured on a P30 on 2026-10-12: this capture wrote down the exact
        // samples it handed the spotter while its owner said "Hey B!" at normal
        // distance, and the phone's own recorder app recorded the same phrase in
        // the same room four minutes later:
        //
        //     recorder app   peak 0,449   median per-second RMS 0,0446
        //     this capture   peak 0,187   median per-second RMS 0,0014
        //
        // Our loudest second was 0,0080, below the 0,0083 an empty room
        // measures. The recorder's audio fires the shipped keyword eight times
        // through the identical decoder; ours fires none, and could not.
        //
        // The paragraph above is not being overruled on that - it carries its
        // own measurement and may still be right. CapturePolicy only makes the
        // combinations selectable by marker file, so which part of this chain
        // costs 30 dB is answered by captures rather than by argument.
        var policy = CircleAI.Voice.CapturePolicy.From(
            global::Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath);

        _record = new AudioRecord(
            policy.RawMic ? AudioSource.Mic : AudioSource.VoiceRecognition,
            SampleRate, ChannelIn.Mono, Encoding.Pcm16bit, bufferSize);

        if (_record.State != State.Initialized)
        {
            // SAID OUT LOUD. This used to yield break in silence, so a microphone
            // the platform had refused was indistinguishable from a quiet room -
            // the wake word simply never fired and nothing anywhere explained why.
            Android.Util.Log.Error(Tag,
                $"AudioRecord did not initialise (state={_record.State}) - no capture. "
                + "Permission, a mic held by another app, or an unsupported format.");
            _record.Release();
            _record = null;
            yield break;
        }

        AttachFarFieldEffects(_record.AudioSessionId, policy);
        _record.StartRecording();

        // AND NOW THE SESSION IS LIVE, WHICH IS WHEN AN EFFECT THAT REFUSED THE
        // FIRST TIME WILL TAKE. Attaching before StartRecording is correct - the
        // session id has to exist - but enabling before it is not reliable, and
        // the old code could not tell because it discarded setEnabled's status.
        ConfirmFarFieldEffects();

        var buffer = new byte[3200]; // 100 ms at 16 kHz mono 16-bit
        var empty = 0;               // consecutive reads that returned nothing

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await _record.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read <= 0)
                {
                    // A CAPTURE THAT DELIVERS NOTHING IS NOT A QUIET ROOM. This
                    // loop used to spin here forever without a word, which is how
                    // a microphone that opens and then hands back nothing looked
                    // exactly like silence. Reported once, then every ~5 s, so it
                    // names itself without filling the log.
                    if (++empty == 1 || empty % 500 == 0)
                        Android.Util.Log.Warn(Tag,
                            $"capture returned no audio {empty} times in a row (read={read})");
                    await Task.Delay(10, ct).ConfigureAwait(false);
                    continue;
                }

                if (empty > 0)
                {
                    Android.Util.Log.Info(Tag, $"capture recovered after {empty} empty reads");
                    empty = 0;
                }

                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                yield return chunk;
            }
        }
        finally
        {
            try { _record?.Stop(); } catch { }
        }
    }

    private const string Tag = "CircleAI.Audio";

    private readonly List<Android.Media.Audiofx.AudioEffect> _effects = new();

    /// <summary>
    /// Turns on the platform's gain and noise handling for this capture session.
    /// </summary>
    /// <remarks>
    /// AUTOMATIC GAIN CONTROL IS THE ONE THAT BUYS DISTANCE. Speech falls off with
    /// the square of it, so a voice four metres away arrives at a sixteenth of the
    /// power it has at one — and without AGC that arrives as a near-silent
    /// waveform whose log-mel features look nothing like the training data.
    /// <para>
    /// Every one of these is OPTIONAL on Android and absent on plenty of phones,
    /// which is why each is attempted separately and a failure is shrugged off. A
    /// missing effect must degrade the range, never take the microphone down with
    /// it.
    /// </para>
    /// </remarks>
    private void AttachFarFieldEffects(int sessionId, CircleAI.Voice.CapturePolicy policy)
    {
        // NAMED, NOT COUNTED, AND THE ABSENT ONES NAMED TOO.
        //
        // This used to log "capture: VoiceRecognition + 2 effect(s)" and nothing
        // else, and the IsAvailable guards below skip an unavailable effect in
        // COMPLETE SILENCE - Try only ever spoke when Create threw. So a phone
        // missing the one effect that buys distance looked identical to a phone
        // that had it, and the count could not tell them apart.
        //
        // On a P30 on 2026-09-06, chasing a wake word that would not fire, the
        // question "is AGC on this phone?" could only be answered by reading
        // `dumpsys media.audio_flinger` and matching effect UUIDs by hand. It was
        // on. Knowing that cost half an hour and should have cost one log line.
        var on = new List<string>();
        var off = new List<string>();

        void Try(string what, bool available, Func<Android.Media.Audiofx.AudioEffect?> make)
        {
            if (!available) { off.Add(what); return; }

            try
            {
                var fx = make();
                if (fx is null) { off.Add($"{what} (create returned nothing)"); return; }

                // KEPT BEFORE THE ENABLE CAN FAIL. A created effect has to be
                // released on dispose or it holds the session; reporting it as off
                // while leaking it trades one silent fault for another.
                _effects.Add(fx);
                fx.SetEnabled(true);

                // "ATTACHED" IS NOT "ENABLED", AND THIS LINE REPORTED THE FIRST AS
                // THE SECOND. The old body discarded SetEnabled's status and added
                // the effect to `on` on the strength of Create not throwing.
                // AudioEffect.setEnabled returns a status and fails routinely with
                // ERROR_INVALID_OPERATION when another client owns the effect or
                // the session is not live yet - so the log printed
                // "on=[AGC, echo canceller]" over a capture that had neither.
                //
                // fx.Enabled is read back because it is the only answer that is
                // not a guess.
                if (fx.Enabled) on.Add(what);
                else off.Add($"{what} (attached but REFUSED to enable)");
            }
            catch (Exception ex)
            {
                off.Add($"{what} ({ex.Message})");
            }
        }

        if (!policy.NoEffects)
        {
            Try("AGC", Android.Media.Audiofx.AutomaticGainControl.IsAvailable,
                () => Android.Media.Audiofx.AutomaticGainControl.Create(sessionId));
            Try("noise suppressor", Android.Media.Audiofx.NoiseSuppressor.IsAvailable,
                () => Android.Media.Audiofx.NoiseSuppressor.Create(sessionId));

            // Echo cancellation matters once the device can talk back: without it the
            // assistant's own voice reaches the microphone and it answers itself.
            //
            // AND IT IS THE STANDING SUSPECT FOR THE 30 dB THIS CAPTURE LOSES. An
            // AEC is built for a voice call and expects a playback reference to
            // cancel against; given none, some implementations suppress the near
            // end instead. Detachable on its own so that can be tested without
            // also giving up AGC and noise suppression.
            if (!policy.NoEchoCanceller)
            {
                Try("echo canceller", Android.Media.Audiofx.AcousticEchoCanceler.IsAvailable,
                    () => Android.Media.Audiofx.AcousticEchoCanceler.Create(sessionId));
            }
            else off.Add("echo canceller (detached by marker)");
        }
        else
        {
            off.Add("ALL effects (detached by marker)");
        }

        Android.Util.Log.Info("CircleAI.Kws",
            $"capture: {policy.Describe()}, on=[{string.Join(", ", on)}]"
            + (off.Count == 0 ? "" : $" missing=[{string.Join(", ", off)}]"));

        // SAID OUT LOUD, because it is the difference between a phone you can
        // call from the doorway and one you have to hold. Speech falls off with
        // the square of distance; without gain compensation a voice at four
        // metres arrives at a sixteenth of its power at one.
        // WHAT AGC IS ACTUALLY WORTH ON THIS PHONE: NOTHING THAT CAN BE MEASURED,
        // AND THE FIRST ANSWER TO THIS QUESTION WAS AN ARTEFACT.
        //
        // A first pass on 2026-10-28 ran the four CapturePolicy combinations in
        // ORDER - DEFAULT, NO-AEC, NO-FX, RAW-MIC - and read 0,00650 / 0,00647 /
        // 0,00067 / 0,00040 mean. That looks like a clean ten-fold step at the
        // effects boundary, and it was written up as one. It was not: the four ran
        // minutes apart in a room nobody was controlling, so "the effects came off"
        // and "the room went quiet" produce the same number.
        //
        // RE-RUN INTERLEAVED, DEFAULT against NO-FX, six runs, same stretch of
        // minutes:
        //
        //     DEFAULT  0,00093   0,00110   0,00103      (AGC confirmed enabled)
        //     NO-FX    0,00127   0,00140   0,00087
        //
        // 0,00102 against 0,00118 - a ratio of 0,86, with the spreads overlapping
        // completely. AGC moves the noise floor of this phone not at all, and two
        // of those runs caught ambient peaks of 0,2590 and 0,1890, which is the
        // drift that produced the first answer.
        //
        // The echo canceller is likewise worth 0,00003, so the paragraph above
        // calling it the standing suspect for the 30 dB has no support either.
        //
        // WHAT THIS DOES NOT SAY. None of it is a speech measurement, and the 30 dB
        // this capture loses was measured on speech. A floor that is indifferent to
        // AGC says nothing about whether AGC helps a voice four metres away, which
        // is what it is attached for. The warning below stays for that reason; it
        // just no longer quotes a number this phone would not reproduce.
        if (!on.Contains("AGC"))
            Android.Util.Log.Warn("CircleAI.Kws",
                "NO AUTOMATIC GAIN CONTROL ON THIS CAPTURE. Expect the wake word to "
                + "need close range, and read low peak= values as distance, not "
                + "silence. (On a P30 its effect on the NOISE FLOOR measured as "
                + "nothing; its effect on distant speech is untested.) "
                + (off.Exists(o => o.StartsWith("AGC", StringComparison.Ordinal))
                    ? "It was attached and did NOT take; see the missing= reason above."
                    : "This phone does not offer one."));
    }

    /// <summary>
    /// Re-asserts every attached effect once the session is actually recording,
    /// and reports what is true afterwards.
    /// </summary>
    /// <remarks>
    /// BECAUSE THE FIRST ENABLE HAPPENS TOO EARLY TO BE RELIED ON. Effects are
    /// attached to the session id before <c>StartRecording</c>, and on a good many
    /// devices a record-session effect will not enable until the session is live -
    /// <c>setEnabled</c> answers ERROR_INVALID_OPERATION and the old code could not
    /// have noticed, because it discarded that status.
    /// <para>
    /// THE RACE THIS GUARDS HAS NOT BEEN SEEN ON THIS PHONE. It was added on a
    /// hypothesis - that a run logging "on=[AGC, echo canceller]" while delivering
    /// the no-effects level had lost the enable - and then three runs with the
    /// read-back in place reported AGC enabled every time, with nothing rescued
    /// after StartRecording. The level difference that prompted it turned out to be
    /// ambient noise, not AGC: interleaved, DEFAULT 0,00102 against NO-FX 0,00118,
    /// a ratio of 0,86 with overlapping spreads.
    /// </para>
    /// <para>
    /// It is kept anyway, because enabling an already-enabled effect is a no-op, so
    /// it is free when the first attempt worked, and because the ORDER is genuinely
    /// wrong without it: attaching needs the session id, enabling wants the session
    /// live, and devices differ. It earns its place as a correct re-assert and an
    /// honest second report, not as the fix for a fault anybody has reproduced.
    /// Nothing is thrown: a capture must never fail to open because an optional
    /// effect would not start.
    /// </para>
    /// </remarks>
    private void ConfirmFarFieldEffects()
    {
        if (_effects.Count == 0) return;

        // The same names the attach line uses, so the two lines can be read
        // together rather than matched up by type.
        static string NameOf(Android.Media.Audiofx.AudioEffect fx) => fx switch
        {
            Android.Media.Audiofx.AutomaticGainControl => "AGC",
            Android.Media.Audiofx.NoiseSuppressor      => "noise suppressor",
            Android.Media.Audiofx.AcousticEchoCanceler => "echo canceller",
            _                                          => fx.GetType().Name,
        };

        var on = new List<string>();
        var off = new List<string>();
        var rescued = new List<string>();

        foreach (var fx in _effects)
        {
            var name = NameOf(fx);
            try
            {
                if (!fx.Enabled)
                {
                    fx.SetEnabled(true);
                    if (fx.Enabled) rescued.Add(name);
                }
                (fx.Enabled ? on : off).Add(name);
            }
            catch (Exception ex)
            {
                off.Add($"{name} ({ex.Message})");
            }
        }

        Android.Util.Log.Info("CircleAI.Kws",
            $"capture effects once recording: on=[{string.Join(", ", on)}]"
            + (off.Count == 0 ? "" : $" OFF=[{string.Join(", ", off)}]")
            + (rescued.Count == 0 ? "" : $" (took only after StartRecording: {string.Join(", ", rescued)})"));

        // THE ONE THAT MATTERS, SAID AGAIN AT THE ONLY MOMENT IT IS CERTAIN.
        // No multiplier quoted: on a P30 the measured effect of AGC on the noise
        // floor was nothing, and its effect on distant speech has not been measured.
        if (!on.Contains("AGC"))
            Android.Util.Log.Warn("CircleAI.Kws",
                "AGC IS NOT RUNNING ON THIS CAPTURE - expect the wake word to need "
                + "close range, and read low peak= values as distance, not silence.");
    }

    public ValueTask DisposeAsync()
    {
        foreach (var fx in _effects)
        {
            try { fx.SetEnabled(false); } catch { }
            try { fx.Release(); fx.Dispose(); } catch { }
        }
        _effects.Clear();

        try { _record?.Stop(); } catch { }
        _record?.Release();
        _record?.Dispose();
        _record = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Speaker playback via AudioTrack, honouring the engine's sample rate.</summary>
public sealed class AndroidAudioPlayer : IAudioPlayer
{
    public async Task PlayAsync(
        ReadOnlyMemory<byte> pcm, int sampleRate, int channels, int bitsPerSample,
        CancellationToken ct = default)
    {
        if (pcm.Length == 0) return;

        var channelOut = channels >= 2 ? ChannelOut.Stereo : ChannelOut.Mono;
        var min = AudioTrack.GetMinBufferSize(sampleRate, channelOut, Encoding.Pcm16bit);
        if (min <= 0) min = pcm.Length;

        using var track = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Assistant)!
                .SetContentType(AudioContentType.Speech)!
                .Build()!)!
            .SetAudioFormat(new Android.Media.AudioFormat.Builder()
                .SetEncoding(Encoding.Pcm16bit)!
                .SetSampleRate(sampleRate)!
                .SetChannelMask(channelOut)!
                .Build()!)!
            .SetBufferSizeInBytes(Math.Max(min, pcm.Length))!
            .SetTransferMode(AudioTrackMode.Static)!
            .Build();

        // AND SAY SO WHILE IT PLAYS, because the comment below was right about
        // the danger and only covered one of the two microphones.
        //
        // The wait below stops the TURN's capture restarting mid-sentence. The
        // WAKE loop is a second microphone, always open in the service, and it
        // never knew - so it recorded the assistant and transcribed it. Pulled
        // off the P30 on 2026-10-12, the wake loop's own capture reads "I AM
        // USING THE SMALLER BRAIN THE BIG ONE STOPPED THIS PHONE SO I LEFT IT
        // ALONE", which is this phone, talking to itself.
        using var mouth = OutLoud.While();

        var bytes = pcm.ToArray();
        track.Write(bytes, 0, bytes.Length);
        track.Play();

        // Static mode plays the whole buffer; wait it out so the loop does not
        // start listening again while the assistant is still talking (which is
        // how a device hears its own voice and answers itself).
        var ms = (int)(pcm.Length / (double)(sampleRate * channels * bitsPerSample / 8) * 1000);
        try { await Task.Delay(ms + 150, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        try { track.Stop(); } catch { }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
#endif
