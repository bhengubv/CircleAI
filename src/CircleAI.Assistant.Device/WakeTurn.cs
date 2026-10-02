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

            took = true;
            await CircleNeuronService.StopListeningAsync(ct).ConfigureAwait(false);

            // The tail of the wake phrase is still in the air. Without this it lands
            // at the front of the question and the recogniser reads "Hey B what is".
            try { await Task.Delay(TimeSpan.FromMilliseconds(700), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            var question = await HearAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(question))
            {
                Log.Info(Tag, "nothing was said");
                return;
            }

            Log.Info(Tag, "heard: " + question);
            try { Earcon.Heard(); } catch { /* as above */ }

            var answer = await AskAsync(question!, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(answer)) return;

            Log.Info(Tag, "answer: " + answer!.Length + " chars");
            await SayAsync(answer!, ct).ConfigureAwait(false);
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

        try
        {
            await using var mic = new AndroidAudioCapture();
            var vad = new EnergyVadDetector();

            await foreach (var segment in vad
                               .DetectAsync(mic.CaptureAsync(deadline.Token), deadline.Token)
                               .ConfigureAwait(false))
            {
                if (segment.IsSpeech)
                {
                    spoken.AddRange(segment.Audio.ToArray());
                    continue;
                }

                // Silence AFTER something was said is the end of the question.
                // Silence before it is somebody who has not started yet.
                if (spoken.Count > 0) break;
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

    /// <summary>Put the question to the brain this process is already hosting.</summary>
    private static async Task<string?> AskAsync(string question, CancellationToken ct)
    {
        var node = CircleNeuronService.Node;
        if (node is null || !node.IsReady)
        {
            Log.Warn(Tag, "brain is not ready - nothing to ask");
            return null;
        }

        var answer = new System.Text.StringBuilder();
        await foreach (var chunk in node
                           .StreamAsync(new[] { new CircleAI.Hosting.Chat.ChatTurn("user", question) })
                           .ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            answer.Append(chunk);
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
