// WakeOnTheCapturedVoiceTests.cs
//
// THE WHOLE WAKE PATH, RUN OVER AUDIO THE DEVICE ITSELF CAPTURED.
//
// Every other test in this feature exercises one piece. This one runs the three
// that have to agree - the gate, the recogniser and the matcher - in the exact
// order and with the exact block size the Android loop uses, over the exact
// bytes the Android microphone produced. It is the only test that can fail for
// the reason the product fails.
//
// WHY IT EXISTS. Five explanations for "it does not wake" were offered and
// measured wrong in a day: the threshold, the shadowed keyword, the trailing
// silence, the int8 encoder, the phrase spelling. Every one of them was
// plausible, and every one was argued from a piece in isolation. The pieces
// were individually fine; what was wrong was what happened when they were put
// in a line - a gate that cut a stream the recogniser assumed was continuous,
// and never ended the utterance it was building.
//
// THE AUDIO. wake-capture.wav, 60 seconds, pulled off the P30 on 2026-10-12
// from the service's own files directory, written by CaptureDump from inside
// the wake loop. Measured: peak 0,406, loudest second 0,0521 RMS, median second
// 0,00094 - which is the same room level the device reported learning. Six of
// its fifty-nine seconds carry a voice.
//
// IT SKIPS RATHER THAN FAILS when the capture or the bundle is absent, because
// neither is in the repo: the bundle is 80 MB of Apache-2.0 model and the
// capture is a recording of a person's home. Point the two variables at them.

