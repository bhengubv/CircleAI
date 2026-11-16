// TheWakeWordKeepsOnlyWhatItStillNeedsTests.cs
//
// THE WAKE WORD KEPT EVERYTHING IT HEARD. ZipformerKwsSpotter added every feature frame to a list and never dropped
// one, and its extractor kept every sample, so the Circle AI service grew about 5 MB a minute for as long as the
// phone listened - 356 MB to 621 MB in 53 minutes on the P30 on 2026-11-15, 1.05 GB the day before - until Huawei's
// iAware killed it as an abnormal process (am_kill iAwareK[abnormProc]), two hours into listening.
//
// The spotter's bookkeeping now lives in FeatureChunks, so these tests drive it the way the spotter does without a
// model: what the encoder is handed must be exactly what it was handed before, and what is held must not grow with
// the minutes.

using System;
using System.Collections.Generic;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public class TheWakeWordKeepsOnlyWhatItStillNeedsTests
{
    // The shapes the shipped zipformer bundles declare: 45 frames read per encoder call, 32 advanced.
    private const int Chunk = 45;
    private const int Advance = 32;
    private const int Block = 1600;      // 100 ms at 16 kHz, what the microphone hands over

    private static float[] Voiced(int samples, int seed = 11, int rate = 16_000)
    {
        var rng = new Random(seed);
        var x = new float[samples];
        for (var i = 0; i < samples; i++)
        {
            var t = (double)i / rate;
            var swell = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 2 * t);
            x[i] = (float)(swell * (0.3 * Math.Sin(2 * Math.PI * 180 * t) + 0.2 * Math.Sin(2 * Math.PI * 950 * t))
                           + 0.05 * (rng.NextDouble() * 2 - 1));
        }
        return x;
    }

    [Fact]
    public void The_encoder_reads_the_same_chunks_as_when_nothing_was_forgotten()
    {
        // THE OLD BOOKKEEPING IS THE REFERENCE: every frame by its number since the stream began, a chunk at 0, 32,
        // 64 ... - what the spotter fed its encoder on the phone, the day it was measured. Forgetting must change what
        // is held and nothing the model sees, or the wake word hears differently.
        var audio = Voiced(10 * 16_000);

        var kept = new KaldiFbank();
        var everyFrame = new List<float[]>();
        var cursor = 0;
        var expected = new List<float[][]>();

        var fbank = new KaldiFbank();
        var chunks = new FeatureChunks(fbank, Chunk, Advance);
        var read = new List<float[][]>();

        for (var at = 0; at < audio.Length; at += Block)
        {
            var block = audio.AsSpan(at, Math.Min(Block, audio.Length - at));

            kept.AcceptWaveform(block);
            for (var f = everyFrame.Count; f < kept.FramesReady; f++) everyFrame.Add(kept.GetFrame(f));
            for (; cursor + Chunk <= everyFrame.Count; cursor += Advance) expected.Add(everyFrame.GetRange(cursor, Chunk).ToArray());

            fbank.AcceptWaveform(block);
            chunks.TakeReady();
            while (chunks.HasChunk)
            {
                var chunk = new float[Chunk][];
                for (var t = 0; t < Chunk; t++) chunk[t] = chunks[t];
                read.Add(chunk);
                chunks.Advance();
            }
        }

        Assert.Equal(30, expected.Count);                    // 999 frames in 10 s: (999 - 45) / 32 + 1
        Assert.Equal(expected.Count, read.Count);
        for (var c = 0; c < expected.Count; c++)
            for (var t = 0; t < Chunk; t++)
                Assert.Equal(expected[c][t], read[c][t]);
    }

    [Fact]
    public void Minutes_of_listening_hold_no_more_than_a_second_does()
    {
        // Three minutes, the way the resident wake loop feeds it. Held between blocks: under one chunk of frames and
        // under one window of samples, whatever the minutes - where it was 100 frames and 16,000 samples more for
        // every second listened.
        var fbank = new KaldiFbank();
        var chunks = new FeatureChunks(fbank, Chunk, Advance);
        var block = Voiced(Block);
        var chunksRead = 0;
        var mostFrames = 0;
        var mostSamples = 0;
        for (var i = 0; i < 1800; i++)
        {
            fbank.AcceptWaveform(block);
            chunks.TakeReady();
            while (chunks.HasChunk)
            {
                _ = chunks[Chunk - 1];
                chunksRead++;
                chunks.Advance();
            }
            mostFrames = Math.Max(mostFrames, chunks.Held);
            mostSamples = Math.Max(mostSamples, fbank.SamplesHeld);
        }

        Assert.Equal(562, chunksRead);                       // 17,999 frames in 3 min: (17,999 - 45) / 32 + 1
        Assert.True(mostFrames < Chunk, $"held {mostFrames} frames between blocks; a chunk is {Chunk}");
        Assert.True(mostSamples < new KaldiFbankOptions().FrameLength,
            $"held {mostSamples} samples between blocks; the next window needs fewer than {new KaldiFbankOptions().FrameLength}");
    }

    [Fact]
    public void After_a_reset_the_first_chunk_is_the_new_streams_first_frames()
    {
        // The spotter's Reset clears the extractor and these frames together; the next chunk must start at the new
        // stream's first frame, not where the old stream's cursor had got to.
        var fbank = new KaldiFbank();
        var chunks = new FeatureChunks(fbank, Chunk, Advance);
        var before = Voiced(16_000, seed: 3);
        for (var at = 0; at < before.Length; at += Block)
        {
            fbank.AcceptWaveform(before.AsSpan(at, Block));
            chunks.TakeReady();
            while (chunks.HasChunk) chunks.Advance();
        }

        fbank.Reset();
        chunks.Clear();
        Assert.Equal(0, chunks.Held);

        var after = Voiced(16_000, seed: 5);
        var fresh = new KaldiFbank();
        fresh.AcceptWaveform(after);
        for (var at = 0; at < after.Length && !chunks.HasChunk; at += Block)
        {
            fbank.AcceptWaveform(after.AsSpan(at, Block));
            chunks.TakeReady();
        }

        Assert.True(chunks.HasChunk);
        for (var t = 0; t < Chunk; t++) Assert.Equal(fresh.GetFrame(t), chunks[t]);
    }

    [Fact]
    public void A_chunk_is_not_read_before_it_is_whole()
    {
        var fbank = new KaldiFbank();
        var chunks = new FeatureChunks(fbank, Chunk, Advance);
        fbank.AcceptWaveform(Voiced(Block));
        chunks.TakeReady();

        Assert.False(chunks.HasChunk);                       // 100 ms is 9 frames; a chunk is 45
        Assert.Throws<ArgumentOutOfRangeException>(() => chunks[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new FeatureChunks(fbank, Chunk, Chunk + 1));
    }
}
