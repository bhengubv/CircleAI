// BrowserSubtitles.cs
//
// SRT and WebVTT, written where CircleAI.Voice cannot reach.
//
// A SECOND OWNER, DELIBERATELY, AND PINNED SO IT CANNOT DRIFT. CircleAI.Voice's
// Subtitles is the real owner of these two formats, and a WASM head cannot load
// that assembly - it drags whisper, ONNX and a native library into a browser
// tab. So the choice is between a browser that cannot turn a transcript into a
// subtitle file at all, and a second copy of thirty lines of formatting.
//
// The copy wins ONLY because SubtitleParityTests renders the same transcript
// through both and asserts the strings are identical, character for character.
// Without that test this file would be exactly the anti-pattern CLAUDE.md warns
// about - one fact with two owners always ends up with two answers - and the
// answer it got wrong would be invisible, because a player handed a subtitle
// file with the wrong punctuation shows nothing and reports nothing.
//
// If this ever needs a third behaviour, move the formatting into
// CircleAI.Assistant, which both sides already reference, rather than adding a
// third copy.

using System.Globalization;
using System.Text;
using CircleAI.Assistant;

namespace CircleAI.Samples.Web.Client.Services;

/// <summary>Renders a transcript as a subtitle file, in the browser.</summary>
internal static class BrowserSubtitles
{
    /// <summary>Shortest cue worth showing. Matches CircleAI.Voice.Subtitles.</summary>
    private static readonly TimeSpan ShortestCue = TimeSpan.FromMilliseconds(80);

    internal static string Render(Transcript transcript, SubtitleFormat format)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        var sb = new StringBuilder();
        var vtt = format == SubtitleFormat.WebVtt;
        var mark = vtt ? '.' : ',';

        if (vtt) sb.Append("WEBVTT\n\n");

        var n = 0;
        foreach (var line in Usable(transcript.Lines))
        {
            n++;
            if (!vtt) sb.Append(n.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(Stamp(line.Start, mark)).Append(" --> ").Append(Stamp(line.End, mark)).Append('\n');
            sb.Append(string.IsNullOrWhiteSpace(line.Speaker) ? line.Text : $"{line.Speaker}: {line.Text}");
            sb.Append('\n').Append('\n');
        }

        return sb.ToString();
    }

    private static IEnumerable<TranscriptLine> Usable(IReadOnlyList<TranscriptLine> lines)
    {
        TranscriptLine? held = null;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            if (line.End <= line.Start) continue;
            if (line.Duration < ShortestCue) continue;

            if (held is not null)
            {
                if (line.Start < held.End) held = held with { End = line.Start };
                if (held.Duration >= ShortestCue) yield return held;
            }

            held = line;
        }

        if (held is not null) yield return held;
    }

    /// <summary>HH:MM:SS,mmm or HH:MM:SS.mmm, hours never rolling over at a day.</summary>
    private static string Stamp(TimeSpan t, char mark)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        var hours = (int)t.TotalHours;
        return string.Create(CultureInfo.InvariantCulture,
            $"{hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}{mark}{t.Milliseconds:D3}");
    }
}