using System;
using System.Collections.Generic;
using System.IO;
using CircleAI.Voice;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public sealed class WakeOnTheCapturedVoiceTests
{
    private readonly ITestOutputHelper _out;

    public WakeOnTheCapturedVoiceTests(ITestOutputHelper output) => _out = output;

    /// <summary>The 20M streaming bundle - Apache-2.0, sherpa-onnx.</summary>
    private static string? Bundle =>
        Environment.GetEnvironmentVariable("CIRCLEAI_ASR_DIR") is { Length: > 0 } d && Directory.Exists(d)
            ? d : null;

    /// <summary>A minute the wake loop captured on the phone, mostly quiet.</summary>
    private static string? Capture =>
        Environment.GetEnvironmentVariable("CIRCLEAI_WAKE_CAPTURE") is { Length: > 0 } f && File.Exists(f)
            ? f : null;

    /// <summary>The owner saying his wake phrases, over and over.</summary>
    /// <remarks>
    /// A DIFFERENT FILE ANSWERING A DIFFERENT QUESTION, and conflating the two
    /// cost a test. The capture is a room that is quiet for 53 of its 60
    /// seconds, so it is what proves the gate GATES; this is 56 seconds of
    /// almost nothing but speech, so a 90 per cent duty cycle on it is correct
    /// and it is what proves the phrase FIRES. Asserting the first file's
    /// numbers against the second reads as a regression and is not one.
    /// </remarks>
    private static string? OwnersVoice =>
        Environment.GetEnvironmentVariable("CIRCLEAI_HEYB_WAV") is { Length: > 0 } f && File.Exists(f)
            ? f : null;

    /// <summary>The device feeds the loop 1600 samples - 100 ms - at a time.</summary>
    private const int Block = 1600;

    private static float[] ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var i = 12;
        while (i + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, i, 4);
            var size = BitConverter.ToInt32(bytes, i + 4);
            if (id == "data")
            {
                var n = Math.Min(size, bytes.Length - (i + 8)) / 2;
                var pcm = new float[n];
                for (var s = 0; s < n; s++)
                    pcm[s] = BitConverter.ToInt16(bytes, i + 8 + s * 2) / 32768f;
                return pcm;
            }
            i += 8 + size + (size & 1);
        }
        throw new InvalidDataException("no data chunk in " + path);
    }

    /// <summary>
    /// Runs the device's loop and reports every phrase it would have woken on.
    /// </summary>
    /// <remarks>
    /// THIS IS A COPY OF ScreenUpWakeWord's INNER LOOP ON PURPOSE, and the
    /// duplication is the point: that loop lives on an Android Service and
    /// cannot be reached from a test, which is precisely where every wrong rule
    /// in this feature was hiding. If the two ever drift, this stops predicting
    /// the device - so it reports its numbers rather than only asserting, and a
    /// human reading them can tell.
    /// </remarks>
    /// <summary>Every utterance the last Run decoded, for reporting.</summary>
    /// <remarks>
    /// THE MATCHER'S INPUT, WRITTEN DOWN. A wake count on its own cannot say
    /// whether a miss was the recogniser or the rule - and this feature has
    /// now burnt a day on exactly that ambiguity, twice over.
    /// </remarks>
    private readonly List<string> _utterances = new();

    private (List<string> woke, string text, double duty, long cut) Run(
        float[] pcm, StreamingTranscriber asr, SpeechGate gate, IReadOnlyList<string> phrases)
    {
        var woke = new List<string>();
        _utterances.Clear();

        asr.Grew += (_, text) =>
        {
            foreach (var phrase in phrases)
            {
                if (!PhraseInSpeech.SaidAnywhere(text, phrase)) continue;
                woke.Add($"{phrase}  <- \"{text}\"");
                asr.Reset();
                return;
            }
        };

        for (var at = 0; at < pcm.Length; at += Block)
        {
            var n = Math.Min(Block, pcm.Length - at);
            if (gate.Wants(pcm.AsSpan(at, n)))
            {
                if (gate.Opened) asr.Reset();
                asr.AcceptWaveform(pcm.AsSpan(at, n));
            }
            // NOT "else if" - see the device loop's note. A cut stretch ends on
            // a block that WAS delivered, so Wants returns true and Closed is
            // true together; an else-if silently skips every long utterance.
            if (gate.Closed)
            {
                // ASK IT TO FINISH, exactly as the device loop now does. A
                // transducer holds its last token until something says the
                // audio has stopped, so without this the final token of every
                // phrase stays uncommitted - and "Hey B" without its "B"
                // matches nothing.
                //
                // NO RESET HERE. The stream is already restarted at the START
                // of the next burst, on gate.Opened, so a reset here would be
                // redundant - and it discards the tokens the flush has just
                // committed before the matcher can see them.
                asr.Flush();

                // AND WRITE DOWN WHAT THE MATCHER WAS GIVEN. A wake count on
                // its own cannot say whether a miss was the recogniser or the
                // rule, which is the ambiguity this feature keeps paying for.
                var ended = asr.Text.Trim();
                if (ended.Length > 0) _utterances.Add(ended);
            }
        }

        return (woke, asr.Text, gate.DutyCycle, gate.Cut);
    }

    [Fact]
    public void The_captured_voice_is_transcribed_rather_than_hallucinated()
    {
        var dir = Bundle;
        var wav = Capture;
        if (dir is null || wav is null)
        {
            _out.WriteLine("skipped: set CIRCLEAI_ASR_DIR and CIRCLEAI_WAKE_CAPTURE");
            return;
        }

        var pcm = ReadWav(wav);
        using var asr = new StreamingTranscriber(dir);
        var gate = new SpeechGate();

        var (woke, text, duty, cut) = Run(pcm, asr, gate, new[] { "Hey B", "Hey Circle AI" });

        _out.WriteLine($"audio   : {pcm.Length / 16000.0:F1} s");
        _out.WriteLine($"gate    : decoded {duty:P0}, room {gate.Floor:F5}, opens at {gate.Opening:F4}, {cut} cut");
        _out.WriteLine($"int8    : {asr.EncoderIsQuantized}");
        _out.WriteLine($"tail    : \"{text}\"");
        foreach (var w in woke) _out.WriteLine($"WOKE    : {w}");

        // THE GATE MUST ACTUALLY GATE. On the P30 before this was adaptive it
        // passed 77 to 87 per cent of blocks in this very room and bought
        // nothing. Six of these sixty seconds carry a voice.
        Assert.True(duty < 0.40, $"the gate passed {duty:P0} of a room that is quiet for 53 of 60 seconds");

        // AND IT MUST NOT INVENT. With no endpoint the recogniser behind this
        // gate wrote, growing for ever and over a quiet room: "YOU SEE THERE
        // YOU ARE TO YOU WHOM MOVED YOU THE DAYS FOLLOWED UNDER A SPARECT".
        // The tail is whatever the LAST utterance decoded to, so on sixty
        // seconds of mostly-silence it cannot be a paragraph.
        Assert.True(text.Length < 120,
            $"the transcript should be one utterance, not an hour of invention: \"{text}\"");
    }

    [Fact]
    public void THE_OWNER_SAYING_HIS_PHRASE_WAKES_IT()
    {
        // THE PRODUCT REQUIREMENT, AS A TEST, OVER HIS ACTUAL VOICE.
        //
        // 56 seconds recorded on 2026-10-11 of the owner saying his wake
        // phrases at ordinary holding distance. Everything else in this feature
        // can be green while this is the only question: does it answer when he
        // says its name.
        //
        // WHAT IT PRODUCED THE FIRST TIME IT PASSED, 2026-10-12:
        //
        //     Hey B          <-  " HAY B"
        //     Hey B          <-  " B HAY B"
        //     Hey B          <-  " B HAY B"
        //     Hey Circle AI  <-  " B HASTE CIRCLE HEY CIRCLE AI"
        //
        // Note that every one of them came through the FORGIVING matcher - the
        // recogniser wrote "hay" for "hey" each time, exactly as it had on the
        // phone. An exact match scores zero here, which is what it scored on
        // the device for a day.
        var dir = Bundle;
        var wav = OwnersVoice;
        if (dir is null || wav is null)
        {
            _out.WriteLine("skipped: set CIRCLEAI_ASR_DIR and CIRCLEAI_HEYB_WAV");
            return;
        }

        var pcm = ReadWav(wav);
        using var asr = new StreamingTranscriber(dir);
        var gate = new SpeechGate();

        var (woke, _, duty, cut) = Run(pcm, asr, gate, new[] { "Hey B", "Hey Circle AI" });

        _out.WriteLine($"gate    : decoded {duty:P0}, room {gate.Floor:F5}, opens at {gate.Opening:F4}, {cut} cut");
        foreach (var u in _utterances) _out.WriteLine($"said    : {u}");
        foreach (var w in woke) _out.WriteLine($"WOKE    : {w}");

        Assert.NotEmpty(woke);

        // AND THE BAR NEVER CLIMBS PAST THE LOUD END, which is what makes that
        // possible here: this file is so nearly continuous that the gate learns
        // the "room" to be the owner's own voice, and a bar that followed it up
        // would leave the phone deaf to the person holding it.
        Assert.True(gate.Opening <= gate.Busiest,
            $"the bar ({gate.Opening:F5}) must stay at or below the loud end ({gate.Busiest:F5})");
    }

    [Fact]
    public void What_it_actually_hears_in_each_burst_is_written_down()
    {
        // NOT AN ASSERTION, A MEASUREMENT - and it is in the suite rather than
        // in a scratch script because this is the number every decision about
        // the wake phrase rests on, and the last time it lived in a script the
        // script was wrong and its ranking was reported as a finding.
        var dir = Bundle;
        var wav = Capture;
        if (dir is null || wav is null)
        {
            _out.WriteLine("skipped: set CIRCLEAI_ASR_DIR and CIRCLEAI_WAKE_CAPTURE");
            return;
        }

        var pcm = ReadWav(wav);
        using var asr = new StreamingTranscriber(dir);
        var gate = new SpeechGate();

        var said = new List<string>();
        for (var at = 0; at < pcm.Length; at += Block)
        {
            var n = Math.Min(Block, pcm.Length - at);
            var want = gate.Wants(pcm.AsSpan(at, n));

            // Record the utterance as the gate closes, which is where one ends.
            if (!want && asr.Text.Trim().Length > 0)
            {
                asr.Flush();
                var t = asr.Text.Trim();
                if (t.Length > 0) said.Add($"{at / 16000.0,5:F1}s  {t}");
                asr.Reset();
            }

            if (!want) continue;
            if (gate.Opened) asr.Reset();
            asr.AcceptWaveform(pcm.AsSpan(at, n));
        }

        _out.WriteLine($"{said.Count} utterances in {pcm.Length / 16000.0:F0} s, gate passed {gate.DutyCycle:P0}:");
        foreach (var s in said) _out.WriteLine("  " + s);

        // A room that is quiet for 53 of 60 seconds must not produce an
        // utterance every second. That would mean the gate is not gating.
        Assert.True(said.Count < 30, $"{said.Count} utterances in a minute is a gate that is not working");
    }
}
