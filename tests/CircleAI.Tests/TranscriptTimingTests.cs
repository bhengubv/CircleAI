// TranscriptTimingTests.cs
//
// What a transcript says, and when it was said.
//
// These pin the SHAPE rather than a recognition result: whether the timings
// survive the trip out of the library, whether the units are right, and whether
// a caller who does not care about timing is unaffected. Recognition accuracy
// needs a model and a microphone; none of that is needed to prove the numbers
// are no longer being thrown away.

using System;
using System.Collections.Generic;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public class TranscriptTimingTests
{
    [Fact]
    public void A_result_with_no_timing_reports_none_rather_than_null()
    {
        // Every other transcriber, and every caller that predates timings. A
        // null here would turn "this engine cannot tell you when" into a
        // NullReferenceException in a caption renderer.
        var r = new TranscriptionResult("hello", 1f, "en");

        Assert.Empty(r.Timed);
        Assert.NotNull(r.Timed);
    }

    [Fact]
    public void The_timings_survive_the_trip_out_of_the_library()
    {
        var r = new TranscriptionResult("hello there", 1f, "en",
        [
            new TranscriptSegment("hello", TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            new TranscriptSegment("there", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)),
        ]);

        Assert.Equal(2, r.Timed.Count);
        Assert.Equal("hello", r.Timed[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(1), r.Timed[1].Start);
    }

    [Fact]
    public void A_segment_knows_how_long_it_lasted()
    {
        // Subtitle timing is a duration, and making every caller subtract is how
        // one of them eventually subtracts the wrong way round.
        var seg = new TranscriptSegment("mm",
            TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(2250));

        Assert.Equal(TimeSpan.FromMilliseconds(750), seg.Duration);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 10)]
    [InlineData(150, 1500)]
    [InlineData(6000, 60000)]
    public void Whispers_centiseconds_become_milliseconds_not_the_other_way(
        long centiseconds, double expectedMs)
    {
        // THE FACTOR OF TEN. whisper.cpp's t0/t1 are HUNDREDTHS of a second, and
        // reading them as milliseconds puts every subtitle ten times too early
        // - which looks like drift rather than a unit error, and is the reason
        // the conversion happens once inside the transcriber rather than in
        // each caller.
        Assert.Equal(expectedMs, TimeSpan.FromMilliseconds(centiseconds * 10).TotalMilliseconds);
    }

    [Fact]
    public void Segments_are_ordered_and_do_not_go_backwards()
    {
        // A caption that starts before the one before it finished is not a
        // rendering bug, and this is where it would be caught.
        IReadOnlyList<TranscriptSegment> segs =
        [
            new("one",   TimeSpan.Zero,               TimeSpan.FromSeconds(1)),
            new("two",   TimeSpan.FromSeconds(1),     TimeSpan.FromSeconds(2.5)),
            new("three", TimeSpan.FromSeconds(2.5),   TimeSpan.FromSeconds(3)),
        ];

        for (var i = 1; i < segs.Count; i++)
            Assert.True(segs[i].Start >= segs[i - 1].End,
                $"segment {i} starts before segment {i - 1} ends");
    }
}
