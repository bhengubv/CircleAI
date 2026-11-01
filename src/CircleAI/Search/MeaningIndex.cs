// MeaningIndex.cs
//
// Lines of text kept as TurboQuant-compressed vectors, so a search can find a line by what
// it means rather than by the words it shares with the query (OPEN-GAPS A7 and A12).
//
// THE ENCODER IS WHEREVER THE MODEL IS. On the phone that is the service, so the app's
// encoder is a call across the link; on a desktop it is the embedder in the same process.
// This class only ever sees IEmbeddingEncoder, so it is the same code in both places.
//
// INDEXED AS IT IS SEARCHED, WITHIN A BUDGET. A line is embedded the first time a search
// meets it and kept on disk after that, keyed by its text as well as where it sits, so an
// edited line is embedded again and an unchanged one never is. Each search spends at most
// Budget on lines it has not seen; what is left waits for the next search rather than
// holding this one up - a search screen that says "Looking..." for a minute is the failure
// this repo has already measured once.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Embeddings.Local;

namespace CircleAI.Search;

/// <summary>Lines kept as compressed vectors, searched by meaning.</summary>
public sealed class MeaningIndex : IAsyncDisposable
{
    // Between a line's key and the hash of its text in an entry's id.
    private const char Separator = '\u001F';

    private readonly IEmbeddingEncoder _encoder;
    private readonly string? _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InMemoryEmbeddingStore? _store;

    /// <param name="encoder">What turns a line into a vector.</param>
    /// <param name="path">Where the vectors are kept between runs; null keeps them in memory only.</param>
    public MeaningIndex(IEmbeddingEncoder encoder, string? path = null)
    {
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _path = path;
    }

    /// <summary>The most one search spends embedding lines it has not seen.</summary>
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Makes the index hold exactly these lines: embeds the new or changed ones within
    /// <see cref="Budget"/>, and lets go of lines that are no longer there.
    /// </summary>
    /// <returns>How many lines are still waiting to be embedded.</returns>
    public async Task<int> IndexAsync(IEnumerable<(string Key, string Text)> lines, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var store = await StoreAsync(ct).ConfigureAwait(false);
            var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, text) in lines)
                if (!string.IsNullOrWhiteSpace(text)) wanted[Id(key, text)] = text;

            var changed = false;
            foreach (var held in store.Ids)
            {
                if (wanted.ContainsKey(held)) continue;
                await store.RemoveAsync(held, ct).ConfigureAwait(false);
                changed = true;
            }

            var heldNow = store.Ids.ToHashSet(StringComparer.Ordinal);
            var missing = wanted.Where(w => !heldNow.Contains(w.Key)).ToList();
            var clock = Stopwatch.StartNew();
            var left = missing.Count;
            foreach (var (id, text) in missing)
            {
                if (clock.Elapsed >= Budget) break;
                ct.ThrowIfCancellationRequested();
                await store.AddAsync(new EmbeddingDocument(id, text), ct).ConfigureAwait(false);
                left--;
                changed = true;
            }

            if (changed && _path is not null) await store.SaveAsync(_path, ct).ConfigureAwait(false);
            return left;
        }
        finally { _gate.Release(); }
    }

    /// <summary>The keys of the lines nearest in meaning to <paramref name="query"/>, best first.</summary>
    public async Task<IReadOnlyList<string>> SearchAsync(string query, int take, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || take <= 0) return [];
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var store = await StoreAsync(ct).ConfigureAwait(false);
            if (store.Count == 0) return [];
            var hits = await store.SearchAsync(query, Math.Min(take, store.Count), ct).ConfigureAwait(false);
            return [.. hits.Select(h => KeyOf(h.Document.Id))];
        }
        finally { _gate.Release(); }
    }

    private async Task<InMemoryEmbeddingStore> StoreAsync(CancellationToken ct)
    {
        if (_store is not null) return _store;
        var store = new InMemoryEmbeddingStore(_encoder);
        if (_path is not null && File.Exists(_path))
        {
            // A file this build cannot read - another model's width, a damaged write - is
            // started again rather than failing every search after it.
            try { await store.LoadAsync(_path, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { }
        }
        return _store = store;
    }

    /// <summary>An entry's id: the line's key and a hash of its text, so an edit re-embeds it.</summary>
    internal static string Id(string key, string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return key + Separator + Convert.ToHexString(hash, 0, 8);
    }

    private static string KeyOf(string id)
    {
        var at = id.LastIndexOf(Separator);
        return at < 0 ? id : id[..at];
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_store is not null) await _store.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
