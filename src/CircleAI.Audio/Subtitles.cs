// Subtitles.cs
//
// Timed segments become a subtitle file.
//
// This is what the timings are FOR. Carrying Start and End out of the
// transcriber and then leaving every caller to format them is most of the way
// to nowhere: the two formats anybody actually uses are thirty lines each, and
// written per-caller they would be thirty lines each, wrongly, several times.
//
// SRT AND VTT DIFFER BY MORE THAN A HEADER, which is exactly why hand-rolling
// one from the other goes wrong. SRT separates seconds from milliseconds with a
// COMMA and numbers every cue; WebVTT uses a FULL STOP, requires the WEBVTT
// line, and numbers nothing. A player handed the wrong separator does not
// complain - it shows no subtitles at all, and the file looks fine.

using System.Globalization;
using System.Text;

namespace CircleAI.Voice;

/// <summary>Writes timed segments as SubRip (.srt) or WebVTT (.vtt).</summary>
public static class Subtitles
{
    /// <summary>
    /// Shortest cue worth showing.
    /// </summary>
    /// <remarks>
    /// Whisper emits the odd near-zero segment on a breath or a click, and a cue
    /// that appears and vanishes inside a frame is a flicker rather than a
    /// subtitle. Skipped rather than padded: stretching it would push it over
    /// the cue after it.
    /// </remarks>
    public static readonly TimeSpan ShortestCue = TimeSpan.FromMilliseconds(80);

    /// <summary>Render as SubRip (.srt).</summary>
    public static string ToSrt(IReadOnlyList<TranscriptSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var sb = new StringBuilder();
        var n = 0;

        foreach (var seg in Usable(segments))
        {
            n++;
            sb.Append(n.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(Stamp(seg.Start, ',')).Append(" --> ").Append(Stamp(seg.End, ',')).Append('\n');
            sb.Append(Line(seg)).Append('\n').Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Render as WebVTT (.vtt).</summary>
    public static string ToVtt(IReadOnlyList<TranscriptSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        // THE HEADER IS NOT OPTIONAL AND IS NOT DECORATION. A .vtt without the
        // WEBVTT line is rejected outright by every browser, silently, and the
        // video simply plays with no captions.
        var sb = new StringBuilder("WEBVTT\n\n");

        foreach (var seg in Usable(segments))
        {
            sb.Append(Stamp(seg.Start, '.')).Append(" --> ").Append(Stamp(seg.End, '.')).Append('\n');
            sb.Append(Line(seg)).Append('\n').Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>The cue text, with the speaker named when one is known.</summary>
    /// <remarks>
    /// A DIARISED TRANSCRIPT IS THE ONE PEOPLE ACTUALLY WANT. "Who said what" is
    /// the difference between a wall of text and minutes of a meeting, and both
    /// formats carry it the same way - as part of the cue, because neither has a
    /// speaker field of its own. Prefixed rather than put on a line above, so a
    /// player that shows one line at a time still shows who is talking.
    /// <para>
    /// Nothing is added when the speaker is unknown. "Unknown: " is noise, and
    /// worse than noise - it reads as another participant.
    /// </para>
    /// </remarks>
    private static string Line(TranscriptSegment seg)
        => string.IsNullOrWhiteSpace(seg.Speaker) ? seg.Text : $"{seg.Speaker}: {seg.Text}";

    /// <summary>The segments worth writing, in order, without overlaps.</summary>
    /// <remarks>
    /// A CUE THAT STARTS BEFORE THE ONE BEFORE IT ENDS IS A RENDERING BUG the
    /// player cannot fix. Chunked transcription can produce one where the
    /// overlap merge let a straddling segment through, and a player shows both
    /// at once - two lines of text on top of each other. Trimming the earlier
    /// cue short is the only repair that keeps every word.
    /// </remarks>
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
                if (seg.Start < held.End)
                    held = held with { End = seg.Start };

                if (held.Duration >= ShortestCue) yield return held;
            }

            held = seg;
        }

        if (held is not null) yield return held;
    }

    /// <summary>
    /// <c>HH:MM:SS,mmm</c> for SRT or <c>HH:MM:SS.mmm</c> for WebVTT.
    /// </summary>
    /// <remarks>
    /// Hours are always written, even when zero. Both formats permit it and some
    /// players require it, and a file that works everywhere beats one that is
    /// three characters shorter.
    /// <para>
    /// InvariantCulture throughout. On a phone set to most of Europe the default
    /// decimal separator is already a comma, so a format built from the current
    /// culture produces a VTT with SRT punctuation on those devices and only
    /// those - the kind of fault that never reproduces where it is being looked
    /// for.
    /// </para>
    /// </remarks>
    internal static string Stamp(TimeSpan t, char decimalMark)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;

        var hours = (int)t.TotalHours;
        return string.Create(CultureInfo.InvariantCulture,
            $"{hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}{decimalMark}{t.Milliseconds:D3}");
    }
}
