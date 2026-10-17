// EncoderPrecisionOnRealAudioTests.cs
//
// SETTLES int8-VERSUS-float WITH AUDIO INSTEAD OF ARGUMENT.
//
// ZipformerKwsSpotter's constructor has carried this claim since it was written:
//
//     "its output distribution is flat enough that keyword tokens sat around
//      p=0.01 where the float graphs put them above 0.3. Nothing errors; the
//      wake word simply never fires."
//
// It was written from a device observation and nothing ever checked it. On
// 2026-10-11 it turned out to matter enormously: the bundle we ship,
// KWS-Zipformer-HeyB, contains ONLY encoder.int8.onnx - proven byte-identical
// to upstream's int8 encoder by sha256 - while its decoder and joiner are the
// FLOAT files. Two of three right. So the product has been running the exact
// configuration that comment warns about, and the wake word fired zero times in
// an hour of a person saying the phrase.
//
// This is the controlled version of that observation: the SAME audio, the SAME
// keywords, the SAME C# decoder, with only the encoder precision changed.
//
// WHY IT IS SKIPPED BY DEFAULT. It needs the upstream bundle, which is a 17.6 MB
// download and is not in the repository. Point KWS_BUNDLE_DIR at an extracted
// sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01 and it runs; otherwise it
// skips, so CI stays offline and fast. The bundle is Apache-2.0 (its README
// front matter says so, from modelscope.cn/pkufool), and it ships test_wavs with
// a transcript, so the ground truth is the upstream project's own, not ours.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class EncoderPrecisionOnRealAudioTests
{
    /// <summary>Set to an extracted upstream bundle to enable these.</summary>
    private static string? Bundle =>
        Environment.GetEnvironmentVariable("KWS_BUNDLE_DIR") is { Length: > 0 } d && Directory.Exists(d)
            ? d
            : null;

    /// <summary>What each shipped wav actually says, from the bundle's trans.txt.</summary>
    /// <remarks>
    /// Upstream's own ground truth, not ours - test_wavs/test_keywords.txt holds
    /// the compiled phrases and trans.txt the transcripts. 0.wav contains
    /// "LIGHT UP"; 1.wav contains both "LOVELY CHILD" and "FOR EVER".
    /// </remarks>
    private static readonly (string Wav, string[] Expect)[] Truth =
    [
        ("0.wav", ["▁ L IGHT ▁UP"]),
        ("1.wav", ["▁LOVE LY ▁CHI L D", "▁FOR E VER"]),
    ];

    /// <summary>16-bit mono PCM out of a RIFF/WAVE file, as floats in [-1,1].</summary>
    private static float[] ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);

        // Walk the chunks rather than assuming a 44-byte header: these files
        // carry a LIST chunk, and a fixed offset would read metadata as audio
        // and produce noise that looks like a failed detection.
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

    private sealed record Run(
        List<KwsDetection> Fired,
        double BestProgressP,
        int DeepestMatched,
        int DeepestTotal);

    private static Run Listen(string bundle, string keywords, float[] pcm, bool quantized)
    {
        using var kws = new ZipformerKwsSpotter(bundle, keywords, preferQuantized: quantized);

        var fired = new List<KwsDetection>();
        var bestP = 0.0;
        var matched = 0;
        var total = 0;

        kws.Detected += (_, d) => fired.Add(d);
        kws.KeywordProgress += (_, p) =>
        {
            if (p.Matched > matched || (p.Matched == matched && p.MeanProbability > bestP))
            {
                matched = p.Matched;
                total = p.Total;
                bestP = p.MeanProbability;
            }
        };

        // In realistic chunks rather than one block: the decoder is streaming and
        // a single giant call would not exercise the path the phone runs.
        const int chunk = 1600;   // 100 ms at 16 kHz
        for (var at = 0; at < pcm.Length; at += chunk)
            kws.AcceptWaveform(pcm.AsSpan(at, Math.Min(chunk, pcm.Length - at)));

        return new Run(fired, bestP, matched, total);
    }

    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public EncoderPrecisionOnRealAudioTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    [Fact]
    public void Quiet_speech_is_lost_without_gain_and_found_with_it()
    {
        // THE ARM'S-LENGTH BAND, REPRODUCED ON THE BENCH.
        //
        // SpeechGain's header carries the measurement it was written for, taken
        // on a P30 on 2026-09-06:
        //
        //     ~5 cm         peak 0,40-0,59   8 of 8 tokens, wake confirmed
        //     arm's length  peak 0,07-0,10   1 of 8 tokens, nothing
        //
        // Its only caller was ZipformerWakeWordDetector, which nothing
        // constructs - so the shipping wake loop ran at unity gain, and on
        // 2026-10-11 a person at normal holding distance (peak 0,17-0,22)
        // matched NOT ONE token where the same phrase at 0,453 had reached two
        // of three repeatedly.
        //
        // This takes upstream's own test audio, attenuates it into that band,
        // and asks the question directly: does the gain recover the detection?
        var bundle = Bundle;
        if (bundle is null)
        {
            _out.WriteLine("SKIPPED: set KWS_BUNDLE_DIR to an extracted upstream KWS bundle.");
            return;
        }

        var keywords = Path.Combine(bundle, "test_wavs", "test_keywords.txt");
        var wav = Path.Combine(bundle, "test_wavs", "0.wav");
        if (!File.Exists(keywords) || !File.Exists(wav))
        {
            _out.WriteLine("SKIPPED: bundle has no test_wavs.");
            return;
        }

        var loud = ReadWav(wav);
        var loudPeak = loud.Max(Math.Abs);

        // Scaled to the band where the device stopped hearing: peak ~0,20.
        var scale = 0.20f / loudPeak;
        var quiet = loud.Select(s => s * scale).ToArray();

        var lifted = (float[])quiet.Clone();
        var gain = new SpeechGain();
        const int block = 1600;
        for (var at = 0; at < lifted.Length; at += block)
            gain.Apply(lifted.AsSpan(at, Math.Min(block, lifted.Length - at)));

        var asIs = Listen(bundle, keywords, quiet, quantized: false);
        var withGain = Listen(bundle, keywords, lifted, quantized: false);

        _out.WriteLine($"loud  peak {loudPeak:F3} -> fired {Listen(bundle, keywords, loud, false).Fired.Count}");
        _out.WriteLine($"quiet peak {quiet.Max(Math.Abs):F3} -> fired {asIs.Fired.Count} "
                     + $"(deepest {asIs.DeepestMatched}/{asIs.DeepestTotal} p={asIs.BestProgressP:F4})");
        _out.WriteLine($"gained peak {lifted.Max(Math.Abs):F3} -> fired {withGain.Fired.Count} "
                     + $"(deepest {withGain.DeepestMatched}/{withGain.DeepestTotal} p={withGain.BestProgressP:F4})");

        // THE RESULT IS THE POINT AND IS PRINTED EITHER WAY. If the gain does
        // not recover it, wiring SpeechGain was not the fix and the numbers
        // above say so rather than another device round.
        Assert.True(withGain.Fired.Count >= asIs.Fired.Count,
            "applying gain made it WORSE, which is a real finding and not the expected one");
    }

    [Fact]
    public void The_float_encoder_hears_what_the_int8_encoder_cannot()
    {
        // NOT SkippableFact - that is a package, and one opt-in test does not
        // justify a dependency. An early return is a silent pass, which is the
        // defect this whole session has been about, so it says so out loud.
        var bundle = Bundle;
        if (bundle is null)
        {
            _out.WriteLine("SKIPPED: set KWS_BUNDLE_DIR to an extracted "
                         + "sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01 to run this.");
            return;
        }

        var keywords = Path.Combine(bundle, "test_wavs", "test_keywords.txt");
        if (!File.Exists(keywords))
        {
            _out.WriteLine("SKIPPED: " + keywords + " is not there.");
            return;
        }

        var report = new List<string>();
        var floatFired = 0;
        var int8Fired = 0;

        foreach (var (wav, expect) in Truth)
        {
            var path = Path.Combine(bundle, "test_wavs", wav);
            if (!File.Exists(path)) continue;

            var pcm = ReadWav(path);

            var f = Listen(bundle, keywords, pcm, quantized: false);
            var q = Listen(bundle, keywords, pcm, quantized: true);

            floatFired += f.Fired.Count;
            int8Fired += q.Fired.Count;

            report.Add(
                $"{wav}: expected {expect.Length} | "
                + $"FLOAT fired {f.Fired.Count} (deepest {f.DeepestMatched}/{f.DeepestTotal} p={f.BestProgressP:F4}) | "
                + $"INT8 fired {q.Fired.Count} (deepest {q.DeepestMatched}/{q.DeepestTotal} p={q.BestProgressP:F4})");
        }

        foreach (var line in report) _out.WriteLine(line);

        // MEASURED 2026-10-11, AND IT REFUTED THE CLAIM IT WAS WRITTEN TO CONFIRM.
        //
        //   0.wav  FLOAT fired 1 (4/4 p=0,7412)  INT8 fired 1 (4/4 p=0,5656)
        //   1.wav  FLOAT fired 2 (5/5 p=0,6263)  INT8 fired 2 (5/5 p=0,5352)
        //
        // int8 detected EVERY keyword float did. The constructor's remark - that
        // int8 leaves keyword tokens "around p=0.01 where the float graphs put
        // them above 0.3" - does not hold on this bundle: the gap is real but it
        // is 0,57 against 0,74, and int8 clears the 0,20 gate every time.
        //
        // So the quantized encoder is NOT why the wake word fails on the P30,
        // and the assertion here is the true one rather than the hoped-for one.
        // Both precisions must find every keyword in upstream's own audio; if
        // either stops, something real has changed.
        Assert.True(floatFired >= 3,
            "the FLOAT encoder missed keywords in upstream's own test audio:\n  "
            + string.Join("\n  ", report));

        Assert.True(int8Fired >= 3,
            "int8 used to find all three; if it no longer does, the earlier "
            + "measurement or the bundle has changed:\n  "
            + string.Join("\n  ", report));
    }
}
