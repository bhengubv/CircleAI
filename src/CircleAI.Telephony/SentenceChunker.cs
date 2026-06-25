// SentenceChunker.cs
//
// (3.3.0) Stream-friendly sentence chunker. Accepts streamed LLM
// tokens and emits whole sentences as soon as they're complete, so
// TTS can speak them out before the full response finishes — cuts
// time-to-first-audio dramatically.

using System;
using System.Collections.Generic;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) Streaming sentence chunker.</summary>
public sealed class SentenceChunker
{
    private static readonly char[] TerminalPunctuation = { '.', '!', '?', '。', '！', '？' };
    private readonly System.Text.StringBuilder _buffer = new();
    private readonly object _gate = new();
    private readonly int _minSentenceLength;

    /// <param name="minSentenceLength">Sentences below this character count are buffered with the next one (avoids "1." / "Mr." splits).</param>
    public SentenceChunker(int minSentenceLength = 4)
    {
        _minSentenceLength = minSentenceLength;
    }

    /// <summary>(3.3.0) Push a token; receive any complete sentences ready to emit.</summary>
    public IEnumerable<string> PushToken(string token)
    {
        if (string.IsNullOrEmpty(token)) yield break;
        List<string>? ready = null;
        lock (_gate)
        {
            _buffer.Append(token);
            while (true)
            {
                var (chunk, kept) = ExtractNext(_buffer.ToString());
                if (chunk is null) break;
                _buffer.Clear();
                _buffer.Append(kept);
                (ready ??= new()).Add(chunk);
            }
        }
        if (ready is not null)
        {
            foreach (var s in ready) yield return s;
        }
    }

    /// <summary>(3.3.0) Flush whatever's buffered as a final fragment, regardless of punctuation.</summary>
    public string Flush()
    {
        lock (_gate)
        {
            var s = _buffer.ToString();
            _buffer.Clear();
            return s;
        }
    }

    private (string? Chunk, string Kept) ExtractNext(string buffer)
    {
        int searchFrom = 0;
        while (searchFrom < buffer.Length)
        {
            var idx = buffer.IndexOfAny(TerminalPunctuation, searchFrom);
            if (idx < 0) return (null, buffer);

            // Consume any trailing whitespace + closing quotes after the punctuation.
            int end = idx + 1;
            while (end < buffer.Length && (char.IsWhiteSpace(buffer[end]) || buffer[end] == '"' || buffer[end] == '\'' || buffer[end] == ')'))
            {
                end++;
            }

            var candidate = buffer[..end].Trim();
            if (candidate.Length >= _minSentenceLength)
            {
                return (candidate, buffer[end..]);
            }
            // Too short — keep extending past this punctuation.
            searchFrom = end;
        }
        return (null, buffer);
    }
}
