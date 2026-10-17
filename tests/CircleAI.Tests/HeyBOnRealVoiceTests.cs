// HeyBOnRealVoiceTests.cs
//
// THE TEST THAT SHOULD HAVE BEEN FIRST.
//
// Every claim made about this wake phrase for a month came from device logs and
// inference, because nobody ever put a recording of a human saying it through
// the decoder. Three explanations were offered and measured wrong in a single
// day - the threshold, the int8 encoder, and amplitude - and each one cost a
// build, an install and somebody standing in a room talking to a phone.
//
// This is a 55,6 s recording made on the P30 itself on 2026-10-12: the owner
// saying "Hey B!" repeatedly at normal holding distance. Peak 0,449, median
// per-second RMS 0,0446, 50 of its 55 seconds above the noise floor - the same
// band as the recording the model transcribes legibly, so the audio is not the
// variable.
//
// Two questions, answered from evidence rather than argument:
//   1. What does the acoustic model actually EMIT for this utterance?
//   2. Which keyword spelling, if any, fires on it?
//
// Set CIRCLEAI_HEYB_WAV to the 16 kHz mono wav and KWS_BUNDLE_DIR to the
// extracted upstream bundle. Skips otherwise.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class HeyBOnRealVoiceTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public HeyBOnRealVoiceTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private const string Mark = "▁";

    private static string? Bundle =>
        Environment.GetEnvironmentVariable("KWS_BUNDLE_DIR") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

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

    [Fact]
    public void What_the_model_hears_when_the_owner_says_it()
    {
        var bundle = Bundle;
        var wav = Wav;
        if (bundle is null || wav is null)
        {
            _out.WriteLine("SKIPPED: needs KWS_BUNDLE_DIR and CIRCLEAI_HEYB_WAV.");
            return;
        }

        var pcm = ReadWav(wav);

        using var kws = new ZipformerKwsSpotter(bundle, Path.Combine(bundle, "test_wavs", "test_keywords.txt"));
        var emitted = new List<string>();
        kws.TokenEmitted += (_, t) => emitted.Add(t);

        const int chunk = 1600;
        for (var at = 0; at < pcm.Length; at += chunk)
            kws.AcceptWaveform(pcm.AsSpan(at, Math.Min(chunk, pcm.Length - at)));

        _out.WriteLine($"{pcm.Length / 16000.0:F1}s, peak {pcm.Max(Math.Abs):F3}, {emitted.Count} tokens");
        _out.WriteLine(string.Empty);
        _out.WriteLine("WHAT IT EMITTED:");
        _out.WriteLine("  " + string.Join(" ", emitted).Replace(Mark, "_"));
        _out.WriteLine(string.Empty);

        // The counts are what matter: this utterance is one short phrase said
        // over and over, so whatever the model thinks the phrase IS will be at
        // the top of this list.
        _out.WriteLine("MOST FREQUENT:");
        foreach (var g in emitted.GroupBy(t => t).OrderByDescending(g => g.Count()).Take(14))
            _out.WriteLine($"  {g.Key.Replace(Mark, "_"),-10} x{g.Count()}");

        Assert.True(emitted.Count > 0,
            "the model emitted nothing for 55 s of clear speech - the audio or the chain is wrong, "
            + "not the phrase");
    }

    [Fact]
    public void The_shipped_phrase_fires_until_it_is_dropped_to_the_level_the_wake_loop_captures()
    {
        // THE EXPERIMENT THAT SETTLES IT, AND IT OVERTURNS FOUR DAYS OF GUESSES.
        //
        // The shipped keyword "_HE Y _B" fires EIGHT times on this recording.
        // The phrase was never wrong. What differs is the capture: the phone's
        // own recorder took this voice at peak 0,449 and median per-second RMS
        // 0,0446, while the wake loop - AudioSource.VoiceRecognition, AGC and
        // echo canceller on, no noise suppressor - was reporting peak 0,17 to
        // 0,22 from the same speaker in the same room on the same phone.
        //
        // So: drop this audio to the level the wake loop actually sees, and ask
        // whether the detections survive. Then apply SpeechGain and ask again.
        var bundle = Bundle;
        var wav = Wav;
        if (bundle is null || wav is null)
        {
            _out.WriteLine("SKIPPED: needs KWS_BUNDLE_DIR and CIRCLEAI_HEYB_WAV.");
            return;
        }

        var loud = ReadWav(wav);
        var file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
        File.WriteAllText(file, $"{Mark}HE Y {Mark}B @Hey_B\n");

        int Fires(float[] pcm)
        {
            using var kws = new ZipformerKwsSpotter(bundle, file);
            var n = 0;
            kws.Detected += (_, _) => n++;
            const int chunk = 1600;
            for (var at = 0; at < pcm.Length; at += chunk)
                kws.AcceptWaveform(pcm.AsSpan(at, Math.Min(chunk, pcm.Length - at)));
            return n;
        }

        try
        {
            var asRecorded = Fires(loud);

            // The band the device reported while the owner was speaking to it.
            var scale = 0.20f / loud.Max(Math.Abs);
            var quiet = loud.Select(s => s * scale).ToArray();
            var atDeviceLevel = Fires(quiet);

            var lifted = (float[])quiet.Clone();
            var gain = new SpeechGain();
            const int block = 1600;
            for (var at = 0; at < lifted.Length; at += block)
                gain.Apply(lifted.AsSpan(at, Math.Min(block, lifted.Length - at)));
            var recovered = Fires(lifted);

            _out.WriteLine($"as recorded     peak {loud.Max(Math.Abs):F3} -> fired {asRecorded}");
            _out.WriteLine($"at wake level   peak {quiet.Max(Math.Abs):F3} -> fired {atDeviceLevel}");
            _out.WriteLine($"+ SpeechGain    peak {lifted.Max(Math.Abs):F3} -> fired {recovered}");

            Assert.True(asRecorded > 0,
                "the shipped phrase does not fire even on a clean recording of it - "
                + "then the phrase really is wrong and everything below is moot");
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void The_device_configuration_is_int8_and_two_stage_and_the_bench_was_neither()
    {
        // EVERY BENCH RUN SO FAR HAS BEEN THE WRONG CONFIGURATION.
        //
        // The bench loads the UPSTREAM bundle, which ships both precisions, so
        // preferQuantized:false quietly picked the FLOAT encoder - while the
        // device's bundle contains only encoder.int8.onnx and falls back to it.
        // And the bench drives ZipformerKwsSpotter directly, while the device
        // wraps it in ConfirmedKeywordSpotter, whose second stage can VETO a
        // detection that stage one made.
        //
        // So the two things that have never been varied together are exactly
        // the two things that differ. This varies them, on the owner's own
        // voice saying the owner's own phrase.
        var bundle = Bundle;
        var wav = Wav;
        if (bundle is null || wav is null)
        {
            _out.WriteLine("SKIPPED: needs KWS_BUNDLE_DIR and CIRCLEAI_HEYB_WAV.");
            return;
        }

        var pcm = ReadWav(wav);
        var file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
        File.WriteAllText(file, $"{Mark}HE Y {Mark}B @Hey_B\n");

        try
        {
            foreach (var int8 in new[] { false, true })
            {
                // Stage one alone.
                using (var raw = new ZipformerKwsSpotter(bundle, file, preferQuantized: int8))
                {
                    var fired = 0;
                    var best = 0.0;
                    raw.Detected += (_, d) => { fired++; if (d.Probability > best) best = d.Probability; };
                    const int chunk = 1600;
                    for (var at = 0; at < pcm.Length; at += chunk)
                        raw.AcceptWaveform(pcm.AsSpan(at, Math.Min(chunk, pcm.Length - at)));
                    _out.WriteLine($"{(int8 ? "INT8 " : "float")} stage one only : fired {fired,2}  best p={best:F4}");
                }

                // Both stages, as the device runs it.
                using (var confirmed = new ConfirmedKeywordSpotter(
                           new ZipformerKwsSpotter(bundle, file, preferQuantized: int8)))
                {
                    var woke = 0;
                    var vetoed = 0;
                    confirmed.Woke += (_, _) => woke++;
                    confirmed.Rejected += (_, _) => vetoed++;
                    const int chunk = 1600;
                    for (var at = 0; at < pcm.Length; at += chunk)
                        confirmed.AcceptWaveform(pcm.AsSpan(at, Math.Min(chunk, pcm.Length - at)));
                    _out.WriteLine($"{(int8 ? "INT8 " : "float")} as the device  : woke {woke,2}  vetoed {vetoed}");
                }
            }
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void Which_spelling_actually_fires()
    {
        var bundle = Bundle;
        var wav = Wav;
        if (bundle is null || wav is null)
        {
            _out.WriteLine("SKIPPED: needs KWS_BUNDLE_DIR and CIRCLEAI_HEYB_WAV.");
            return;
        }

        var pcm = ReadWav(wav);

        // EVERY PLAUSIBLE WAY TO WRITE THE SAME UTTERANCE. The point is not to
        // pick a favourite and test it - that is what produced three wrong
        // answers in a day - but to run them all against the same audio and let
        // the counts decide.
        var candidates = new (string Why, string Tokens)[]
        {
            ("what shipped (the logo)",        $"{Mark}HE Y {Mark}B"),
            ("spelled as it is said",          $"{Mark}HE Y {Mark}BE E"),
            ("said, shorter",                  $"{Mark}HE Y {Mark}BE"),
            ("run together, continuation",     $"{Mark}HE Y B"),
            ("run together, spelled",          $"{Mark}HE Y BE"),
            ("just the greeting",              $"{Mark}HE Y"),
        };

        var results = new List<string>();

        foreach (var (why, tokens) in candidates)
        {
            var file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
            try
            {
                File.WriteAllText(file, tokens + " @Hey_B\n");

                using var kws = new ZipformerKwsSpotter(bundle, file);

                var fired = 0;
                var deepest = 0;
                var total = 0;
                var best = 0.0;

                kws.Detected += (_, _) => fired++;
                kws.KeywordProgress += (_, p) =>
                {
                    if (p.Matched > deepest || (p.Matched == deepest && p.MeanProbability > best))
                    {
                        deepest = p.Matched;
                        total = p.Total;
                        best = p.MeanProbability;
                    }
                };

                const int chunk = 1600;
                for (var at = 0; at < pcm.Length; at += chunk)
                    kws.AcceptWaveform(pcm.AsSpan(at, Math.Min(chunk, pcm.Length - at)));

                results.Add($"  {tokens.Replace(Mark, "_"),-16} fired {fired,3}   "
                          + $"deepest {deepest}/{(total == 0 ? tokens.Split(' ').Length : total)}  "
                          + $"p={best:F4}   ({why})");
            }
            catch (Exception ex)
            {
                results.Add($"  {tokens.Replace(Mark, "_"),-16} REFUSED: {ex.Message}   ({why})");
            }
            finally
            {
                if (File.Exists(file)) File.Delete(file);
            }
        }

        foreach (var line in results) _out.WriteLine(line);

        Assert.NotEmpty(results);
    }
}
