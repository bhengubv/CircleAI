// HowQuietBeforeItGoesDeafTests.cs
//
// THE LEVEL AT WHICH THE RECOGNISER STOPS PRODUCING WORDS AT ALL.
//
// WHY THIS IS THE ONLY QUESTION LEFT. On the P30 at 07:32 on 2026-10-12 the
// live microphone produced, through the real wake loop, a complete sentence:
//
//     heard #1 (4.4 s): "YES LET ME KNOW IF THERE IS ANYTHING ELSE I CAN
//                        HELP WITH"
//
// Thirteen words, no errors, at a reported loudest block of 0,0964. So the
// capture path, the feature extractor, the encoder, the decoder, the gate's
// endpoint and the utterance log all work, together, on a phone. That disposes
// of a long list of theories at once.
//
// In the same eleven minutes, every window whose loudest block was 0,0150 or
// below produced ZERO utterances - not wrong words, no words. The gate opened
// for up to 58 per cent of blocks and the transcript came back empty every
// time.
//
// So there is a cliff between 0,015 and 0,096, and where it sits decides the
// product: it is the difference between a phone you speak to from across the
// room and one you have to hold to your mouth. Nothing about it can be settled
// by argument, and it does not need the owner to say anything - the recording
// of his voice already exists and can be scaled.
//
// SCALED BY RMS, NOT BY PEAK. An earlier bench experiment scaled by peak and
// concluded that level was not the problem. Peak is set by the single loudest
// sample in a minute - one door slam, one plosive - and says almost nothing
// about the level the feature extractor integrates over a frame. That
// experiment never reproduced what the device delivers, and it is why "level"
// sat on the ruled-out list for a day while being the answer.

