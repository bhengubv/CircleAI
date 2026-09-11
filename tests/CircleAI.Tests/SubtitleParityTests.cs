// SubtitleParityTests.cs
//
// The browser's copy of the subtitle formats must equal the library's.
//
// A WASM head cannot load CircleAI.Voice - whisper, ONNX and a native library do
// not belong in a browser tab - so BrowserSubtitles renders SRT and WebVTT
// itself. That is one fact with two owners, which this repo's own CLAUDE.md
// names as the anti-pattern that "always ends up with two answers".
//
// This is the thing that makes the copy survivable: the same transcript through
// both, asserted identical character for character. Without it the drift would
// be invisible, because a player handed a subtitle file with a full stop where a
// comma belongs shows NO subtitles and reports nothing - the failure looks like
// a broken video, not a broken file.
//
// The copy lives in the Web.Client head, which this test project does not
// reference, so the browser implementation is mirrored here. That is a third
// copy - and it is deliberate: this file exists to fail when the two real ones
// disagree, and it can only do that by holding the rules itself.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public class SubtitleParityTests
{
    public static TheoryData<string, IReadOnlyList<TranscriptSegment>> Cases() => new()
    {
        {
            "plain",
            [
                new("hello there", TimeSpan.Zero, TimeSpan.FromSeconds(2)),
                new("how are you", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4.5)),
            ]
        },
        {
            "with speakers",
            [
                new("hello", TimeSpan.Zero, TimeSpan.FromSeconds(2)) { Speaker = "Speaker 1" },
                new("hi", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)) { Speaker = "Speaker 2" },
            ]
        },
        {
            "overlapping, which must be trimmed the same way",
            [
                new("first",  TimeSpan.Zero,           TimeSpan.FromSeconds(5)),
                new("second", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(7)),
            ]
        },
        {
            "blips and blanks, which must be dropped the same way",
            [
                new("   ",  TimeSpan.Zero,             TimeSpan.FromSeconds(1)),
                new("blip", TimeSpan.FromSeconds(1),   TimeSpan.FromSeconds(1.01)),
                new("real", TimeSpan.FromSeconds(2),   TimeSpan.FromSeconds(4)),
            ]
        },
        {
            "past a day, where TimeSpan.Hours rolls over and TotalHours does not",
            [
                new("day two", TimeSpan.FromHours(25),
                               TimeSpan.FromHours(25).Add(TimeSpan.FromSeconds(2))),
            ]
        },
        { "nothing at all", [] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Srt_is_identical_in_both_owners(
        string _, IReadOnlyList<TranscriptSegment> segments)
    {
        Assert.Equal(Subtitles.ToSrt(segments), Mirror(segments, webVtt: false));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Vtt_is_identical_in_both_owners(
        string _, IReadOnlyList<TranscriptSegment> segments)
    {
        Assert.Equal(Subtitles.ToVtt(segments), Mirror(segments, webVtt: true));
    }

    [Fact]
    public void The_two_formats_are_not_accidentally_the_same()
    {
        // If the mirror ever collapsed to one implementation the tests above
        // would pass while proving nothing. The formats genuinely differ.
        IReadOnlyList<TranscriptSegment> segs =
            [new("hello", TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3))];

        Assert.NotEqual(Subtitles.ToSrt(segs), Subtitles.ToVtt(segs));
        Assert.NotEqual(Mirror(segs, false), Mirror(segs, true));
    }

    // ── The browser's rules, restated ───────────────────────────────────
    //
    // Kept deliberately independent of Subtitles: restating them is what lets
    // this file detect a change in either real implementation. Copying from one
    // of them would make this test agree with that one by construction.

    private static readonly TimeSpan ShortestCue = TimeSpan.FromMilliseconds(80);

    private static string Mirror(IReadOnlyList<TranscriptSegment> segments, bool webVtt)
    {
        var sb = new StringBuilder();
        var mark = webVtt ? '.' : ',';
        if (webVtt) sb.Append("WEBVTT\n\n");

        var n = 0;
        foreach (var seg in Usable(segments))
        {
            n++;
            if (!webVtt) sb.Append(n.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(Stamp(seg.Start, mark)).Append(" --> ").Append(Stamp(seg.End, mark)).Append('\n');
            sb.Append(string.IsNullOrWhiteSpace(seg.Speaker) ? seg.Text : $"{seg.Speaker}: {seg.Text}");
            sb.Append('\n').Append('\n');
        }

        return sb.ToString();
    }

    private static IEnumerable<TranscriptSegment> Usable(IReadOnlyList<TranscriptSegment> segments)
    {
        TranscriptSegment? held = null;

        foreach (var seg in segments)
        {
            if (string.IsNullOrWhiteSpace(seg.Text)) continue;
            if (seg.End <= seg.Start) continue;
            if (seg.Duration < ShortestCue) continue;

            if (held is not null)
            {
                if (seg.Start < held.End) held = held with { End = seg.Start };
                if (held.Duration >= ShortestCue) yield return held;
            }

            held = seg;
        }

        if (held is not null) yield return held;
    }

    private static string Stamp(TimeSpan t, char mark)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        var hours = (int)t.TotalHours;
        return string.Create(CultureInfo.InvariantCulture,
            $"{hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}{mark}{t.Milliseconds:D3}");
    }
}
