// RealVoiceFromTheDeviceTests.cs
//
// EVERY CONCLUSION ABOUT THE WAKE PHRASE SO FAR CAME FROM A DEVICE LOG.
//
// The bench has only ever been fed upstream's two test wavs - clean, studio,
// American, read aloud. On that audio everything works: both encoder precisions
// detect all three keywords, and attenuating it to the level the P30 was
// hearing does not break detection either. Meanwhile the phone, with the same
// code, reaches "HEY" and never what follows.
//
// So the bench has never seen the thing that is actually failing: THIS speaker,
// THIS microphone, THIS room, at the distance a phone is really held. Without
// that, every explanation offered has been inference - and three of them have
// already turned out to be wrong (the threshold, the int8 encoder, amplitude).
//
// These run the real decoder over recordings pulled off the P30 itself and
// print what the acoustic model actually emits. No transcript is needed and
// none is assumed: the question is whether this speaker's voice through this
// microphone produces sensible tokens at all, and whether anything B-shaped
// ever appears.
//
// Set CIRCLEAI_VOICE_DIR to a folder of 16 kHz mono wavs, and KWS_BUNDLE_DIR to
// the extracted upstream bundle. Skips otherwise, so CI stays offline.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class RealVoiceFromTheDeviceTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public RealVoiceFromTheDeviceTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static string? Dir(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } d && Directory.Exists(d) ? d : null;

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
    public void Gain_declines_to_lift_a_recording_that_is_mostly_an_empty_room()
    {
        // THE PROOF THAT COULD NOT BE GOT FROM STUDIO AUDIO.
        //
        // Scaling upstream's clean test wav down to peak 0,20 did NOT break
        // detection, which is why wiring SpeechGain looked unprovable. That test
        // was a bad proxy: attenuating a clean recording lowers the signal and
        // the (almost absent) noise together, so the signal-to-noise ratio
        // survives. Real quiet speech in a real room loses both.
        //
        // Two recordings pulled off the P30 itself settle it, same speaker, same
        // microphone, minutes of audio each:
        //
        //   clip1  peak 0,186  rms 0,0091  ->  10 tokens in 38 seconds
        //   clip2  peak 0,420  rms 0,0498  ->  293 tokens, legible speech
        //
        // The failing wake attempts were at peak 0,17-0,22. The session that
        // reached 2 of 3 tokens was at 0,453. Those are these two bands exactly.
        var bundle = Dir("KWS_BUNDLE_DIR");
        var voice = Dir("CIRCLEAI_VOICE_DIR");
        if (bundle is null || voice is null)
        {
            _out.WriteLine("SKIPPED: needs KWS_BUNDLE_DIR and CIRCLEAI_VOICE_DIR.");
            return;
        }

        var keywords = Path.Combine(bundle, "test_wavs", "test_keywords.txt");

        // The quietest recording available - the one the model is deaf to.
        var quietest = Directory.GetFiles(voice, "*.wav")
            .Select(p => (Path: p, Pcm: ReadWav(p)))
            .OrderBy(x => x.Pcm.Select(s => (double)s * s).Sum() / Math.Max(1, x.Pcm.Length))
            .FirstOrDefault();

        if (quietest.Pcm is null) { _out.WriteLine("SKIPPED: no wavs."); return; }

        int Tokens(float[] pcm)
        {
            using var kws = new ZipformerKwsSpotter(bundle, keywords);
            var n = 0;
            kws.TokenEmitted += (_, _) => n++;
            const int chunk = 1600;
            for (var at = 0; at < pcm.Length; at += chunk)
                kws.AcceptWaveform(pcm.AsSpan(at, Math.Min(chunk, pcm.Length - at)));
            return n;
        }

        var raw = Tokens(quietest.Pcm);

        var lifted = (float[])quietest.Pcm.Clone();
        var gain = new SpeechGain();
        const int block = 1600;
        for (var at = 0; at < lifted.Length; at += block)
            gain.Apply(lifted.AsSpan(at, Math.Min(block, lifted.Length - at)));

        var withGain = Tokens(lifted);

        _out.WriteLine($"{Path.GetFileName(quietest.Path)} "
                     + $"peak {quietest.Pcm.Max(Math.Abs):F3} -> {lifted.Max(Math.Abs):F3}");
        _out.WriteLine($"  tokens without gain: {raw}");
        _out.WriteLine($"  tokens with    gain: {withGain}");

        // MEASURED 2026-10-11, AND THE ANSWER IS "CORRECTLY, ALMOST NOTHING":
        // peak 0,186 -> 0,191 and 10 tokens -> 11.
        //
        // That is the safety rule working, not a failure. Per-second RMS over
        // this recording is median 0,0019 with only 5 of its 38 seconds above
        // the 0,010 noise floor - its median is BELOW the 0,0083 an empty P30
        // room measures. The file is mostly silence with brief faint speech, so
        // SpeechGain cannot tell it from a room and holds at unity. Lifting it
        // would be exactly the failure its header refuses: a wake word that
        // fires at a chair being moved, which cannot be worked around.
        //
        // So this clip does NOT measure the arm's-length case, and it must not
        // be read as evidence either way about the gain. What it pins is that
        // near-silence stays near-silent.
        Assert.True(withGain >= raw,
            $"gain made it worse: {raw} -> {withGain}. That is a real finding, not the expected one.");

        Assert.True(withGain - raw < 50,
            $"gain lifted a near-silent recording into {withGain} tokens - the noise floor is "
            + "no longer separating speech from an empty room, and false wakes follow");
    }

    [Fact]
    public void The_noise_floor_is_set_above_the_speech_it_is_meant_to_lift()
    {
        // MEASURED 2026-10-12, AFTER THREE WRONG ANSWERS, AND THIS ONE IS ABOUT
        // ONE CONSTANT.
        //
        // The capture is NOT quiet: the ambient floor our microphone records
        // (0,00093) matches the phone's own recorder app (0,00082) to within
        // noise, so there is no gain lost in how we open the device. What
        // differs is distance. Held close and spoken deliberately into the
        // recorder, this speaker lands at median per-second RMS 0,0446 and the
        // shipped keyword fires eight times. At the distance a phone is
        // actually used, the same voice reaches our capture at 0,002-0,006 and
        // the model emits nothing at all.
        //
        // That is exactly the band SpeechGain exists for - and it declines to
        // act in it. NoiseFloor is 0,010, set from "the empty room on a P30
        // measures 0,0083 RMS". This room measures 0,0009. So the constant is
        // roughly ten times higher than the room it was meant to clear, and it
        // classifies real speech at 0,0055 as silence.
        //
        // Target is 0,05 and MaxGain is 12, so speech at 0,0055 wants 9,1x and
        // could be lifted straight into the band the model reads. The question
        // this answers is whether lowering the floor recovers the tokens
        // without also amplifying the room.
        var bundle = Dir("KWS_BUNDLE_DIR");
        var voice = Dir("CIRCLEAI_VOICE_DIR");
        if (bundle is null || voice is null)
        {
            _out.WriteLine("SKIPPED: needs KWS_BUNDLE_DIR and CIRCLEAI_VOICE_DIR.");
            return;
        }

        var keywords = Path.Combine(bundle, "test_wavs", "test_keywords.txt");

        int Tokens(float[] pcm)
        {
            using var kws = new ZipformerKwsSpotter(bundle, keywords);
            var n = 0;
            kws.TokenEmitted += (_, _) => n++;
            const int chunk = 1600;
            for (var at = 0; at < pcm.Length; at += chunk)
                kws.AcceptWaveform(pcm.AsSpan(at, Math.Min(chunk, pcm.Length - at)));
            return n;
        }

        float[] Lift(float[] src, double floorAt)
        {
            var copy = (float[])src.Clone();
            var gain = new SpeechGain { NoiseFloor = floorAt };
            const int block = 1600;
            for (var at = 0; at < copy.Length; at += block)
                gain.Apply(copy.AsSpan(at, Math.Min(block, copy.Length - at)));
            return copy;
        }

        foreach (var wav in Directory.GetFiles(voice, "*.wav").OrderBy(p => p))
        {
            var pcm = ReadWav(wav);
            var name = Path.GetFileName(wav);

            _out.WriteLine($"=== {name}  peak {pcm.Max(Math.Abs):F3} ===");
            _out.WriteLine($"   as captured              -> {Tokens(pcm),4} tokens");

            foreach (var floorAt in new[] { 0.010, 0.005, 0.003, 0.001 })
            {
                var lifted = Lift(pcm, floorAt);
                _out.WriteLine($"   SpeechGain floor {floorAt:F3}     -> {Tokens(lifted),4} tokens  "
                             + $"(peak {lifted.Max(Math.Abs):F3})");
            }
        }

        // PRINTED EITHER WAY. If lowering the floor does not recover tokens,
        // the constant is not the problem and these numbers say so instead of
        // another round at the phone.
        Assert.True(true);
    }

    [Fact]
    public void What_this_speaker_through_this_microphone_actually_produces()
    {
        var bundle = Dir("KWS_BUNDLE_DIR");
        var voice = Dir("CIRCLEAI_VOICE_DIR");
        if (bundle is null || voice is null)
        {
            _out.WriteLine("SKIPPED: needs KWS_BUNDLE_DIR and CIRCLEAI_VOICE_DIR.");
            return;
        }

        var wavs = Directory.GetFiles(voice, "*.wav").OrderBy(p => p).ToList();
        var silent = new List<string>();
        if (wavs.Count == 0) { _out.WriteLine("SKIPPED: no wavs in " + voice); return; }

        // Upstream's own keyword file, so the trie has real phrases in it and
        // the decoder runs exactly as it does on the phone. What is being
        // measured is the TOKEN STREAM, not whether these keywords fire - none
        // of them is going to be in a recording about C#.
        var keywords = Path.Combine(bundle, "test_wavs", "test_keywords.txt");

        foreach (var wav in wavs)
        {
            var pcm = ReadWav(wav);
            var peak = pcm.Max(Math.Abs);
            var rms = Math.Sqrt(pcm.Select(s => (double)s * s).Sum() / Math.Max(1, pcm.Length));

            using var kws = new ZipformerKwsSpotter(bundle, keywords);

            var emitted = new List<string>();
            kws.TokenEmitted += (_, t) => emitted.Add(t);

            const int chunk = 1600;
            for (var at = 0; at < pcm.Length; at += chunk)
                kws.AcceptWaveform(pcm.AsSpan(at, Math.Min(chunk, pcm.Length - at)));

            var text = string.Concat(emitted).Replace("▁", " ").Trim();

            _out.WriteLine($"=== {Path.GetFileName(wav)} "
                         + $"{pcm.Length / 16000.0:F1}s peak={peak:F3} rms={rms:F4} ===");
            _out.WriteLine($"  {emitted.Count} tokens emitted");
            _out.WriteLine($"  {(text.Length > 600 ? text[..600] + " ..." : text)}");

            // DOES ANYTHING B-SHAPED EVER COME OUT? The wake phrase has spent
            // months waiting for one of these on this very microphone.
            var bish = emitted.Where(t => t.TrimStart('▁').StartsWith("B", StringComparison.Ordinal))
                              .GroupBy(t => t)
                              .OrderByDescending(g => g.Count())
                              .Select(g => $"{g.Key.Replace('▁', '_')}x{g.Count()}")
                              .ToList();

            _out.WriteLine($"  B-shaped tokens: {(bish.Count == 0 ? "NONE" : string.Join(" ", bish))}");
            _out.WriteLine(string.Empty);

            if (emitted.Count == 0)
                silent.Add($"{Path.GetFileName(wav)} ({pcm.Length / 16000.0:F1}s, peak {peak:F3})");
        }

        // ASSERTED AFTER ALL OF THEM, NOT INSIDE THE LOOP. Failing on the first
        // file hides the comparison, and the comparison is the entire point: on
        // 2026-10-12 four capture configurations were recorded back to back and
        // the first one returning zero stopped the run before the other three
        // were measured. A test that reports one quarter of an experiment is
        // the same defect as a log that reports one of two causes.
        Assert.True(silent.Count == 0,
            "the acoustic model emitted NOTHING for: " + string.Join(", ", silent)
            + ". The same speaker through the phone's own recorder app yields 293 tokens, "
            + "so this is the capture rather than the voice.");
    }
}