using System;
using System.Collections.Generic;
using System.IO;
using CircleAI.Voice;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public sealed class HowQuietBeforeItGoesDeafTests
{
    private readonly ITestOutputHelper _out;

    public HowQuietBeforeItGoesDeafTests(ITestOutputHelper output) => _out = output;

    private static string? Bundle =>
        Environment.GetEnvironmentVariable("CIRCLEAI_ASR_DIR") is { Length: > 0 } d && Directory.Exists(d)
            ? d : null;

    private static string? OwnersVoice =>
        Environment.GetEnvironmentVariable("CIRCLEAI_HEYB_WAV") is { Length: > 0 } f && File.Exists(f)
            ? f : null;

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
                var avail = bytes.Length - (i + 8);
                var n = (size <= 0 || size > avail ? avail : size) / 2;
                var pcm = new float[n];
                for (var s = 0; s < n; s++)
                    pcm[s] = BitConverter.ToInt16(bytes, i + 8 + s * 2) / 32768f;
                return pcm;
            }
            i += 8 + size + (size & 1);
        }
        throw new InvalidDataException("no data chunk in " + path);
    }

    /// <summary>The level the device's own log reports: loudest 100 ms block.</summary>
    private static double LoudestBlock(float[] pcm)
    {
        var top = 0.0;
        for (var at = 0; at + Block <= pcm.Length; at += Block)
        {
            double sum = 0;
            for (var i = at; i < at + Block; i++) sum += (double)pcm[i] * pcm[i];
            var rms = Math.Sqrt(sum / Block);
            if (rms > top) top = rms;
        }
        return top;
    }

    /// <summary>Runs the device's chain and returns what it decoded.</summary>
    private static (int words, int wakes, string sample) Run(
        float[] pcm, string dir, IReadOnlyList<string>? phrases = null)
    {
        using var asr = new StreamingTranscriber(dir);
        var gate = new SpeechGate();
        var words = 0;
        var wakes = 0;
        var sample = string.Empty;
        var want = phrases ?? new[] { "Hey B", "Hey Circle AI" };

        asr.Grew += (_, text) =>
        {
            foreach (var phrase in want)
                if (PhraseInSpeech.SaidAnywhere(text, phrase)) { wakes++; asr.Reset(); return; }
        };

        for (var at = 0; at < pcm.Length; at += Block)
        {
            var n = Math.Min(Block, pcm.Length - at);
            if (gate.Wants(pcm.AsSpan(at, n)))
            {
                if (gate.Opened) asr.Reset();
                asr.AcceptWaveform(pcm.AsSpan(at, n));
            }

            // Not else-if: a cut stretch ends on a block that WAS delivered.
            if (gate.Closed)
            {
                asr.Flush();
                var heard = asr.Text.Trim();
                if (heard.Length > 0)
                {
                    words += heard.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                    if (sample.Length == 0) sample = heard;
                }
            }
        }

        return (words, wakes, sample);
    }

    [Fact]
    public void WHERE_THE_RECOGNISER_GOES_DEAF_IS_A_NUMBER_AND_THIS_IS_IT()
    {
        var dir = Bundle;
        var wav = OwnersVoice;
        if (dir is null || wav is null)
        {
            _out.WriteLine("skipped: set CIRCLEAI_ASR_DIR and CIRCLEAI_HEYB_WAV");
            return;
        }

        var loud = ReadWav(wav);

        _out.WriteLine($"{"scale",6} {"loudest block",14} {"words",6} {"wakes",6}  first utterance");
        _out.WriteLine(new string('-', 78));

        var results = new List<(double scale, double loudest, int words, int wakes)>();

        // Halving each time, from the recording as made down to a sixty-fourth -
        // which brackets both the 0,0964 that worked on the phone and the
        // 0,0150 that produced nothing.
        foreach (var scale in new[] { 1.0, 0.5, 0.25, 0.125, 0.0625, 0.03125, 0.015625 })
        {
            var quiet = new float[loud.Length];
            for (var i = 0; i < loud.Length; i++) quiet[i] = (float)(loud[i] * scale);

            var level = LoudestBlock(quiet);
            var (words, wakes, sample) = Run(quiet, dir);
            results.Add((scale, level, words, wakes));

            _out.WriteLine($"{scale,6:F4} {level,14:F4} {words,6} {wakes,6}  "
                         + (sample.Length > 40 ? sample[..40] : sample));
        }

        // THE RECORDING AS MADE MUST STILL WORK, or the harness is broken and
        // nothing below it means anything.
        Assert.True(results[0].words > 0, "the unscaled recording must transcribe");

        // AND IT MUST GO DEAF SOMEWHERE, or level is not the mechanism and this
        // whole line of enquiry is wrong - which is worth failing loudly for
        // rather than quietly concluding.
        var deaf = results.FindIndex(r => r.words == 0);
        Assert.True(deaf > 0,
            "scaling the owner's voice down 64x produced words at every level - "
            + "if that is true then level is NOT why the phone is silent, and the "
            + "0 utterances measured on the device need another explanation");

        var lastHeard = results[deaf - 1];
        var firstDeaf = results[deaf];

        _out.WriteLine(string.Empty);
        _out.WriteLine($"last level that produced words : loudest block {lastHeard.loudest:F4}");
        _out.WriteLine($"first level that produced none : loudest block {firstDeaf.loudest:F4}");
        _out.WriteLine($"=> the chain needs a loudest block of roughly {lastHeard.loudest:F4} to read a voice.");
        _out.WriteLine(string.Empty);
        _out.WriteLine("Measured on the P30 the same day: windows at 0,0150 and below produced");
        _out.WriteLine("0 utterances; the one window that reached 0,0964 transcribed thirteen");
        _out.WriteLine("words without an error.");
    }

    [Fact]
    public void WHICH_WAKE_PHRASE_ACTUALLY_SURVIVES_THIS_MODEL_AND_THIS_VOICE()
    {
        // THE LEVEL TABLE TURNED UP SOMETHING LEVEL DOES NOT EXPLAIN. At half
        // scale the recogniser transcribed THIRTY-NINE words of the owner's
        // voice and woke ZERO times, writing:
        //
        //     HAY CIRCLED AYE HAY CIRCLE AY CIRCLE AI
        //     HAY CIRCLE A HEY CIRCLE E I
        //
        // "Circle" is there, correct, every time. What moves is the tail: "AI"
        // comes back as "AY", "A", "E I", "AYE" - because a two-letter spoken
        // sequence has no context to constrain it, which is the same wall the
        // single letter "B" hits and the same wall the keyword spotter hit from
        // the other side.
        //
        // So the phrase itself is a product decision with a measurable answer,
        // and this is the measurement: the owner's own voice, at five levels,
        // against each candidate. It asserts almost nothing on purpose - the
        // brand is not the test suite's to choose - but the numbers are here so
        // the choice is made on evidence rather than on taste alone.
        var dir = Bundle;
        var wav = OwnersVoice;
        if (dir is null || wav is null)
        {
            _out.WriteLine("skipped: set CIRCLEAI_ASR_DIR and CIRCLEAI_HEYB_WAV");
            return;
        }

        var loud = ReadWav(wav);
        var scales = new[] { 1.0, 0.5, 0.25, 0.125 };
        var candidates = new[] { "Hey B", "Hey Circle AI", "Hey Circle", "Circle" };

        _out.WriteLine($"{"phrase",-16}" + string.Concat(Array.ConvertAll(scales, s => $"{s,8:F3}")) + "   total");
        _out.WriteLine(new string('-', 60));

        var best = string.Empty;
        var bestTotal = -1;

        foreach (var phrase in candidates)
        {
            var line = $"{phrase,-16}";
            var total = 0;

            foreach (var scale in scales)
            {
                var quiet = new float[loud.Length];
                for (var i = 0; i < loud.Length; i++) quiet[i] = (float)(loud[i] * scale);

                var (_, wakes, _) = Run(quiet, dir, new[] { phrase });
                total += wakes;
                line += $"{wakes,8}";
            }

            _out.WriteLine(line + $"{total,8}");
            if (total > bestTotal) { bestTotal = total; best = phrase; }
        }

        _out.WriteLine(string.Empty);
        _out.WriteLine($"most reliable of the four on this voice and this model: \"{best}\" ({bestTotal} wakes)");
        _out.WriteLine(string.Empty);
        _out.WriteLine("The recording is the owner testing SEVERAL phrases, including Hey Bob,");
        _out.WriteLine("Hey Friend, Hey Alexa, Siri, Hey Google and Jarvis - so a candidate that");
        _out.WriteLine("scores high here is firing on its own phrase, not on everything.");

        // THE ONE THING THIS DOES ASSERT. A phrase that cannot be heard at ANY
        // level, on a recording of somebody saying it repeatedly, is not a wake
        // phrase - and shipping one would be the whole feature failing quietly.
        Assert.True(bestTotal > 0,
            "not one of these phrases woke at any level - if that is ever true, the "
            + "matcher is broken rather than the phrase being a poor choice");
    }
}
