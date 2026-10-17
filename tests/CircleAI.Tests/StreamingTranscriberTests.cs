// StreamingTranscriberTests.cs
//
// PROVING OUR OWN C# TRANSCRIBER AGAINST THE OWNER'S OWN VOICE.
//
// The Python reference already answered the design question: over a 55,6 s
// recording of the owner testing wake phrases on his P30, sherpa-onnx's 20M
// streaming model transcribed legibly at 30x realtime, and a phrase match found
// "hey circle" twice, "hey alexa" once and "hey b" twice - three different
// phrases, one model, no training. That is the capability the whole product
// needs and nothing else has: Africa has ~2,000 languages, so one trained model
// per phrase does not scale.
//
// What is NOT proven by Python is our own C# port. The last hand-rolled decoder
// in this repo disagreed with sherpa's own implementation on the same file -
// 8 detections against 4 - and nobody noticed for months. So this measures the
// C# against the same audio and the same expectation.
//
// Needs the model and the recording; set CIRCLEAI_ASR_DIR and CIRCLEAI_HEYB_WAV.
// Skips otherwise, so CI stays offline.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class StreamingTranscriberTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public StreamingTranscriberTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static string? Model =>
        Environment.GetEnvironmentVariable("CIRCLEAI_ASR_DIR") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static string? Wav =>
        Environment.GetEnvironmentVariable("CIRCLEAI_HEYB_WAV") is { Length: > 0 } f && File.Exists(f) ? f : null;

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

    /// <summary>Streams a file through, waking on a phrase the way the device will.</summary>
    private (int Woke, string Text, double Realtime) Listen(
        string model, float[] pcm, string phrase)
    {
        using var asr = new StreamingTranscriber(model);

        var woke = 0;
        var whole = new System.Text.StringBuilder();

        asr.Grew += (_, text) =>
        {
            if (!PhraseInSpeech.JustSaid(text, phrase)) return;

            woke++;
            whole.Append(text).Append(" | ");
            // THE MOMENT IT FIRES, FORGET. Otherwise the phrase stays in the
            // transcript and every later frame matches it again.
            asr.Reset();
        };

        var clock = Stopwatch.StartNew();
        const int chunk = 1600;                       // 100 ms, as the device feeds it
        for (var at = 0; at < pcm.Length; at += chunk)
            asr.AcceptWaveform(pcm.AsSpan(at, Math.Min(chunk, pcm.Length - at)));
        asr.Flush();
        clock.Stop();

        return (woke, whole.ToString(), pcm.Length / 16000.0 / clock.Elapsed.TotalSeconds);
    }

    [Fact]
    public void It_transcribes_the_owners_voice()
    {
        var model = Model;
        var wav = Wav;
        if (model is null || wav is null)
        {
            _out.WriteLine("SKIPPED: needs CIRCLEAI_ASR_DIR and CIRCLEAI_HEYB_WAV.");
            return;
        }

        using var asr = new StreamingTranscriber(model);
        var pcm = ReadWav(wav);

        var clock = Stopwatch.StartNew();
        const int chunk = 1600;
        for (var at = 0; at < pcm.Length; at += chunk)
            asr.AcceptWaveform(pcm.AsSpan(at, Math.Min(chunk, pcm.Length - at)));
        asr.Flush();
        clock.Stop();

        var text = asr.Text.Trim();
        var secs = pcm.Length / 16000.0;

        _out.WriteLine($"{secs:F1}s in {clock.Elapsed.TotalSeconds:F1}s "
                     + $"({secs / clock.Elapsed.TotalSeconds:F1}x realtime), "
                     + $"encoder {(asr.EncoderIsQuantized ? "int8" : "float")}");
        _out.WriteLine(text.Length > 900 ? text[..900] + " ..." : text);

        // THE FLOOR THIS HAS TO CLEAR. The 3.3M keyword spotter produced ZERO
        // tokens from the app's own capture of this speaker. Anything that
        // cannot produce a transcript here is no better.
        Assert.True(text.Length > 100,
            $"only {text.Length} characters from {secs:F0}s of clear speech: \"{text}\"");
    }

    [Fact]
    public void One_model_wakes_on_three_different_phrases_with_no_training()
    {
        // THE WHOLE REASON FOR THE CHANGE. A fixed-phrase engine needs a trained
        // model per phrase, which cannot serve a continent with ~2,000
        // languages. The Python reference found "hey circle" twice, "hey alexa"
        // once and "hey b" twice in this one recording, from one model.
        var model = Model;
        var wav = Wav;
        if (model is null || wav is null)
        {
            _out.WriteLine("SKIPPED: needs CIRCLEAI_ASR_DIR and CIRCLEAI_HEYB_WAV.");
            return;
        }

        var pcm = ReadWav(wav);
        var found = new Dictionary<string, int>();

        foreach (var phrase in new[] { "hey circle", "hey alexa", "hey b" })
        {
            var (woke, _, rt) = Listen(model, pcm, phrase);
            found[phrase] = woke;
            _out.WriteLine($"  \"{phrase,-12}\" -> {woke} wake(s)   ({rt:F1}x realtime)");
        }

        Assert.True(found.Values.Sum() > 0,
            "the C# transcriber woke on none of the three phrases the Python "
            + "reference found in this same file - the port disagrees with the library");

        Assert.True(found["hey circle"] > 0,
            "\"hey circle\" is the clearest multi-word phrase in the recording and "
            + "the one the reference found twice; missing it means the port is wrong");
    }

    [Fact]
    public void It_does_not_wake_on_speech_that_is_not_the_phrase()
    {
        // A wake word that fires during conversation cannot be worked around,
        // and it is the one way this could be worse than what it replaces.
        var model = Model;
        var wav = Wav;
        if (model is null || wav is null)
        {
            _out.WriteLine("SKIPPED: needs CIRCLEAI_ASR_DIR and CIRCLEAI_HEYB_WAV.");
            return;
        }

        var pcm = ReadWav(wav);
        var (woke, _, _) = Listen(model, pcm, "open the garage door");

        _out.WriteLine($"  \"open the garage door\" -> {woke} wake(s) (want 0)");
        Assert.Equal(0, woke);
    }
}
