// ClipCaptureTests.cs
//
// A recording stands in for the microphone: the same blocks in the same order, at the pace it was
// said, and then a silent room for as long as anybody listens.

using System.Diagnostics;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class ClipCaptureTests
{
    private const int Block = 3200;   // 100 ms of 16 kHz 16-bit mono

    [Fact]
    public async Task It_plays_the_clip_block_by_block_and_then_a_silent_room()
    {
        var pcm = new byte[Block + 100];
        Array.Fill(pcm, (byte)7);
        await using var clip = new ClipCapture(pcm);

        var blocks = new List<byte[]>();
        await foreach (var block in clip.CaptureAsync(CancellationToken.None))
        {
            blocks.Add(block.ToArray());
            if (blocks.Count == 3) break;
        }

        Assert.Equal(Block, blocks[0].Length);
        Assert.All(blocks[0], b => Assert.Equal(7, b));
        Assert.Equal(100, blocks[1].Length);
        Assert.Equal(Block, blocks[2].Length);
        Assert.All(blocks[2], b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task It_takes_as_long_as_the_recording_does()
    {
        await using var clip = new ClipCapture(new byte[Block * 3]);

        var clock = Stopwatch.StartNew();
        var seen = 0;
        await foreach (var _ in clip.CaptureAsync(CancellationToken.None))
            if (++seen == 3) break;

        Assert.True(clock.ElapsedMilliseconds >= 280, $"three blocks came in {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Stopping_it_ends_the_stream_rather_than_throwing()
    {
        await using var clip = new ClipCapture(new byte[Block]);
        using var stop = new CancellationTokenSource();

        // Stopped once a block has come, while it waits for the next. A stop set before the first block
        // raced that block's timer: on a busy box the two fired a second late and the stop could win.
        var seen = 0;
        await foreach (var _ in clip.CaptureAsync(stop.Token))
            if (++seen == 1) stop.CancelAfter(TimeSpan.FromMilliseconds(20));

        Assert.True(seen >= 1);
    }
}
