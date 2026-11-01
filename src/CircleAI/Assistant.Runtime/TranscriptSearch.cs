// TranscriptSearch.cs
//
// Finding a line in a kept recording - by its words always, and by its meaning when an
// embedding model is there to ask (OPEN-GAPS A7).
//
// ONE OWNER FOR BOTH SEARCH SCREENS. CircleAISession.SearchAsync searched transcripts with
// BM25 and the app's Find screen did not search them at all; both now call this, so a line
// one of them can find, the other can too.
//
// FUSED BY RANK, NOT BY SCORE. A BM25 score and a cosine are different units, so adding or
// comparing them is arithmetic that means nothing - the same argument CircleAISession.Merge
// makes about memory and transcripts. Reciprocal rank fusion asks only where each list put
// a line: a line both lists rank well rises, and a line only one list found still appears.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Search;

namespace CircleAI.Assistant;

/// <summary>Searches kept transcripts, line by line.</summary>
public static class TranscriptSearch
{
    /// <summary>Reciprocal rank fusion's constant - the value its authors found steady across tasks.</summary>
    public const int FusionK = 60;

    /// <summary>The lines that best answer <paramref name="query"/>, best first.</summary>
    /// <param name="kept">Where the transcripts are.</param>
    /// <param name="query">What was typed.</param>
    /// <param name="take">How many lines.</param>
    /// <param name="meaning">Lines by meaning, when an embedding model can be asked; null searches words only.</param>
    /// <param name="ct">Cancels the search.</param>
    public static async Task<IReadOnlyList<Found>> SearchAsync(
        IKeepsTranscripts kept, string query, int take,
        MeaningIndex? meaning = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(kept);
        if (string.IsNullOrWhiteSpace(query) || take <= 0) return [];

        var saved = await kept.AllAsync(ct).ConfigureAwait(false);
        var lines = Lines(saved);
        if (lines.Count == 0) return [];

        // ── By its words ────────────────────────────────────────────────
        // A LINE AT A TIME, NOT A TRANSCRIPT AT A TIME. Indexing a forty-minute meeting as one
        // document buries the sentence somebody is looking for in ten thousand words, and BM25
        // length normalisation cannot rescue a needle that size. A line is also what a result
        // should SHOW.
        var words = new LexicalIndex();
        foreach (var line in lines)
            words.Add(new SearchDocument(new SearchOrigin("transcript", line.Key), line.Text, line.When));
        var byWords = words.Search(query, take * 2).Select(h => h.Document.Origin.Id).ToList();

        // ── By its meaning ──────────────────────────────────────────────
        IReadOnlyList<string> byMeaning = [];
        if (meaning is not null)
        {
            try
            {
                await meaning.IndexAsync(lines.Select(l => (l.Key, l.Text)), ct).ConfigureAwait(false);
                byMeaning = await meaning.SearchAsync(query, take * 2, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // A meaning leg that cannot answer leaves the words, which still work.
            }
        }

        var byKey = lines.ToDictionary(l => l.Key, StringComparer.Ordinal);
        return [.. Fuse(byWords, byMeaning, take)
            .Where(byKey.ContainsKey)
            .Select(k => byKey[k])
            .Select(l => new Found("transcript", l.TranscriptId, l.Title, l.Text, l.When))];
    }

    /// <summary>
    /// Two ranked lists of keys as one: reciprocal rank fusion, ties to the first list.
    /// </summary>
    public static IReadOnlyList<string> Fuse(IReadOnlyList<string> first, IReadOnlyList<string> second, int take)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (take <= 0) return [];

        var score = new Dictionary<string, double>(StringComparer.Ordinal);
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        void Vote(IReadOnlyList<string> list)
        {
            for (var rank = 0; rank < list.Count; rank++)
            {
                var key = list[rank];
                score[key] = score.GetValueOrDefault(key) + 1.0 / (FusionK + rank + 1);
                order.TryAdd(key, order.Count);
            }
        }
        Vote(first);
        Vote(second);

        return [.. score.OrderByDescending(kv => kv.Value)
                        .ThenBy(kv => order[kv.Key])
                        .Take(take)
                        .Select(kv => kv.Key)];
    }

    /// <summary>Every line of every kept transcript, keyed by transcript and position.</summary>
    internal static List<Line> Lines(IReadOnlyList<SavedTranscript> saved)
    {
        var lines = new List<Line>();
        foreach (var t in saved)
        {
            if (t.Transcript.Lines.Count > 0)
            {
                for (var i = 0; i < t.Transcript.Lines.Count; i++)
                {
                    var line = t.Transcript.Lines[i];
                    var text = line.Speaker is null ? line.Text : $"{line.Speaker}: {line.Text}";
                    lines.Add(new Line($"{t.Id}#{i}", t.Id, t.Title, text, t.When));
                }
            }
            else if (!string.IsNullOrWhiteSpace(t.Transcript.Text))
            {
                // An engine that reported no timings still produced text.
                lines.Add(new Line($"{t.Id}#all", t.Id, t.Title, t.Transcript.Text, t.When));
            }
        }
        return lines;
    }

    internal sealed record Line(string Key, string TranscriptId, string Title, string Text, DateTimeOffset When);
}
